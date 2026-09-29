namespace VspcAutotaskPlugin.Domain;

public static class BillingPeriod
{
    /// <summary>
    /// Returns the current billing window: from the most recent anchor day up to today,
    /// clamped to 31 days because the VSPC usage API rejects longer ranges.
    /// </summary>
    public static (DateOnly Start, DateOnly End) CurrentWindow(int anchorDayOfMonth, DateOnly today)
    {
        var anchor = Math.Clamp(anchorDayOfMonth, 1, 28);
        var start = new DateOnly(today.Year, today.Month, anchor);
        if (today.Day < anchor)
            start = start.AddMonths(-1);
        if (today.DayNumber - start.DayNumber > 30)
            start = DateOnly.FromDayNumber(today.DayNumber - 30);
        return (start, today);
    }
}
