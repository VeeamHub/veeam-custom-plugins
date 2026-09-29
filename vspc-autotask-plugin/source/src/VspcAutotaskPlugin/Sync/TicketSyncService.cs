using VspcAutotaskPlugin.Autotask;
using VspcAutotaskPlugin.Domain;
using VspcAutotaskPlugin.Infrastructure;
using VspcAutotaskPlugin.Storage;
using VspcAutotaskPlugin.Vspc;

namespace VspcAutotaskPlugin.Sync;

public sealed record TicketPollSummary(
    DateTimeOffset At, int Swept, int Created, int Closed, int Notes, int Cancelled, int Errors, string? Message);

/// <summary>
/// Automated ticketing — the Autotask counterpart of CWM's Ticketing feature.
/// Polls VSPC triggered alarms (VSPC has no webhooks), applies the delay window,
/// creates/annotates/closes Autotask tickets, and syncs closure both directions.
/// </summary>
public sealed class TicketSyncService
{
    public const string SettingsKey = "ticketing.settings";
    private const string LastSweepKey = "ticketing.lastSweep";
    private const string LastSummaryKey = "ticketing.lastSummary";
    private static readonly TimeSpan SweepOverlap = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ErrorRetryBackoff = TimeSpan.FromMinutes(10);

    private readonly VspcClient _vspc;
    private readonly AutotaskClient _autotask;
    private readonly Store _store;
    private readonly Db _db;
    private readonly ActivityLog _activity;

    public TicketSyncService(VspcClient vspc, AutotaskClient autotask, Store store, Db db, ActivityLog activity)
    {
        _vspc = vspc;
        _autotask = autotask;
        _store = store;
        _db = db;
        _activity = activity;
    }

    public TicketingSettings GetSettings() => _db.GetJson<TicketingSettings>(SettingsKey) ?? new TicketingSettings();

    public TicketPollSummary? GetLastSummary() => _db.GetSyncJson<TicketPollSummary>(LastSummaryKey);

    public async Task<TicketPollSummary> PollOnceAsync(CancellationToken ct = default)
    {
        var settings = GetSettings();
        int created = 0, closed = 0, notes = 0, cancelled = 0, errors = 0, swept = 0;
        var now = DateTimeOffset.UtcNow;

        if (settings.WarningPriorityId == null || settings.ErrorPriorityId == null ||
            settings.NewStatusId == null || settings.CompleteStatusId == null)
        {
            var summary = new TicketPollSummary(now, 0, 0, 0, 0, 0, 0,
                "Ticketing is not fully configured (priorities and statuses are required)");
            _db.SetSyncJson(LastSummaryKey, summary);
            return summary;
        }

        // The "new" and "closed" statuses MUST differ: bidirectional close detection reads a
        // ticket's status back and treats CompleteStatusId as "closed by a technician". If it
        // equals NewStatusId, every ticket we create looks closed the instant it is created —
        // the alarm gets falsely resolved and re-triggers, spawning a duplicate every poll.
        if (settings.NewStatusId == settings.CompleteStatusId)
        {
            var summary = new TicketPollSummary(now, 0, 0, 0, 0, 0, 0,
                "The 'new' and 'closed' ticket statuses are identical — open Settings and choose distinct Autotask statuses.");
            _db.SetSyncJson(LastSummaryKey, summary);
            return summary;
        }

        // Snapshot the links already Open before this poll creates any tickets. Section 3
        // (Autotask-side close → VSPC resolve) only reconciles these, so a ticket created
        // during this same poll is never mistaken for one a technician just closed.
        var openBeforePoll = _store.GetTicketLinksByState(TicketLinkState.Open)
            .Where(l => l.AtTicketId.HasValue)
            .Select(l => l.ActiveAlarmUid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var enabledAlarms = _store.GetEnabledAlarmUids();
        var mappings = _store.GetCompanyMappings()
            .ToDictionary(m => m.VspcCompanyUid, StringComparer.OrdinalIgnoreCase);
        var alarmNames = _store.GetAlarmRules()
            .ToDictionary(r => r.AlarmTemplateUid, r => r.Name ?? "", StringComparer.OrdinalIgnoreCase);

        // ---- 1. Sweep alarm activity since the last poll ----------------------
        var lastSweepRaw = _db.GetSyncState(LastSweepKey);
        DateTimeOffset? since = null;
        if (DateTimeOffset.TryParse(lastSweepRaw, out var lastSweep))
            since = lastSweep - SweepOverlap;
        if (settings.EnabledAt.HasValue && (since == null || since < settings.EnabledAt))
            since = settings.EnabledAt;

        List<VspcActiveAlarm> changedAlarms;
        try
        {
            changedAlarms = await _vspc.GetActiveAlarmsAsync(since, ct);
            swept = changedAlarms.Count;
        }
        catch (Exception ex) when (ex is VspcApiException or HttpRequestException or TaskCanceledException)
        {
            _activity.Error("ticketing", "Failed to read triggered alarms from VSPC", ex.Message);
            var summary = new TicketPollSummary(now, 0, 0, 0, 0, 0, 1, "VSPC alarm sweep failed: " + ex.Message);
            _db.SetSyncJson(LastSummaryKey, summary);
            return summary;
        }

        foreach (var alarm in changedAlarms)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await ProcessAlarmEventAsync(alarm, settings, enabledAlarms, mappings, alarmNames, now, ct);
                switch (outcome)
                {
                    case TicketAction.CreateTicket: created++; break;
                    case TicketAction.CloseTicket: closed++; break;
                    case TicketAction.AddRetriggerNote: notes++; break;
                    case TicketAction.CancelPending: cancelled++; break;
                }
            }
            catch (Exception ex) when (ex is AtApiException or VspcApiException or HttpRequestException)
            {
                errors++;
                _activity.Error("ticketing", $"Failed to process alarm event {alarm.InstanceUid}", ex.Message);
            }
        }
        _db.SetSyncState(LastSweepKey, now.ToString("O"));

