using System.IO.Compression;
using System.Text;
using VspcAutotaskPlugin.Infrastructure;

namespace VspcAutotaskPlugin.Plugin;

/// <summary>
/// Endpoints the VSPC plugin host calls directly on the plugin service, as declared in
/// plugin.manifest.xml: health/readiness probes, log download, and pre-uninstall data purge.
/// </summary>
public static class PluginEndpoints
{
    public static void Map(WebApplication app)
    {
        // Health endpoints must return 200 with an empty body.
        app.MapGet("/api/v1/healthcheck", () => Results.Ok());
        app.MapGet("/api/v1/readiness", (Db db) => Results.Ok());

        app.MapGet("/api/v1/logs", DownloadLogsAsync);
        app.MapDelete("/api/v1/localdata", Purge);
    }

    /// <summary>Streams a ZIP with the last 30 days of the activity log (plus any file logs).</summary>
    private static async Task DownloadLogsAsync(HttpContext ctx, Db db, AppPaths paths)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/zip";
        ctx.Response.Headers.ContentDisposition = "attachment; filename=\"vspc-autotask-plugin-logs.zip\"";

        using var archive = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true);

        var since = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
        var rows = db.Query(
            "SELECT ts, level, area, message, detail FROM activity_log WHERE ts >= $since ORDER BY id",
            r => string.Join('\t',
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? "" : r.GetString(4).Replace('\n', ' ').Replace('\r', ' ')),
            new() { ["$since"] = since });

        var entry = archive.CreateEntry("activity-log.tsv");
        await using (var writer = new StreamWriter(entry.Open(), Encoding.UTF8))
        {
            await writer.WriteLineAsync("timestamp\tlevel\tarea\tmessage\tdetail");
            foreach (var row in rows)
                await writer.WriteLineAsync(row);
        }

        var logsDir = Path.Combine(paths.DataDir, "logs");
        if (Directory.Exists(logsDir))
        {
            foreach (var filePath in Directory.EnumerateFiles(logsDir, "*.log"))
            {
                if (File.GetLastWriteTimeUtc(filePath) < DateTime.UtcNow.AddDays(-30)) continue;
                try
                {
                    var fileEntry = archive.CreateEntry(Path.GetFileName(filePath));
                    await using var source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    await using var target = fileEntry.Open();
                    await source.CopyToAsync(target);
                }
                catch
                {
                    // Skip unreadable files; the archive stays valid.
                }
            }
        }
        await ctx.Response.Body.FlushAsync();
    }

    /// <summary>Wipes all plugin-generated data (called by VSPC before uninstall or on demand).</summary>
    private static IResult Purge(Db db, ActivityLog activity, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("PluginEndpoints");
        foreach (var table in new[]
        {
            "company_mappings", "service_mappings", "company_billing", "alarm_rules",
            "ticket_links", "billing_adjustments", "sync_state", "settings", "activity_log"
        })
        {
            try { db.Exec($"DELETE FROM {table}"); }
            catch (Exception ex) { logger.LogWarning(ex, "Purge of table {Table} failed", table); }
        }
        activity.Info("system", "Local plugin data purged on VSPC request");
        return Results.Ok();
    }
}
