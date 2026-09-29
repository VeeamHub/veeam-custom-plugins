using VspcAutotaskPlugin.Autotask;
using VspcAutotaskPlugin.Domain;
using VspcAutotaskPlugin.Infrastructure;
using VspcAutotaskPlugin.Storage;
using VspcAutotaskPlugin.Sync;
using VspcAutotaskPlugin.Vspc;

namespace VspcAutotaskPlugin.Workers;

/// <summary>
/// Refreshes cached company lists and the VSPC alarm-template catalog every 4 hours
/// (matching the CWM plugin's default synchronization interval).
/// </summary>
public sealed class CompanyWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    private readonly CompanySyncService _companies;
    private readonly VspcClient _vspc;
    private readonly AutotaskClient _autotask;
    private readonly Store _store;
    private readonly Db _db;
    private readonly ILogger<CompanyWorker> _logger;

    public CompanyWorker(CompanySyncService companies, VspcClient vspc, AutotaskClient autotask,
        Store store, Db db, ILogger<CompanyWorker> logger)
    {
        _companies = companies;
        _vspc = vspc;
        _autotask = autotask;
        _store = store;
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var features = _db.GetJson<FeatureToggles>("features") ?? new FeatureToggles();
                if (_vspc.IsConfigured && _autotask.IsConfigured)
                {
                    if (features.Companies)
                        await _companies.GetOrRefreshAsync(Interval - TimeSpan.FromMinutes(5), stoppingToken);

                    if (features.Ticketing)
                    {
                        var templates = await _vspc.GetAlarmTemplatesAsync(stoppingToken);
                        _store.SyncAlarmRules(templates.Select(t => new AlarmRule(
                            t.InstanceUid, false, t.Name, t.Category, t.InternalId)));
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Company/alarm cache refresh failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
