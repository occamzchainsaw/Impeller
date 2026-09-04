using Impeller.App.ViewModels;
using Impeller.App.ViewModels.Engine;
using Microsoft.Extensions.Time.Testing;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// Covers the strip at the foot of the window.
/// </summary>
/// <remarks>
/// One case here is the reason the strip exists rather than being a nicety: an engine that has
/// stopped ticking while the pipe stays up. Everything else on screen looks exactly as it does when
/// the machine is fine — the cards keep their last duty, the connection still says connected — and
/// the age is the only thing that says otherwise.
/// </remarks>
public class ShellStatusTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static (ShellStatusViewModel Status, FakeTimeProvider Clock) Rig()
    {
        var clock = new FakeTimeProvider(Start);
        return (new ShellStatusViewModel(new EngineConnection(), clock), clock);
    }

    [Fact]
    public void A_fresh_reading_reads_as_current_rather_than_as_a_number()
    {
        // The engine ticks once a second, so a number here would be "1 s ago" for ever and would
        // teach the user to ignore the one place that can report it having stopped.
        Assert.Equal("updating", ShellStatusViewModel.Describe(TimeSpan.FromSeconds(1)));
        Assert.False(ShellStatusViewModel.IsStaleAge(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void An_engine_that_stops_ticking_is_stale_even_though_it_is_still_connected()
    {
        // The case the strip is for. Nothing else on screen changes when this happens.
        Assert.True(ShellStatusViewModel.IsStaleAge(TimeSpan.FromSeconds(20)));
        Assert.Equal("last reading 20 s ago", ShellStatusViewModel.Describe(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void A_long_silence_is_counted_in_minutes()
    {
        Assert.Equal("last reading 7 min ago", ShellStatusViewModel.Describe(TimeSpan.FromMinutes(7)));
        Assert.Equal("no readings for over an hour", ShellStatusViewModel.Describe(TimeSpan.FromHours(3)));
    }

    [Fact]
    public void Having_never_heard_from_it_is_not_the_same_as_having_heard_recently()
    {
        // Null is a distinct state, and the one a window opened before the engine came up sits in.
        Assert.Equal("waiting for the first reading", ShellStatusViewModel.Describe(null));
        Assert.True(ShellStatusViewModel.IsStaleAge(null));
    }

    [Fact]
    public void The_age_is_measured_from_when_the_reading_arrived()
    {
        var (status, clock) = Rig();

        status.NoteTick();
        clock.Advance(TimeSpan.FromSeconds(12));

        Assert.Equal(TimeSpan.FromSeconds(12), status.SinceLastReading);
    }

    [Fact]
    public void A_reading_arriving_resets_the_age()
    {
        var (status, clock) = Rig();

        status.NoteTick();
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(ShellStatusViewModel.IsStaleAge(status.SinceLastReading));

        status.NoteTick();

        Assert.False(ShellStatusViewModel.IsStaleAge(status.SinceLastReading));
    }

    [Fact]
    public void With_no_engine_the_age_says_nothing()
    {
        // "Last reading 4 hours ago" under "not connected to the engine service" is two ways of
        // saying one thing, and the second one reads like a separate fault.
        var (status, clock) = Rig();

        status.NoteTick();
        clock.Advance(TimeSpan.FromMinutes(4));

        Assert.False(status.IsConnected);
        Assert.Equal(string.Empty, status.TickAgeText);
        Assert.False(status.IsStale);
    }

    [Fact]
    public void The_age_repaints_itself_without_anything_arriving()
    {
        // An age that only updates when a reading lands is exactly the age that cannot report a
        // reading not landing. The timer is the whole mechanism.
        var (status, clock) = Rig();
        var repaints = 0;

        status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellStatusViewModel.TickAgeText))
            {
                repaints++;
            }
        };

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.True(repaints >= 4, $"expected the strip to repaint about once a second, saw {repaints}");
    }

    [Fact]
    public void Disposing_stops_it_repainting()
    {
        var (status, clock) = Rig();
        var repaints = 0;

        status.PropertyChanged += (_, _) => repaints++;
        status.Dispose();

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, repaints);
    }

    [Fact]
    public void With_nothing_loaded_there_is_no_configuration_to_name()
    {
        var (status, _) = Rig();

        Assert.False(status.HasConfiguration);
        Assert.Equal(string.Empty, status.ConfigurationName);
    }
}
