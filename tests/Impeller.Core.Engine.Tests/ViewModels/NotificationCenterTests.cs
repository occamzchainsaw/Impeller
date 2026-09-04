using Impeller.App.ViewModels.Notifications;
using Microsoft.Extensions.Time.Testing;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// Covers what the shell keeps of what it has said.
/// </summary>
/// <remarks>
/// The thing this replaces is a per-page <c>InfoBar</c>, which could hold exactly one message, only
/// while the user stayed on the page that raised it. So the cases worth pinning are the ones that
/// were impossible before: a second message not destroying the first, a message outliving the
/// moment, and — the one that makes a history readable rather than a log — a condition repeating
/// every tick not filling the whole list with itself.
/// </remarks>
public class NotificationCenterTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static (NotificationCenter Centre, FakeTimeProvider Clock) Rig()
    {
        var clock = new FakeTimeProvider(Start);
        return (new NotificationCenter(clock), clock);
    }

    [Fact]
    public void A_second_message_does_not_destroy_the_first()
    {
        // The whole complaint. One bar held one line, so anything that happened twice left only the
        // second thing on screen and no way at all to find the first.
        var (centre, _) = Rig();

        centre.Success("Rig Fan Control approved.");
        centre.Error("The change was refused.");

        Assert.Equal(2, centre.History.Count);
        Assert.Equal("The change was refused.", centre.History[0].Title);
        Assert.Equal("Rig Fan Control approved.", centre.History[1].Title);
    }

    [Fact]
    public void The_newest_is_first()
    {
        // The list is read from the top and mostly only the top is read.
        var (centre, _) = Rig();

        centre.Inform("one");
        centre.Inform("two");
        centre.Inform("three");

        Assert.Equal(["three", "two", "one"], centre.History.Select(entry => entry.Title));
    }

    [Fact]
    public void A_condition_repeating_every_tick_stays_one_line()
    {
        // The engine ticks once a second and several failures repeat with it. Without folding, one
        // stuck condition is the entire history and everything worth keeping has been pushed out.
        var (centre, clock) = Rig();

        for (var second = 0; second < 30; second++)
        {
            centre.Error("The change could not be saved.", "The engine is not running.");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var entry = Assert.Single(centre.History);

        Assert.Equal(30, entry.Repeats);
        Assert.Equal("×30", entry.CountText);
    }

    [Fact]
    public void A_repeat_carries_the_time_it_last_happened()
    {
        // "17 minutes ago, and 36 times" would be a lie about the last one. The count is history;
        // the time is now.
        var (centre, clock) = Rig();

        centre.Error("Not connected to the engine.");
        clock.Advance(TimeSpan.FromSeconds(8));
        centre.Error("Not connected to the engine.");

        Assert.Equal(Start.AddSeconds(8), Assert.Single(centre.History).At);
    }

    [Fact]
    public void The_same_thing_much_later_is_a_new_line()
    {
        // Folding is for a condition repeating, not for a thing that happened twice in an evening.
        var (centre, clock) = Rig();

        centre.Warn("Saved, with something worth knowing.");
        clock.Advance(TimeSpan.FromMinutes(5));
        centre.Warn("Saved, with something worth knowing.");

        Assert.Equal(2, centre.History.Count);
        Assert.All(centre.History, entry => Assert.Equal(1, entry.Repeats));
    }

    [Fact]
    public void Only_the_newest_line_folds()
    {
        // Two conditions alternating are two things happening. Collapsing them into two counters
        // would hide the alternation, which is usually the interesting part.
        var (centre, _) = Rig();

        centre.Error("A");
        centre.Error("B");
        centre.Error("A");

        Assert.Equal(3, centre.History.Count);
    }

    [Fact]
    public void A_different_detail_under_the_same_headline_is_a_new_line()
    {
        var (centre, _) = Rig();

        centre.Error("The change was refused.", "System Fan #3 has no such curve.");
        centre.Error("The change was refused.", "CPU Fan has no such curve.");

        Assert.Equal(2, centre.History.Count);
    }

    [Fact]
    public void The_history_is_bounded()
    {
        // Fifty is far more than anyone will read, and unbounded is a window left open for a week
        // holding every message it ever showed.
        var (centre, _) = Rig();

        for (var index = 0; index < NotificationCenter.Capacity + 20; index++)
        {
            centre.Inform($"message {index}");
        }

        Assert.Equal(NotificationCenter.Capacity, centre.History.Count);
        Assert.Equal($"message {NotificationCenter.Capacity + 19}", centre.History[0].Title);
    }

    [Fact]
    public void The_badge_counts_what_has_not_been_read()
    {
        var (centre, _) = Rig();

        centre.Inform("one");
        centre.Inform("two");

        Assert.Equal(2, centre.UnreadCount);

        centre.MarkAllRead();

        Assert.Equal(0, centre.UnreadCount);
        Assert.Equal(2, centre.History.Count);
    }

    [Fact]
    public void A_repeat_does_not_count_as_something_new()
    {
        // A badge showing 47 for one stuck condition is a badge nobody looks at twice.
        var (centre, clock) = Rig();

        centre.Error("Not connected to the engine.");
        centre.MarkAllRead();

        clock.Advance(TimeSpan.FromSeconds(2));
        centre.Error("Not connected to the engine.");

        Assert.Equal(0, centre.UnreadCount);
    }

    [Fact]
    public void Every_post_reaches_whatever_shows_toasts()
    {
        var (centre, clock) = Rig();
        var seen = new List<ShellNotification>();

        centre.Posted += (_, entry) => seen.Add(entry);

        centre.Success("approved");
        clock.Advance(TimeSpan.FromSeconds(1));
        centre.Success("approved");

        // Twice, and the same instance both times: that is what lets a toast already on screen
        // restart its own timer instead of a second identical card appearing beneath it.
        Assert.Equal(2, seen.Count);
        Assert.Same(seen[0], seen[1]);
    }

    [Fact]
    public void An_exception_reaches_the_user_as_its_message_and_nothing_else()
    {
        // A type name teaches nobody anything. The stack trace belongs in the log.
        var (centre, _) = Rig();

        centre.Error("The plugin was not changed.", new InvalidOperationException("The engine is not running."));

        var entry = Assert.Single(centre.History);

        Assert.Equal("The engine is not running.", entry.Message);
        Assert.DoesNotContain("InvalidOperation", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clearing_empties_it_and_the_badge_with_it()
    {
        var (centre, _) = Rig();

        centre.Inform("one");
        centre.Clear();

        Assert.True(centre.IsEmpty);
        Assert.Equal(0, centre.UnreadCount);
    }

    [Fact]
    public void A_message_with_no_title_is_a_programming_error_rather_than_a_blank_line()
    {
        var (centre, _) = Rig();

        Assert.Throws<ArgumentException>(() => centre.Inform("   "));
    }

    [Fact]
    public void Everything_lands_on_the_thread_the_list_is_bound_to()
    {
        // The history is bound to a list that throws if it is touched from anywhere but the UI
        // thread, and a view model posts from wherever its continuation happened to resume.
        var (centre, _) = Rig();
        var posts = 0;

        centre.Dispatcher = new CountingDispatcher(() => posts++);
        centre.Error("The change could not be saved.");

        Assert.Equal(1, posts);
        Assert.Single(centre.History);
    }

    private sealed class CountingDispatcher(Action onPost) : App.ViewModels.IUiDispatcher
    {
        public void Post(Action action)
        {
            onPost();
            action();
        }
    }
}
