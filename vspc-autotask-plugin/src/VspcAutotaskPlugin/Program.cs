using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Veeam.AC.Service.PluginManager;
using VspcAutotaskPlugin.Autotask;
using VspcAutotaskPlugin.Infrastructure;
using VspcAutotaskPlugin.Plugin;
using VspcAutotaskPlugin.Storage;
using VspcAutotaskPlugin.Sync;
using VspcAutotaskPlugin.Vspc;
using VspcAutotaskPlugin.Web;
using VspcAutotaskPlugin.Workers;

// Resolve the log directory before anything else so that even bootstrap failures
// leave evidence on the VSPC server (console output is not persisted by the host).
var dataDirOverride = Environment.GetEnvironmentVariable("DataDir");
var dataDir = !string.IsNullOrEmpty(dataDirOverride)
    ? dataDirOverride
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VspcAutotaskPlugin");
var logDir = Path.Combine(dataDir, "logs");

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.AddProvider(new FileLoggerProvider(logDir));

    // This service is a VSPC plugin: VSPC starts the executable (Configuration > Catalog)
    // and the Plugin Registry listens on the local gRPC endpoint (env override or port 5017).
    var pluginContext = PluginContext.Load(builder.Configuration);
    if (pluginContext.Manifest == null)
    {
        var message =
            "plugin.manifest.xml could not be located next to the service executable or in the " +
            "package content tree — the plugin package appears to be corrupted or incompletely deployed. " +
            $"Probed from base directory '{AppContext.BaseDirectory}'.";
        File.AppendAllText(Path.Combine(Directory.CreateDirectory(logDir).FullName, "startup-error.log"),
            $"{DateTimeOffset.UtcNow:O} FATAL {message}{Environment.NewLine}");
        Console.Error.WriteLine("VSPC Autotask plugin: " + message);
        return 1;
    }

    // The port is assigned by plugin.manifest.xml; VSPC talks to the plugin over loopback.
    builder.WebHost.UseUrls($"http://127.0.0.1:{pluginContext.Manifest.ServiceHttpPort}");

    builder.Services.AddSingleton(pluginContext);
    builder.Services.AddGrpcClient<PluginRegistry.PluginRegistryClient>(
        options => options.Address = new Uri(pluginContext.RegistryAddress));
    builder.Services.AddHostedService<PluginRegistrationService>();

    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.SerializerOptions.PropertyNameCaseInsensitive = true;
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    builder.Services.AddSingleton<AppPaths>();
    builder.Services.AddSingleton<Db>();
    builder.Services.AddSingleton<SecretProtector>();
    builder.Services.AddSingleton<ActivityLog>();
    builder.Services.AddSingleton<Store>();
    builder.Services.AddSingleton<VspcClient>();
    builder.Services.AddSingleton<AutotaskClient>();
    builder.Services.AddSingleton<CompanySyncService>();
    builder.Services.AddSingleton<BillingSyncService>();
    builder.Services.AddSingleton<TicketSyncService>();
    builder.Services.AddSingleton<PluginAuthService>();
    builder.Services.AddHostedService<TicketWorker>();
    builder.Services.AddHostedService<BillingWorker>();
    builder.Services.AddHostedService<CompanyWorker>();

    var app = builder.Build();

    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    log.LogInformation(
        "VSPC Autotask plugin {Version} starting: port {Port}, registry {Registry} (host env vars present: {HasEnv}), " +
        "package root '{PackageRoot}', data dir '{DataDir}'",
        pluginContext.Manifest.Version, pluginContext.Manifest.ServiceHttpPort, pluginContext.RegistryAddress,
        pluginContext.HasHostEnvironment, pluginContext.PackageRoot ?? "(service dir only)", dataDir);

    // Initialize storage (and prune the activity log) before serving traffic.
    app.Services.GetRequiredService<ActivityLog>().Info("system",
        "VSPC Autotask plugin service starting under VSPC plugin hosting");

    // Log every inbound request except VSPC's high-frequency health polling, so the
    // service log shows exactly what the VSPC UI proxies when a user opens the plugin.
    var requestLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Http");
    app.Use(async (ctx, next) =>
    {
        await next();
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/api/v1/healthcheck", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/readiness", StringComparison.OrdinalIgnoreCase))
            return;
        requestLog.LogInformation("HTTP {Method} {Path} -> {Status} (X-User-Uid: {Uid})",
            ctx.Request.Method, path + ctx.Request.QueryString, ctx.Response.StatusCode,
            string.IsNullOrEmpty(ctx.Request.Headers[PluginAuthService.UserUidHeader].ToString()) ? "absent" : "present");
    });

    // Safety net for Autotask API failures. The UI-facing read accessors already return empty
    // when the connection is unconfigured (see AutotaskClient), but write/action paths (billing
    // preview & run, ticket poll, create-company) and any real Autotask error once connected can
    // still throw AtApiException. Convert it into a structured JSON response so the VSPC-proxied
    // request completes with a parseable body instead of a raw unhandled 500 that the UI shows as
    // an opaque failure. Registered before routing so it wraps endpoint execution.
    var atErrorLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Autotask");
    app.Use(async (ctx, next) =>
    {
        try
        {
            await next();
        }
        catch (AtApiException ex)
        {
            atErrorLog.LogWarning("Autotask API error on {Method} {Path}: {Message}",
                ctx.Request.Method, ctx.Request.Path, ex.Message);
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = ex.StatusCode is >= 400 and < 600
                    ? ex.StatusCode
                    : StatusCodes.Status502BadGateway;
                await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        }
    });

    // The service serves its own UI files as well (VSPC hosts the packaged copies, but the
    // running service remains the source of truth for /content requests and ad-hoc installs).
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // Expose the package content tree — VSPC requests manifest-declared files
    // (icon, UI files, REST specification) from the plugin over HTTP.
    if (pluginContext.PackageRoot != null)
    {
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".yml"] = "text/yaml";
        contentTypes.Mappings[".yaml"] = "text/yaml";
        contentTypes.Mappings[".nuspec"] = "text/xml";
        contentTypes.Mappings[".proto"] = "text/plain";
        app.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = "/content",
            FileProvider = new PhysicalFileProvider(Path.Combine(pluginContext.PackageRoot, "content")),
            ContentTypeProvider = contentTypes,
            ServeUnknownFileTypes = true
        });
    }

    Api.MapApi(app);
    PluginEndpoints.Map(app);

    // Plugin UI pages: paths declared in the REST specification (api.yml) are proxied by the
    // VSPC UI to this service. VSPC opens the plugin window with "GET /?_v=<build>", so every
    // page path must answer GET explicitly — a POST-only mapping makes endpoint routing claim
    // the path and return 405 before the static-files middleware can serve it. Each path here
    // MUST also be declared in api.yml, or the VSPC proxy will not forward it (→ 404).
    var webRoot = app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
    var indexFile = Path.Combine(webRoot, "index.html");
    IResult ServeIndex() => Results.File(indexFile, "text/html");

    // "/" and "/index.html" = the plugin open target; "/autotaskPsa" = the internal (tabbed)
    // page; the remaining paths are the portal-nav routes (AppPortalLayout) so that refreshing
    // or deep-linking onto a left-nav item serves the SPA instead of 404ing. In-app tab/nav
    // clicks are client-side routing and never reach the service.
    string[] pagePaths =
    {
        "/", "/index.html", "/autotaskPsa",
        "/companies", "/billing", "/ticketing", "/settings", "/activity"
    };
    foreach (var page in pagePaths)
    {
        app.MapGet(page, ServeIndex);
        app.MapPost(page, ServeIndex);
    }
    app.MapGet("/autotaskPsa/{**rest}", (string? rest) => ServeIndex());
    app.MapPost("/autotaskPsa/{**rest}", (string? rest) => ServeIndex());

    app.Run();
    return 0;
}
catch (Exception fatal)
{
    // Last-resort capture: without this, a startup crash under VSPC hosting is invisible.
    try
    {
        Directory.CreateDirectory(logDir);
        File.AppendAllText(Path.Combine(logDir, "startup-error.log"),
            $"{DateTimeOffset.UtcNow:O} FATAL {fatal}{Environment.NewLine}");
    }
    catch { /* nothing left to do */ }
    Console.Error.WriteLine("VSPC Autotask plugin failed to start: " + fatal);
    return 1;
}
