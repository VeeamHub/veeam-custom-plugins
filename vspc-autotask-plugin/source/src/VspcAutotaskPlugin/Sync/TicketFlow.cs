using VspcAutotaskPlugin.Domain;

namespace VspcAutotaskPlugin.Sync;

public enum TicketAction
{
    None,
    /// <summary>New alert: create a Pending link and wait out the delay window.</summary>
    StartPending,
    /// <summary>Alarm resolved before the delay elapsed: never create the ticket.</summary>
    CancelPending,
    /// <summary>Delay elapsed and the alarm is still alerting: create the Autotask ticket.</summary>
    CreateTicket,
    /// <summary>Alarm re-triggered while its ticket is open: append a ticket note.</summary>
    AddRetriggerNote,
    /// <summary>Alarm resolved/acknowledged while its ticket is open: close the ticket.</summary>
    CloseTicket,
    /// <summary>Alarm alerting again after the previous cycle finished: restart as Pending.</summary>
    RestartPending,
    /// <summary>A failed link that already has a ticket: resume the Open lifecycle.</summary>
    ResumeOpen
}

/// <summary>
/// Pure decision logic for the alarm → ticket lifecycle (kept free of I/O so it is unit-testable).
/// Mirrors CWM plugin semantics: delay window before creation, bidirectional close,
/// notes on re-trigger, one ticket per triggered alarm at a time.
/// </summary>
public static class TicketFlow
{
    public static bool IsAlert(string? alarmStatus) =>
        string.Equals(alarmStatus, "Error", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(alarmStatus, "Warning", StringComparison.OrdinalIgnoreCase);

    public static bool IsResolved(string? alarmStatus, bool acknowledgeCloses) =>
        string.Equals(alarmStatus, "Resolved", StringComparison.OrdinalIgnoreCase) ||
        (acknowledgeCloses && string.Equals(alarmStatus, "Acknowledged", StringComparison.OrdinalIgnoreCase));

    public static bool IsNewerActivation(string? storedActivationTime, DateTimeOffset? activationTime)
    {
        if (activationTime == null) return false;
        if (string.IsNullOrEmpty(storedActivationTime)) return true;
        return DateTimeOffset.TryParse(storedActivationTime, out var stored)
            ? activationTime.Value > stored.AddSeconds(1)
            : true;
    }

    public static TicketAction Evaluate(
        TicketLink? link,
        string? alarmStatus,
        DateTimeOffset? activationTime,
        DateTimeOffset now,
        bool acknowledgeCloses)
    {
        if (link == null)
            return IsAlert(alarmStatus) ? TicketAction.StartPending : TicketAction.None;

        switch (link.State)
        {
            case TicketLinkState.Pending:
                if (IsResolved(alarmStatus, acknowledgeCloses) ||
                    string.Equals(alarmStatus, "Info", StringComparison.OrdinalIgnoreCase))
                    return TicketAction.CancelPending;
                if (IsAlert(alarmStatus) && link.DueAt.HasValue && now >= link.DueAt.Value)
                    return TicketAction.CreateTicket;
                return TicketAction.None;

            case TicketLinkState.Open:
                if (IsResolved(alarmStatus, acknowledgeCloses))
                    return TicketAction.CloseTicket;
                if (IsAlert(alarmStatus) && IsNewerActivation(link.LastActivationTime, activationTime))
                    return TicketAction.AddRetriggerNote;
                return TicketAction.None;

            case TicketLinkState.Closed:
            case TicketLinkState.Cancelled:
                return IsAlert(alarmStatus) && IsNewerActivation(link.LastActivationTime, activationTime)
                    ? TicketAction.RestartPending
                    : TicketAction.None;

            case TicketLinkState.Error:
                if (!IsAlert(alarmStatus)) return TicketAction.None;
                // Don't hot-loop failed operations; wait out the retry backoff stored in DueAt.
                if (link.DueAt.HasValue && now < link.DueAt.Value) return TicketAction.None;
                return link.AtTicketId.HasValue ? TicketAction.ResumeOpen : TicketAction.RestartPending;

            default:
                return TicketAction.None;
        }
    }
}
