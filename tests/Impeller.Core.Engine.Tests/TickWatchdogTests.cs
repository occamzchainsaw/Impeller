namespace Impeller.Core.Engine.Tests;

public class TickWatchdogTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Ticks_arriving_on_time_are_healthy()
    {
        var watchdog = new TickWatchdog(Timeout);

        Assert.Equal(
            WatchdogVerdict.Healthy,
            watchdog.Evaluate(Start + TimeSpan.FromSeconds(3), Start));
    }

    [Fact]
    public void A_tick_exactly_at_the_deadline_is_still_healthy()
    {
        // The timeout is how long may pass, not how long may not.
        var watchdog = new TickWatchdog(Timeout);

        Assert.Equal(WatchdogVerdict.Healthy, watchdog.Evaluate(Start + Timeout, Start));
    }

    [Fact]
    public void A_missed_deadline_trips_once_and_then_stays_tripped()
    {
        // Reporting the trip once is what lets the caller engage the failsafe exactly once rather
        // than hammering every control every check.
        var watchdog = new TickWatchdog(Timeout);
        var overdue = Start + TimeSpan.FromSeconds(11);

        Assert.Equal(WatchdogVerdict.JustTripped, watchdog.Evaluate(overdue, Start));
        Assert.Equal(WatchdogVerdict.StillTripped, watchdog.Evaluate(overdue + Timeout, Start));
        Assert.True(watchdog.IsTripped);
    }

    [Fact]
    public void Ticks_resuming_reports_a_recovery_once()
    {
        var watchdog = new TickWatchdog(Timeout);
        watchdog.Evaluate(Start + TimeSpan.FromSeconds(11), Start);

        var resumed = Start + TimeSpan.FromSeconds(20);

        Assert.Equal(WatchdogVerdict.Recovered, watchdog.Evaluate(resumed, resumed));
        Assert.Equal(WatchdogVerdict.Healthy, watchdog.Evaluate(resumed, resumed));
        Assert.False(watchdog.IsTripped);
    }

    [Fact]
    public void A_timeout_of_zero_disables_the_watchdog_rather_than_tripping_constantly()
    {
        // Any other reading of "timeout: 0" would engage the failsafe forever on a healthy engine,
        // which is the opposite of what someone switching it off could possibly want.
        var watchdog = new TickWatchdog(TimeSpan.Zero);

        Assert.Equal(WatchdogVerdict.Healthy, watchdog.Evaluate(Start + TimeSpan.FromHours(1), Start));
        Assert.False(watchdog.IsTripped);
    }

    [Fact]
    public void Clearing_a_trip_lets_the_next_lapse_be_reported_again()
    {
        var watchdog = new TickWatchdog(Timeout);
        watchdog.Evaluate(Start + TimeSpan.FromSeconds(11), Start);

        watchdog.Clear();

        Assert.Equal(
            WatchdogVerdict.JustTripped,
            watchdog.Evaluate(Start + TimeSpan.FromSeconds(12), Start));
    }
}
