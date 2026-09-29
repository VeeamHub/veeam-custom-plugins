using VspcAutotaskPlugin.Autotask;
using VspcAutotaskPlugin.Domain;
using VspcAutotaskPlugin.Infrastructure;
using VspcAutotaskPlugin.Sync;
using VspcAutotaskPlugin.Vspc;

namespace VspcAutotaskPlugin.Workers;

/// <summary>Polls VSPC alarms and drives the ticket lifecycle on the configured interval.</summary>
public sealed class TicketWorker : BackgroundService
{
    private readonly TicketSyncService _tickets;
    private readonly VspcClient _vspc;
    private readonly AutotaskClient _autotask;
    private readonly Db _db;
    private readonly ILogger<TicketWorker> _logger;

    public TicketWorker(TicketSyncService tickets, VspcClient vspc, AutotaskClient autotask, Db db, ILogger<TicketWorker> logger)
    {
        _tickets = tickets;
        _vspc = vspc;
        _autotask = autotask;
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); // let the host settle
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(120);
            try
            {
                var features = _db.GetJson<FeatureToggles>("features") ?? new FeatureToggles();
                var settings = _tickets.GetSettings();
                interval = TimeSpan.FromSeconds(Math.Clamp(settings.PollSeconds, 30, 3600));

                if (features.Ticketing && _vspc.IsConfigured && _autotask.IsConfigured)
                {
                    var summary = await _tickets.PollOnceAsync(stoppingToken);
                    if (summary.Created + summary.Closed + summary.Notes + summary.Cancelled + summary.Errors > 0)
                        _logger.LogInformation(
                            "Ticket poll: {Swept} alarm events, {Created} created, {Closed} closed, {Notes} notes, {Cancelled} cancelled, {Errors} errors",
                            summary.Swept, summary.Created, summary.Closed, summary.Notes, summary.Cancelled, summary.Errors);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ticket poll failed");
            }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
