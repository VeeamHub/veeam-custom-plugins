using VspcAutotaskPlugin.Autotask;
using VspcAutotaskPlugin.Domain;
using VspcAutotaskPlugin.Infrastructure;
using VspcAutotaskPlugin.Storage;
using VspcAutotaskPlugin.Sync;
using VspcAutotaskPlugin.Vspc;

namespace VspcAutotaskPlugin.Workers;

/// <summary>Runs the billing reconciliation once per day at the configured UTC hour.</summary>
public sealed class BillingWorker : BackgroundService
{
    private readonly BillingSyncService _billing;
    private readonly VspcClient _vspc;
    private readonly AutotaskClient _autotask;
    private readonly Store _store;
    private readonly Db _db;
    private readonly ILogger<BillingWorker> _logger;

    public BillingWorker(BillingSyncService billing, VspcClient vspc, AutotaskClient autotask,
        Store store, Db db, ILogger<BillingWorker> logger)
    {
        _billing = billing;
        _vspc = vspc;
        _autotask = autotask;
        _store = store;
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var features = _db.GetJson<FeatureToggles>("features") ?? new FeatureToggles();
                if (features.Billing && _vspc.IsConfigured && _autotask.IsConfigured &&
                    _store.GetCompanyBillings().Count > 0 && IsDue())
                {
                    _logger.LogInformation("Starting scheduled billing sync");
                    await _billing.RunAsync(dryRun: false, ct: stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled billing sync failed");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private bool IsDue()
    {
        var settings = _billing.GetSettings();
        var now = DateTimeOffset.UtcNow;
        if (now.Hour != Math.Clamp(settings.SyncHourUtc, 0, 23)) return false;
        var lastRunRaw = _db.GetSyncState(BillingSyncService.LastRunKey);
        return !DateTimeOffset.TryParse(lastRunRaw, out var lastRun) || lastRun.UtcDateTime.Date < now.UtcDateTime.Date;
    }
}