        // ---- 2. Pending links whose delay has elapsed --------------------------
        foreach (var link in _store.GetTicketLinksByState(TicketLinkState.Pending))
        {
            ct.ThrowIfCancellationRequested();
            if (link.DueAt.HasValue && now < link.DueAt.Value) continue;
            try
            {
                // Confirm the alarm is still alerting right before creating the ticket.
                var alarm = await _vspc.GetActiveAlarmAsync(link.ActiveAlarmUid, ct);
                var status = alarm?.LastActivation?.Status;
                if (alarm == null || !TicketFlow.IsAlert(status))
                {
                    link.State = TicketLinkState.Cancelled;
                    link.LastStatus = status ?? "Gone";
                    _store.UpsertTicketLink(link);
                    cancelled++;
                    _activity.Info("ticketing",
                        $"Alarm '{link.AlarmName}' on '{link.ObjectName}' cleared within the delay window — no ticket created");
                    continue;
                }
                await CreateTicketAsync(link, alarm, settings, ct);
                created++;
            }
            catch (Exception ex) when (ex is AtApiException or VspcApiException or HttpRequestException)
            {
                errors++;
                MarkLinkError(link, ex.Message, now);
            }
        }

        // ---- 3. Tickets closed on the Autotask side → resolve VSPC alarms ------
        if (settings.ResolveAlarmOnTicketClose)
        {
            var openLinks = _store.GetTicketLinksByState(TicketLinkState.Open)
                .Where(l => l.AtTicketId.HasValue && openBeforePoll.Contains(l.ActiveAlarmUid)).ToList();
            foreach (var chunk in openLinks.Chunk(100))
            {
                ct.ThrowIfCancellationRequested();
                List<AtTicket> tickets;
                try
                {
                    tickets = await _autotask.QueryAsync<AtTicket>("Tickets",
                        AtQuery.In("id", chunk.Select(l => (object)l.AtTicketId!.Value)),
                        new[] { "id", "status", "ticketNumber" }, ct: ct);
                }
                catch (Exception ex) when (ex is AtApiException or HttpRequestException)
                {
                    errors++;
                    _activity.Error("ticketing", "Failed to read ticket statuses from Autotask", ex.Message);
                    continue;
                }

                foreach (var ticket in tickets.Where(t => t.Status == settings.CompleteStatusId))
                {
                    var link = chunk.First(l => l.AtTicketId == ticket.Id);
                    try
                    {
                        await _vspc.ResolveActiveAlarmAsync(link.ActiveAlarmUid,
                            $"Closed via Autotask ticket {ticket.TicketNumber ?? ticket.Id.ToString()}", false, ct);
                    }
                    catch (VspcApiException ex) when (ex.StatusCode == 404)
                    {
                        // Alarm already gone in VSPC — nothing to resolve.
                    }
                    catch (Exception ex) when (ex is VspcApiException or HttpRequestException)
                    {
                        errors++;
                        _activity.Error("ticketing",
                            $"Ticket {ticket.TicketNumber} is closed but the VSPC alarm could not be resolved", ex.Message);
                        continue;
                    }
                    link.State = TicketLinkState.Closed;
                    link.LastStatus = "Resolved";
                    _store.UpsertTicketLink(link);
                    closed++;
                    _activity.Info("ticketing",
                        $"Ticket {ticket.TicketNumber} was closed in Autotask — resolved alarm '{link.AlarmName}' on '{link.ObjectName}'");
                }
            }
        }

