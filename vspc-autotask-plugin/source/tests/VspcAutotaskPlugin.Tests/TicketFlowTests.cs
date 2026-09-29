using VspcAutotaskPlugin.Domain;
using VspcAutotaskPlugin.Sync;
using Xunit;

namespace VspcAutotaskPlugin.Tests;

public class TicketFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    private static TicketLink Link(TicketLinkState state, DateTimeOffset? dueAt = null,
        string? lastActivation = null, long? ticketId = null) => new()
    {
        ActiveAlarmUid = "alarm-1",
        State = state,
        DueAt = dueAt,
        LastActivationTime = lastActivation,
        AtTicketId = ticketId
    };

    // ---- no link yet ----

    [Theory]
    [InlineData("Error")]
    [InlineData("Warning")]
    public void New_alert_starts_pending(string status) =>
        Assert.Equal(TicketAction.StartPending, TicketFlow.Evaluate(null, status, Now, Now, true));

    [Theory]
    [InlineData("Resolved")]
    [InlineData("Info")]
    [InlineData("Acknowledged")]
    public void New_non_alert_does_nothing(string status) =>
        Assert.Equal(TicketAction.None, TicketFlow.Evaluate(null, status, Now, Now, true));

    // ---- pending ----

    [Fact]
    public void Pending_alarm_resolved_within_delay_cancels() =>
        Assert.Equal(TicketAction.CancelPending,
            TicketFlow.Evaluate(Link(TicketLinkState.Pending, Now.AddMinutes(5)), "Resolved", Now, Now, true));

    [Fact]
    public void Pending_not_due_yet_waits() =>
        Assert.Equal(TicketAction.None,
            TicketFlow.Evaluate(Link(TicketLinkState.Pending, Now.AddMinutes(5)), "Error", Now, Now, true));

    [Fact]
    public void Pending_due_and_still_alerting_creates_ticket() =>
        Assert.Equal(TicketAction.CreateTicket,
            TicketFlow.Evaluate(Link(TicketLinkState.Pending, Now.AddMinutes(-1)), "Error", Now, Now, true));

    [Fact]
    public void Pending_acknowledged_cancels_when_ack_counts_as_resolved() =>
        Assert.Equal(TicketAction.CancelPending,
            TicketFlow.Evaluate(Link(TicketLinkState.Pending, Now.AddMinutes(5)), "Acknowledged", Now, Now, true));

    // ---- open ----

    [Fact]
    public void Open_alarm_resolved_closes_ticket() =>
        Assert.Equal(TicketAction.CloseTicket,
            TicketFlow.Evaluate(Link(TicketLinkState.Open, ticketId: 42), "Resolved", Now, Now, true));

    [Fact]
    public void Open_acknowledged_closes_only_when_configured()
    {
        var link = Link(TicketLinkState.Open, ticketId: 42);
        Assert.Equal(TicketAction.CloseTicket, TicketFlow.Evaluate(link, "Acknowledged", Now, Now, true));
        Assert.Equal(TicketAction.None, TicketFlow.Evaluate(link, "Acknowledged", Now, Now, false));
    }

    [Fact]
    public void Open_with_newer_activation_adds_note()
    {
        var link = Link(TicketLinkState.Open, lastActivation: Now.AddHours(-2).ToString("O"), ticketId: 42);
        Assert.Equal(TicketAction.AddRetriggerNote, TicketFlow.Evaluate(link, "Error", Now, Now, true));
    }

    [Fact]
    public void Open_with_same_activation_does_nothing()
    {
        var link = Link(TicketLinkState.Open, lastActivation: Now.ToString("O"), ticketId: 42);
        Assert.Equal(TicketAction.None, TicketFlow.Evaluate(link, "Error", Now, Now, true));
    }

    // ---- closed / cancelled ----

    [Fact]
    public void Closed_with_new_activation_restarts()
    {
        var link = Link(TicketLinkState.Closed, lastActivation: Now.AddHours(-3).ToString("O"), ticketId: 42);
        Assert.Equal(TicketAction.RestartPending, TicketFlow.Evaluate(link, "Error", Now, Now, true));
    }

    [Fact]
    public void Cancelled_with_new_activation_restarts()
    {
        var link = Link(TicketLinkState.Cancelled, lastActivation: Now.AddHours(-3).ToString("O"));
        Assert.Equal(TicketAction.RestartPending, TicketFlow.Evaluate(link, "Warning", Now, Now, true));
    }

    [Fact]
    public void Closed_and_quiet_stays_closed()
    {
        var link = Link(TicketLinkState.Closed, lastActivation: Now.ToString("O"), ticketId: 42);
        Assert.Equal(TicketAction.None, TicketFlow.Evaluate(link, "Resolved", Now, Now, true));
    }

    // ---- error retry ----

    [Fact]
    public void Error_waits_out_backoff_then_resumes_or_restarts()
    {
        var backingOff = Link(TicketLinkState.Error, dueAt: Now.AddMinutes(5), ticketId: 42);
        Assert.Equal(TicketAction.None, TicketFlow.Evaluate(backingOff, "Error", Now, Now, true));

        var withTicket = Link(TicketLinkState.Error, dueAt: Now.AddMinutes(-1), ticketId: 42);
        Assert.Equal(TicketAction.ResumeOpen, TicketFlow.Evaluate(withTicket, "Error", Now, Now, true));

        var withoutTicket = Link(TicketLinkState.Error, dueAt: Now.AddMinutes(-1));
        Assert.Equal(TicketAction.RestartPending, TicketFlow.Evaluate(withoutTicket, "Error", Now, Now, true));
    }
}
