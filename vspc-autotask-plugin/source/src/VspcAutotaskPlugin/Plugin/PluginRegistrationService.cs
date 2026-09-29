using Grpc.Core;
using Veeam.AC.Service.PluginManager;
using VspcAutotaskPlugin.Domain;
using VspcAutotaskPlugin.Infrastructure;
using VspcAutotaskPlugin.Vspc;

namespace VspcAutotaskPlugin.Plugin;

/// <summary>
/// Registers the plugin with the VSPC Plugin Registry (gRPC) on startup, then
/// auto-provisions the VSPC REST connection: fetches the API key assigned to the
/// plugin (getApiKey) and discovers the local REST endpoint (discoverService).
/// Unregisters on shutdown.
/// </summary>
public sealed class PluginRegistrationService : IHostedService
{
    private readonly PluginRegistry.PluginRegistryClient _registry;
    private readonly PluginContext _context;
    private readonly Db _db;
    private readonly SecretProtector _protector;
    private readonly ActivityLog _activity;
    private readonly ILogger<PluginRegistrationService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private volatile bool _registered;

    public PluginRegistrationService(
        PluginRegistry.PluginRegistryClient registry, PluginContext context, Db db,
        SecretProtector protector, ActivityLog activity,
        ILogger<PluginRegistrationService> logger)
    {
        _registry = registry;
        _context = context;
        _db = db;
        _protector = protector;
        _activity = activity;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = RunAsync(); // startup gate in Program guarantees plugin hosting + manifest
        return Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        var manifest = _context.Manifest!;

        // 1. Register (retry until VSPC's registry answers).
        var errorLogged = false;
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await _registry.registerAsync(
                    new PluginRegisterRequest { PluginId = manifest.PluginId, Manifest = manifest },
                    new CallOptions(cancellationToken: _stopping.Token)).ResponseAsync;
                _registered = true;
                _logger.LogInformation("Registered plugin {PluginId} with the VSPC plugin registry", manifest.PluginId);
                _activity.Info("system", "Plugin registered with the VSPC service");
                break;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!errorLogged)
                {
                    errorLogged = true;
                    _logger.LogWarning(ex, "Plugin registration failed; retrying every 5 seconds");
                }
                try { await Task.Delay(TimeSpan.FromSeconds(5), _stopping.Token); }
                catch (OperationCanceledException) { return; }
            }
        }

        // 2. Auto-provision the VSPC REST connection from the plugin host.
        if (manifest.ServiceApiKeyObtainMethod == PluginApiKeyObtainMethod.None)
            return;

        for (var attempt = 1; attempt <= 30 && !_stopping.IsCancellationRequested; attempt++)
        {
            try
            {
                var keyReply = await _registry.getApiKeyAsync(
                    new PluginGetApiKeyRequest { PluginId = manifest.PluginId },
                    new CallOptions(cancellationToken: _stopping.Token)).ResponseAsync;
                if (string.IsNullOrEmpty(keyReply.ApiKey))
                    throw new InvalidOperationException("Plugin registry returned an empty API key");

                var restReply = await _registry.discoverServiceAsync(
                    new PluginDiscoverServiceRequest { PluginId = manifest.PluginId, Service = VspcServiceName.Rest },
                    new CallOptions(cancellationToken: _stopping.Token)).ResponseAsync;
                var baseUrl = NormalizeBaseUrl(restReply.ServiceEndpoint);

                _db.SetJson(VspcClient.SettingsKey, new VspcConnectionSettings
                {
                    BaseUrl = baseUrl,
                    ApiKeyEnc = _protector.Protect(keyReply.ApiKey),
                    // Loopback endpoint with a VSPC-managed (typically self-signed) certificate.
                    IgnoreTlsErrors = true
                });

                _logger.LogInformation("VSPC REST connection auto-provisioned at {BaseUrl}", baseUrl);
                _activity.Info("connection", "VSPC REST connection auto-provisioned by the plugin host", baseUrl);
                return;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fetching plugin API key / REST endpoint failed (attempt {Attempt}/30)", attempt);
                try { await Task.Delay(TimeSpan.FromSeconds(10), _stopping.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
        _activity.Warn("connection",
            "Could not obtain the plugin API key from VSPC — configure the VSPC connection manually in Settings");
    }

    private static string NormalizeBaseUrl(string endpoint)
    {
        var url = endpoint.TrimEnd('/');
        if (url.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/api/v3".Length];
        return url;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        if (!_registered || _context.Manifest == null) return;
        try
        {
            await _registry.unregisterAsync(
                new PluginUnregisterRequest { PluginId = _context.Manifest.PluginId },
                new CallOptions(cancellationToken: cancellationToken)).ResponseAsync;
        }
        catch
        {
            // Shutting down — a failed unregister must not block the host.
        }
    }
}
