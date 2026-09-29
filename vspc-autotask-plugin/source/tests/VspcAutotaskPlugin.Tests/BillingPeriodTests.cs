using VspcAutotaskPlugin.Domain;
using Xunit;

namespace VspcAutotaskPlugin.Tests;

public class BillingPeriodTests
{
    [Fact]
    public void Window_starts_on_anchor_day_of_current_month_when_passed()
    {
        var (start, end) = BillingPeriod.CurrentWindow(1, new DateOnly(2026, 7, 17));
        Assert.Equal(new DateOnly(2026, 7, 1), start);
        Assert.Equal(new DateOnly(2026, 7, 17), end);
    }

    [Fact]
    public void Window_starts_in_previous_month_when_anchor_not_reached_yet()
    {
        var (start, _) = BillingPeriod.CurrentWindow(20, new DateOnly(2026, 7, 17));
        Assert.Equal(new DateOnly(2026, 6, 20), start);
    }

    [Fact]
    public void Anchor_day_equal_to_today_starts_today()
    {
        var (start, end) = BillingPeriod.CurrentWindow(17, new DateOnly(2026, 7, 17));
        Assert.Equal(end, start);
    }

    [Fact]
    public void Anchor_is_clamped_to_28()
    {
        var (start, _) = BillingPeriod.CurrentWindow(31, new DateOnly(2026, 7, 30));
        Assert.Equal(new DateOnly(2026, 7, 28), start);
    }

    [Fact]
    public void Window_never_exceeds_31_days_for_vspc_usage_api()
    {
        // The VSPC usage API rejects ranges longer than 31 days; verify the invariant
        // holds for every anchor day across a full year of "today" values.
        for (var anchor = 1; anchor <= 28; anchor++)
        {
            var day = new DateOnly(2026, 1, 1);
            while (day < new DateOnly(2027, 1, 1))
            {
                var (start, end) = BillingPeriod.CurrentWindow(anchor, day);
                Assert.True(start <= end, $"start after end for anchor {anchor} on {day}");
                Assert.True(end.DayNumber - start.DayNumber <= 30,
                    $"window too long for anchor {anchor} on {day}: {start}..{end}");
                day = day.AddDays(1);
            }
        }
    }
}
