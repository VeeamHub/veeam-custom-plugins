namespace VspcAutotaskPlugin.Infrastructure;

public sealed record ActivityEntry(long Id, string Ts, string Level, string Area, string Message, string? Detail);

/// <summary>
/// Persistent activity feed shown in the UI. Mirrors entries to the host logger.
/// Areas: connection, companies, billing, ticketing, system.
/// </summary>
public sealed class ActivityLog
{
    private readonly Db _db;
    private readonly ILogger<ActivityLog> _logger;

    public ActivityLog(Db db, ILogger<ActivityLog> logger)
    {
        _db = db;
        _logger = logger;
        // Keep the table bounded; retain the most recent 20k entries.
        _db.Exec("DELETE FROM activity_log WHERE id < (SELECT COALESCE(MAX(id),0) - 20000 FROM activity_log)");
    }

    public void Info(string area, string message, string? detail = null) => Write("info", area, message, detail);
    public void Warn(string area, string message, string? detail = null) => Write("warn", area, message, detail);
    public void Error(string area, string message, string? detail = null) => Write("error", area, message, detail);

    private void Write(string level, string area, string message, string? detail)
    {
        var logLevel = level switch { "error" => LogLevel.Error, "warn" => LogLevel.Warning, _ => LogLevel.Information };
        _logger.Log(logLevel, "[{Area}] {Message} {Detail}", area, message, detail ?? string.Empty);
        try
        {
            _db.Exec(
                "INSERT INTO activity_log (ts, level, area, message, detail) VALUES ($ts, $level, $area, $message, $detail)",
                new()
                {
                    ["$ts"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["$level"] = level,
                    ["$area"] = area,
                    ["$message"] = message,
                    ["$detail"] = detail
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist activity entry");
        }
    }

    public List<ActivityEntry> Query(int limit = 200, string? area = null, string? level = null)
    {
        var where = new List<string>();
        var args = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(area)) { where.Add("area = $area"); args["$area"] = area; }
        if (!string.IsNullOrEmpty(level)) { where.Add("level = $level"); args["$level"] = level; }
        var sql = "SELECT id, ts, level, area, message, detail FROM activity_log " +
                  (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : "") +
                  "ORDER BY id DESC LIMIT " + Math.Clamp(limit, 1, 1000);
        return _db.Query(sql, r => new ActivityEntry(
            r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5)), args);
    }
}