        var result = new TicketPollSummary(now, swept, created, closed, notes, cancelled, errors, null);
        _db.SetSyncJson(LastSummaryKey, result);
        return result;
    }

    // ------------------------------------------------------------------ steps

    private async Task<TicketAction> ProcessAlarmEventAsync(
        VspcActiveAlarm alarm, TicketingSettings settings, HashSet<string> enabledAlarms,
        Dictionary<string, CompanyMapping> mappings, Dictionary<string, string> alarmNames,
        DateTimeOffset now, CancellationToken ct)
    {
        var link = _store.GetTicketLink(alarm.InstanceUid);
        var status = alarm.LastActivation?.Status;
        var activationTime = alarm.LastActivation?.Time;
        var action = TicketFlow.Evaluate(link, status, activationTime, now, settings.AcknowledgeClosesTicket);

        switch (action)
        {
            case TicketAction.StartPending:
            case TicketAction.RestartPending:
            {
                if (alarm.AlarmTemplateUid == null || !enabledAlarms.Contains(alarm.AlarmTemplateUid))
                    return TicketAction.None;
                var orgUid = alarm.Object?.OrganizationUid;
                if (orgUid == null || !mappings.TryGetValue(orgUid, out var mapping))
                    return TicketAction.None; // alarm belongs to an unmapped org (provider-internal, reseller, ...)

                link ??= new TicketLink { ActiveAlarmUid = alarm.InstanceUid };
                link.VspcCompanyUid = mapping.VspcCompanyUid;
                link.VspcCompanyName = mapping.VspcCompanyName;
                link.AtCompanyId = mapping.AtCompanyId;
                link.AlarmTemplateUid = alarm.AlarmTemplateUid;
                link.AlarmName = alarmNames.TryGetValue(alarm.AlarmTemplateUid, out var name) && name.Length > 0
                    ? name : link.AlarmName ?? "VSPC alarm";
                link.ObjectName = alarm.Object?.ObjectName ?? alarm.Object?.ComputerName ?? link.ObjectName;
                link.State = TicketLinkState.Pending;
                link.LastStatus = status;
                link.LastActivationTime = activationTime?.ToString("O");
                link.RepeatCount = alarm.RepeatCount;
                link.DueAt = now.AddMinutes(Math.Max(0, settings.DelayMinutes));
                link.AtTicketId = action == TicketAction.RestartPending ? null : link.AtTicketId;
                link.AtTicketNumber = action == TicketAction.RestartPending ? null : link.AtTicketNumber;
                link.LastError = null;
                _store.UpsertTicketLink(link);
                return action;
            }

            case TicketAction.CancelPending:
                link!.State = TicketLinkState.Cancelled;
                link.LastStatus = status;
                link.LastActivationTime = activationTime?.ToString("O") ?? link.LastActivationTime;
                _store.UpsertTicketLink(link);
                _activity.Info("ticketing",
                    $"Alarm '{link.AlarmName}' on '{link.ObjectName}' cleared within the delay window — no ticket created");
                return action;

            case TicketAction.CreateTicket:
                // Created in the dedicated pending pass (with a fresh status check).
                return TicketAction.None;

            case TicketAction.CloseTicket:
                if (!settings.CloseTicketOnAlarmResolve) return TicketAction.None;
                await CloseTicketAsync(link!, alarm, settings, ct);
                return action;

            case TicketAction.AddRetriggerNote:
                if (settings.NoteOnRetrigger)
                    await AddRetriggerNoteAsync(link!, alarm, ct);
                link!.LastStatus = status;
                link.LastActivationTime = activationTime?.ToString("O") ?? link.LastActivationTime;
                link.RepeatCount = alarm.RepeatCount;
                _store.UpsertTicketLink(link);
                return settings.NoteOnRetrigger ? action : TicketAction.None;

            case TicketAction.ResumeOpen:
                link!.State = TicketLinkState.Open;
                link.LastError = null;
                _store.UpsertTicketLink(link);
                return TicketAction.None;

            default:
                return TicketAction.None;
        }
    }

    private async Task CreateTicketAsync(TicketLink link, VspcActiveAlarm alarm, TicketingSettings settings, CancellationToken ct)
    {
        var status = alarm.LastActivation?.Status ?? "Error";
        var priority = string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase)
            ? settings.ErrorPriorityId!.Value
            : settings.WarningPriorityId!.Value;

        var title = $"[Veeam] {link.AlarmName}: {link.ObjectName}";
        if (title.Length > 255) title = title[..255];

        var message = alarm.LastActivation?.Message ?? "";
        var description =
            $"Veeam Service Provider Console alarm\n" +
            $"----------------------------------------\n" +
            $"Alarm:     {link.AlarmName}\n" +
            $"Severity:  {status}\n" +
            $"Company:   {link.VspcCompanyName}\n" +
            $"Object:    {link.ObjectName}\n" +
            $"Computer:  {alarm.Object?.ComputerName ?? "-"}\n" +
            $"Triggered: {alarm.LastActivation?.Time:yyyy-MM-dd HH:mm:ss} UTC (repeat count {alarm.RepeatCount})\n\n" +
            $"{message}\n\n" +
            $"VSPC alarm reference: {link.ActiveAlarmUid}";
        if (description.Length > 7900) description = description[..7900];

        var payload = new Dictionary<string, object?>
        {
            ["companyID"] = link.AtCompanyId,
            ["title"] = title,
            ["description"] = description,
            ["status"] = settings.NewStatusId!.Value,
            ["priority"] = priority,
            ["dueDateTime"] = DateTime.UtcNow.AddHours(Math.Max(1, settings.DueHours)).ToString("yyyy-MM-ddTHH:mm:ssZ")
        };
        if (settings.QueueId.HasValue) payload["queueID"] = settings.QueueId.Value;
        if (settings.SourceId.HasValue) payload["source"] = settings.SourceId.Value;
        if (settings.TicketTypeId.HasValue) payload["ticketType"] = settings.TicketTypeId.Value;

        var ticketId = await _autotask.CreateAsync("Tickets", payload, ct);
        var ticket = await _autotask.GetAsync<AtTicket>("Tickets", ticketId, ct);

        link.AtTicketId = ticketId;
        link.AtTicketNumber = ticket?.TicketNumber;
        link.State = TicketLinkState.Open;
        link.LastStatus = status;
        link.LastActivationTime = alarm.LastActivation?.Time?.ToString("O") ?? link.LastActivationTime;
        link.RepeatCount = alarm.RepeatCount;
        link.LastError = null;
        _store.UpsertTicketLink(link);
        _activity.Info("ticketing",
            $"Created Autotask ticket {link.AtTicketNumber ?? ("#" + ticketId)} for alarm '{link.AlarmName}' " +
            $"on '{link.ObjectName}' ({link.VspcCompanyName})");
    }

    private async Task CloseTicketAsync(TicketLink link, VspcActiveAlarm alarm, TicketingSettings settings, CancellationToken ct)
    {
        if (link.AtTicketId == null) return;
        try
        {
            await _autotask.PatchAsync("Tickets", new Dictionary<string, object?>
            {
                ["id"] = link.AtTicketId.Value,
                ["status"] = settings.CompleteStatusId!.Value
            }, ct);

            await TryAddNoteAsync(link.AtTicketId.Value,
                "Alarm resolved in Veeam Service Provider Console",
                $"The originating VSPC alarm was {alarm.LastActivation?.Status?.ToLowerInvariant() ?? "resolved"}.\n\n" +
                $"{alarm.LastActivation?.Message ?? ""}".Trim(), ct);

            link.State = TicketLinkState.Closed;
            link.LastStatus = alarm.LastActivation?.Status;
            link.LastActivationTime = alarm.LastActivation?.Time?.ToString("O") ?? link.LastActivationTime;
            link.LastError = null;
            _store.UpsertTicketLink(link);
            _activity.Info("ticketing",
                $"Closed Autotask ticket {link.AtTicketNumber ?? ("#" + link.AtTicketId)} — alarm '{link.AlarmName}' " +
                $"was {alarm.LastActivation?.Status?.ToLowerInvariant()} in VSPC");
        }
        catch (Exception ex) when (ex is AtApiException or HttpRequestException)
        {
            MarkLinkError(link, "Closing ticket failed: " + ex.Message, DateTimeOffset.UtcNow);
            throw;
        }
    }

    private async Task AddRetriggerNoteAsync(TicketLink link, VspcActiveAlarm alarm, CancellationToken ct)
    {
        if (link.AtTicketId == null) return;
        await TryAddNoteAsync(link.AtTicketId.Value,
            "Alarm re-triggered in Veeam Service Provider Console",
            $"Status: {alarm.LastActivation?.Status}\n" +
            $"Time:   {alarm.LastActivation?.Time:yyyy-MM-dd HH:mm:ss} UTC\n" +
            $"Repeat count: {alarm.RepeatCount}\n\n" +
            $"{alarm.LastActivation?.Message ?? ""}".Trim(), ct);
        _activity.Info("ticketing",
            $"Added re-trigger note to ticket {link.AtTicketNumber ?? ("#" + link.AtTicketId)} for alarm '{link.AlarmName}'");
    }

    /// <summary>
    /// TicketNotes requires instance-specific noteType/publish picklist values; resolve
    /// sensible defaults from metadata and never fail the main operation over a note.
    /// </summary>
    private async Task TryAddNoteAsync(long ticketId, string title, string description, CancellationToken ct)
    {
        try
        {
            var noteTypes = await _autotask.GetPicklistAsync("TicketNotes", "noteType", ct);
            var publishOptions = await _autotask.GetPicklistAsync("TicketNotes", "publish", ct);
            var noteType = noteTypes.FirstOrDefault(v =>
                    v.Label?.Contains("detail", StringComparison.OrdinalIgnoreCase) == true)
                ?? noteTypes.FirstOrDefault();
            var publish = publishOptions.FirstOrDefault(v =>
                    v.Label?.Contains("all", StringComparison.OrdinalIgnoreCase) == true)
                ?? publishOptions.FirstOrDefault();
            if (noteType?.Value == null || publish?.Value == null)
            {
                _activity.Warn("ticketing", "Could not resolve TicketNotes picklist defaults; note skipped");
                return;
            }
            if (description.Length > 31000) description = description[..31000];
            await _autotask.CreateAsync("TicketNotes", new Dictionary<string, object?>
            {
                ["ticketID"] = ticketId,
                ["title"] = title.Length > 250 ? title[..250] : title,
                ["description"] = description.Length == 0 ? title : description,
                ["noteType"] = int.Parse(noteType.Value),
                ["publish"] = int.Parse(publish.Value)
            }, ct);
        }
        catch (Exception ex) when (ex is AtApiException or HttpRequestException or FormatException)
        {
            _activity.Warn("ticketing", $"Could not add note to ticket #{ticketId}", ex.Message);
        }
    }

    private void MarkLinkError(TicketLink link, string error, DateTimeOffset now)
    {
        link.State = TicketLinkState.Error;
        link.LastError = error.Length > 900 ? error[..900] : error;
        link.DueAt = now + ErrorRetryBackoff;
        _store.UpsertTicketLink(link);
        _activity.Error("ticketing",
            $"Ticket operation failed for alarm '{link.AlarmName}' on '{link.ObjectName}' — will retry", error);
    }
}
