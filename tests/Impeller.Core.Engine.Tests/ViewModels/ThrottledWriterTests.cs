using Impeller.App.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// Covers the rate limiter behind the manual slider.
/// </summary>
/// <remarks>
/// <para>
/// The shape being checked is throttle-with-a-trailing-send, not debounce, and the difference is
/// the whole point: a debounce would leave a fan motionless during a slow drag and then jump it at
/// the end. So: the first value goes immediately, the ones behind it are paced, and the last one
/// always lands.
/// </para>
/// <para>
/// On a fake clock, because the alternative is a test that sleeps — and a sleeping test that
/// measures timing is a test that fails on a loaded machine.
/// </para>
/// </remarks>
public class ThrottledWriterTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task The_first_value_goes_immediately()
    {
        // Leading edge. A slider that waited a quarter of a second before the fan moved at all
        // would feel broken, whatever it did afterwards.
        var (time, sent, writer) = Build();

        await using (writer)
        {
            writer.Write(10f);
            await writer.DrainAsync();

            Assert.Equal([10f], sent);
        }
    }

    [Fact]
    public async Task Values_behind_it_are_paced_rather_than_all_sent()
    {
        var (time, sent, writer) = Build();

        await using (writer)
        {
            // Sequenced deliberately. Offering all four at once is a different test - see below -
            // because they collapse before anything is sent at all.
            writer.Write(10f);
            await Until(() => sent.Count == 1);
            Assert.Equal([10f], sent);

            foreach (var value in new[] { 20f, 30f, 40f })
            {
                writer.Write(value);
            }

            await AdvanceUntil(time, () => sent.Count == 2);

            // The newest, not the next in the queue. Everything between is superseded, which is
            // exactly right when the engine reads the requested duty once a tick anyway.
            Assert.Equal([10f, 40f], sent);
        }
    }

    [Fact]
    public async Task Values_offered_before_anything_has_been_sent_collapse_to_the_newest()
    {
        // Not a special case - the general rule, applying from the first value onward. A drag that
        // finishes before the pump is even scheduled produces one write, of the value the user
        // stopped on, which is the only one that was ever going to matter.
        var (_, sent, writer) = Build();

        await using (writer)
        {
            foreach (var value in new[] { 10f, 20f, 30f, 40f })
            {
                writer.Write(value);
            }

            await writer.DrainAsync();

            Assert.Equal([40f], sent);
        }
    }

    [Fact]
    public async Task The_last_value_always_lands()
    {
        // The one property a user would notice going wrong: let go of the slider at 63 and the fan
        // has to end up at 63, not at wherever the throttle happened to sample.
        var (time, sent, writer) = Build();

        await using (writer)
        {
            writer.Write(1f);
            await Until(() => sent.Count == 1);

            for (var value = 2; value <= 60; value++)
            {
                writer.Write(value);
            }

            // Stated as "ends on 60", not "the second send is 60". The pump runs on the thread pool
            // and can take a value mid-loop, so how many sends happen is genuinely unspecified -
            // where the fan finishes is not.
            await AdvanceUntil(time, () => sent.Count > 0 && sent[^1] == 60f);

            Assert.Equal(60f, sent[^1]);
        }
    }

    [Fact]
    public async Task It_stops_sending_once_the_value_stops_moving()
    {
        var (time, sent, writer) = Build();

        await using (writer)
        {
            writer.Write(10f);
            await Until(() => sent.Count == 1);

            writer.Write(20f);
            await AdvanceUntil(time, () => sent.Count == 2);

            // Nothing new offered, so nothing more is sent however long passes. A limiter that kept
            // re-sending the last value would be a limiter that never lets the pipe go quiet.
            for (var round = 0; round < 10; round++)
            {
                time.Advance(Interval);
                await Task.Delay(5);
            }

            Assert.Equal(2, sent.Count);
        }
    }

    [Fact]
    public async Task Only_one_send_runs_at_a_time()
    {
        // Without this, two writes can complete out of order and leave the fan holding the older
        // value — which no amount of rate limiting would fix.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var peak = 0;
        var time = new FakeTimeProvider();

        var writer = new ThrottledWriter<float>(time, Interval, async (_, _) =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref concurrent));
            await gate.Task;
            Interlocked.Decrement(ref concurrent);
        });

        await using (writer)
        {
            writer.Write(10f);
            await Until(() => concurrent == 1);

            writer.Write(20f);

            for (var round = 0; round < 5; round++)
            {
                time.Advance(Interval);
                await Task.Delay(5);
            }

            Assert.Equal(1, peak);

            gate.SetResult();
        }
    }

    [Fact]
    public async Task A_failing_send_does_not_stop_the_next_one()
    {
        // A dropped connection mid-drag is ordinary. A limiter that gave up on it would silently
        // disable the slider until the card was rebuilt.
        var time = new FakeTimeProvider();
        var sent = new List<float>();

        var writer = new ThrottledWriter<float>(time, Interval, (value, _) =>
        {
            if (value < 20f)
            {
                throw new IOException("the engine went away");
            }

            lock (sent)
            {
                sent.Add(value);
            }

            return Task.CompletedTask;
        });

        await using (writer)
        {
            writer.Write(10f);
            await Until(() => writer.Sends == 1);

            writer.Write(30f);

            await AdvanceUntil(time, () => sent.Count == 1);
            Assert.Equal([30f], sent);
        }
    }

    private static (FakeTimeProvider Time, List<float> Sent, ThrottledWriter<float> Writer) Build()
    {
        var time = new FakeTimeProvider();
        var sent = new List<float>();

        var writer = new ThrottledWriter<float>(time, Interval, (value, _) =>
        {
            lock (sent)
            {
                sent.Add(value);
            }

            return Task.CompletedTask;
        });

        return (time, sent, writer);
    }

    /// <summary>
    /// Nudges the clock until something happens.
    /// </summary>
    /// <remarks>
    /// The pump runs on the thread pool, so a single Advance can land before it has reached its
    /// delay - and a fake clock only fires timers that already exist when it is advanced. Advancing
    /// repeatedly removes that race without weakening what is being asserted: nothing here says
    /// "within one interval", only "eventually, and exactly once".
    /// </remarks>
    private static async Task AdvanceUntil(FakeTimeProvider time, Func<bool> condition)
    {
        for (var round = 0; round < 200; round++)
        {
            if (condition())
            {
                return;
            }

            time.Advance(Interval);
            await Task.Delay(5);
        }

        Assert.Fail("Timed out advancing the clock.");
    }

    /// <summary>Waits for the pump, which runs on the thread pool rather than on the fake clock.</summary>
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail("Timed out waiting for the writer.");
    }
}
