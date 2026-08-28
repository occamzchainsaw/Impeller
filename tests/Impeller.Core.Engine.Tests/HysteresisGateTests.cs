namespace Impeller.Core.Engine.Tests;

public class HysteresisGateTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    [Fact]
    public void First_reading_is_always_accepted()
    {
        // Nothing to compare against, so suppression cannot apply.
        var gate = new HysteresisGate(new HysteresisSettings(DeadbandUp: 10f, DeadbandDown: 10f));

        Assert.Equal(50f, gate.Offer(50f, Tick));
    }

    [Fact]
    public void Change_inside_the_deadband_is_ignored()
    {
        var gate = new HysteresisGate(new HysteresisSettings(DeadbandUp: 3f, DeadbandDown: 3f));
        gate.Offer(50f, Tick);

        Assert.Equal(50f, gate.Offer(52f, Tick));
        Assert.Equal(50f, gate.Offer(48f, Tick));
    }

    [Fact]
    public void Change_beyond_the_deadband_is_accepted_when_no_response_time_is_set()
    {
        var gate = new HysteresisGate(new HysteresisSettings(DeadbandUp: 3f, DeadbandDown: 3f));
        gate.Offer(50f, Tick);

        Assert.Equal(55f, gate.Offer(55f, Tick));
    }

    [Fact]
    public void Rising_and_falling_deadbands_apply_independently()
    {
        // Wide on the way up, narrow on the way down.
        var gate = new HysteresisGate(new HysteresisSettings(DeadbandUp: 10f, DeadbandDown: 1f));
        gate.Offer(50f, Tick);

        Assert.Equal(50f, gate.Offer(55f, Tick));
        Assert.Equal(45f, gate.Offer(45f, Tick));
    }

    [Fact]
    public void Change_must_persist_for_the_response_time_before_it_is_accepted()
    {
        var gate = new HysteresisGate(new HysteresisSettings(ResponseUp: TimeSpan.FromSeconds(3)));
        gate.Offer(50f, Tick);

        Assert.Equal(50f, gate.Offer(70f, Tick));
        Assert.Equal(50f, gate.Offer(70f, Tick));
        Assert.Equal(70f, gate.Offer(70f, Tick));
    }

    [Fact]
    public void A_brief_spike_never_reaches_the_curve()
    {
        // The whole point: two seconds of heat on a three-second response changes nothing.
        var gate = new HysteresisGate(new HysteresisSettings(ResponseUp: TimeSpan.FromSeconds(3)));
        gate.Offer(50f, Tick);

        gate.Offer(90f, Tick);
        gate.Offer(90f, Tick);

        Assert.Equal(50f, gate.Offer(50f, Tick));
    }

    [Fact]
    public void Reversal_restarts_the_response_clock()
    {
        var settings = new HysteresisSettings(
            ResponseUp: TimeSpan.FromSeconds(2),
            ResponseDown: TimeSpan.FromSeconds(2));
        var gate = new HysteresisGate(settings);
        gate.Offer(50f, Tick);

        // One second of rising, then a fall: the accumulated rise must not count toward the fall.
        gate.Offer(70f, Tick);
        Assert.Equal(50f, gate.Offer(30f, Tick));
        Assert.Equal(30f, gate.Offer(30f, Tick));
    }

    [Fact]
    public void Rising_and_falling_response_times_apply_independently()
    {
        var settings = new HysteresisSettings(
            ResponseUp: TimeSpan.FromSeconds(1),
            ResponseDown: TimeSpan.FromSeconds(4));
        var gate = new HysteresisGate(settings);
        gate.Offer(50f, Tick);

        // Ramping up is prompt.
        Assert.Equal(70f, gate.Offer(70f, Tick));

        // Ramping back down is deliberately reluctant.
        Assert.Equal(70f, gate.Offer(50f, Tick));
        Assert.Equal(70f, gate.Offer(50f, Tick));
        Assert.Equal(70f, gate.Offer(50f, Tick));
        Assert.Equal(50f, gate.Offer(50f, Tick));
    }

    [Fact]
    public void Bypass_accepts_immediately_regardless_of_deadband_or_response_time()
    {
        var settings = new HysteresisSettings(
            DeadbandUp: 20f,
            ResponseUp: TimeSpan.FromSeconds(30));
        var gate = new HysteresisGate(settings);
        gate.Offer(50f, Tick);

        Assert.Equal(95f, gate.Offer(95f, Tick, bypassSuppression: true));
    }

    [Fact]
    public void Elapsed_time_drives_acceptance_rather_than_call_count()
    {
        // One long tick satisfies the response time that three short ones would not.
        var gate = new HysteresisGate(new HysteresisSettings(ResponseUp: TimeSpan.FromSeconds(5)));
        gate.Offer(50f, Tick);

        Assert.Equal(70f, gate.Offer(70f, TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void Reset_discards_history_so_the_next_reading_is_a_first_reading()
    {
        var gate = new HysteresisGate(new HysteresisSettings(DeadbandUp: 20f, DeadbandDown: 20f));
        gate.Offer(50f, Tick);
        Assert.Equal(50f, gate.Offer(60f, Tick));

        gate.Reset();

        Assert.Null(gate.AcceptedValue);
        Assert.Equal(60f, gate.Offer(60f, Tick));
    }
}
