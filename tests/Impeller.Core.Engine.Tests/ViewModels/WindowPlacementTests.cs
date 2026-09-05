using Impeller.App.ViewModels.Shell;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// The rules for how big the window opens and whether a remembered position still exists.
/// </summary>
/// <remarks>
/// Every case here is a real display arrangement rather than an invented one: an ultrawide, a
/// laptop at 150%, a second monitor that has been unplugged, a resolution that dropped. The bugs
/// this guards against all look identical from the outside — the app does not appear — and none of
/// them can be reproduced without owning the hardware.
/// </remarks>
public sealed class WindowPlacementTests
{
    /// <summary>A 5120 x 1440 ultrawide at 100%, taskbar at the bottom.</summary>
    private static readonly PixelRect Ultrawide = new(0, 0, 5120, 1392);

    /// <summary>An ordinary 2560 x 1440 monitor.</summary>
    private static readonly PixelRect Wide = new(0, 0, 2560, 1392);

    [Fact]
    public void The_window_does_not_open_the_width_of_an_ultrawide()
    {
        var placement = WindowPlacementPolicy.Default(Ultrawide, scale: 1d);

        Assert.Equal(WindowPlacementPolicy.PreferredWidth, placement.Width);
        Assert.Equal(WindowPlacementPolicy.PreferredHeight, placement.Height);
    }

    [Fact]
    public void The_window_opens_centred()
    {
        var placement = WindowPlacementPolicy.Default(Ultrawide, scale: 1d);

        Assert.Equal((Ultrawide.Width - placement.Width) / 2, placement.X);
        Assert.Equal((Ultrawide.Height - placement.Height) / 2, placement.Y);
    }

    [Fact]
    public void A_second_monitor_is_centred_on_itself_not_on_the_desktop()
    {
        var second = new PixelRect(2560, -300, 1920, 1080);

        var placement = WindowPlacementPolicy.Default(second, scale: 1d);

        Assert.InRange(placement.X, second.X, second.Right - placement.Width);
        Assert.InRange(placement.Y, second.Y, second.Bottom - placement.Height);
    }

    [Fact]
    public void The_default_size_follows_the_display_scaling()
    {
        var placement = WindowPlacementPolicy.Default(new PixelRect(0, 0, 3840, 2100), scale: 1.5d);

        Assert.Equal((int)(WindowPlacementPolicy.PreferredWidth * 1.5), placement.Width);
        Assert.Equal((int)(WindowPlacementPolicy.PreferredHeight * 1.5), placement.Height);
    }

    [Fact]
    public void A_display_too_small_for_the_preferred_size_gets_most_of_itself()
    {
        var small = new PixelRect(0, 0, 1366, 728);

        var placement = WindowPlacementPolicy.Default(small, scale: 1d);

        Assert.True(placement.Width <= small.Width, $"{placement.Width} wider than the screen.");
        Assert.True(placement.Height <= small.Height, $"{placement.Height} taller than the screen.");
        Assert.True(placement.X >= small.X);
        Assert.True(placement.Y >= small.Y);
    }

    [Fact]
    public void A_remembered_position_is_left_exactly_where_it_was()
    {
        var stored = new WindowPlacement(1700, 240, 1400, 900, Maximized: false);

        var restored = WindowPlacementPolicy.Restore(stored, [Ultrawide], Ultrawide, scale: 1d);

        Assert.Equal(stored, restored);
    }

    [Fact]
    public void A_window_straddling_two_monitors_is_not_tidied_up()
    {
        var left = new PixelRect(0, 0, 2560, 1392);
        var right = new PixelRect(2560, 0, 2560, 1392);
        var stored = new WindowPlacement(2200, 300, 1200, 820, Maximized: false);

        var restored = WindowPlacementPolicy.Restore(stored, [left, right], left, scale: 1d);

        Assert.Equal(stored, restored);
    }

    [Fact]
    public void A_position_on_a_monitor_that_is_gone_comes_back_to_the_one_that_is_left()
    {
        // Saved on a second screen to the right, which has since been unplugged.
        var stored = new WindowPlacement(3200, 400, 1200, 820, Maximized: false);

        var restored = WindowPlacementPolicy.Restore(stored, [Wide], Wide, scale: 1d);

        Assert.NotEqual(stored.X, restored.X);
        Assert.True(WindowPlacementPolicy.IsReachable(restored, [Wide]), "Restored off screen.");
    }

    [Fact]
    public void A_position_left_behind_by_a_dropped_resolution_is_rescued()
    {
        var stored = new WindowPlacement(3000, 1300, 1200, 820, Maximized: false);

        var restored = WindowPlacementPolicy.Restore(stored, [new PixelRect(0, 0, 1920, 1032)], new PixelRect(0, 0, 1920, 1032), scale: 1d);

        Assert.True(
            WindowPlacementPolicy.IsReachable(restored, [new PixelRect(0, 0, 1920, 1032)]),
            "Restored off screen.");
    }

    [Fact]
    public void A_window_hanging_off_the_right_edge_is_left_alone()
    {
        // Deliberate: enough of the title bar is still on screen to drag it back.
        var stored = new WindowPlacement(2200, 300, 1200, 820, Maximized: false);

        var restored = WindowPlacementPolicy.Restore(stored, [Wide], Wide, scale: 1d);

        Assert.Equal(stored, restored);
    }

    [Fact]
    public void A_window_whose_title_bar_is_above_the_screen_is_rescued()
    {
        // The body is visible and the title bar is not, so it cannot be dragged anywhere.
        var stored = new WindowPlacement(600, -200, 1200, 820, Maximized: false);

        var restored = WindowPlacementPolicy.Restore(stored, [Wide], Wide, scale: 1d);

        Assert.NotEqual(stored.Y, restored.Y);
    }

    [Fact]
    public void A_size_smaller_than_the_window_can_use_is_grown()
    {
        var stored = new WindowPlacement(100, 100, 300, 200, Maximized: false);

        var restored = WindowPlacementPolicy.Restore(stored, [Wide], Wide, scale: 1d);

        Assert.Equal(WindowPlacementPolicy.MinimumWidth, restored.Width);
        Assert.Equal(WindowPlacementPolicy.MinimumHeight, restored.Height);
    }

    [Fact]
    public void Nothing_remembered_reads_as_a_first_run()
    {
        var restored = WindowPlacementPolicy.Restore(default, [Wide], Wide, scale: 1d);

        Assert.Equal(WindowPlacementPolicy.Default(Wide, 1d), restored);
    }

    [Fact]
    public void Maximised_survives_the_monitor_it_was_maximised_on_disappearing()
    {
        var stored = new WindowPlacement(3200, 400, 1200, 820, Maximized: true);

        var restored = WindowPlacementPolicy.Restore(stored, [Wide], Wide, scale: 1d);

        Assert.True(restored.Maximized);
    }

    [Fact]
    public void Rectangles_that_do_not_touch_do_not_overlap()
    {
        var overlap = new PixelRect(0, 0, 100, 100).Intersect(new PixelRect(100, 0, 100, 100));

        Assert.True(overlap.IsEmpty);
    }

    [Fact]
    public void Rectangles_that_do_touch_report_the_shared_part()
    {
        var overlap = new PixelRect(0, 0, 100, 100).Intersect(new PixelRect(60, 40, 100, 100));

        Assert.Equal(new PixelRect(60, 40, 40, 60), overlap);
    }
}
