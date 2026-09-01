using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests;

public class StartStopGateTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// <summary>A fan that starts at 22% and stalls at or below 16%, with ramp limiting off.</summary>
    private static ControlBinding Binding(SensorId? controlId = null) =>
        new(controlId ?? SensorId.New())
        {
            StartDuty = new Duty(22f),
            StopDuty = new Duty(16f),
            MaximumStepUpPerSecond = 0f,
            MaximumStepDownPerSecond = 0f,
        };

    /// <summary>Runs the gate for a number of ticks, feeding each result back as the new current.</summary>
    private static List<Duty> Run(
        StartStopGate gate,
        Duty target,
        Duty start,
        int ticks,
        float? pairedRpm = null)
    {
        var current = start;
        var results = new List<Duty>();

        for (var i = 0; i < ticks; i++)
        {
            current = gate.Resolve(target, current, pairedRpm, Tick);
            results.Add(current);
        }

        return results;
    }

    [Theory]
    [InlineData(22f, 16f, 16f)]   // an explicit stop duty is the threshold
    [InlineData(22f, 0f, 21f)]    // start only: derive one just under it
    [InlineData(22f, 22f, 21f)]   // stop equal to start is the same as not setting one
    [InlineData(0f, 0f, 0f)]      // no start duty disables the feature entirely
    [InlineData(0f, 16f, 0f)]     // ...even when a stop duty was set
    public void The_stop_threshold_is_derived_from_whichever_values_were_set(
        float start,
        float stop,
        float expected)
    {
        var binding = new ControlBinding(SensorId.New())
        {
            StartDuty = new Duty(start),
            StopDuty = new Duty(stop),
        };

        Assert.Equal(expected, binding.StopThreshold, precision: 3);
    }

    [Fact]
    public void A_duty_under_the_stop_threshold_commands_off_rather_than_a_stall()
    {
        // 10% on a fan that stalls below 16% is not a slow fan. It is a stopped fan drawing
        // current and reporting nothing, which is strictly worse than being off.
        var gate = new StartStopGate(Binding());

        var result = gate.Resolve(new Duty(10f), new Duty(50f), pairedRpm: null, Tick);

        Assert.True(result.IsOff);
    }

    [Fact]
    public void A_duty_exactly_at_the_stop_threshold_also_commands_off()
    {
        var gate = new StartStopGate(Binding());

        Assert.True(gate.Resolve(new Duty(16f), new Duty(50f), pairedRpm: null, Tick).IsOff);
    }

    [Fact]
    public void A_duty_above_the_threshold_passes_through_once_the_fan_is_running()
    {
        var gate = new StartStopGate(Binding());

        // Already spinning at 40 and asked for 45: nothing to start, nothing to suppress.
        var result = gate.Resolve(new Duty(45f), new Duty(40f), pairedRpm: 900f, Tick);

        Assert.Equal(45f, result.Percent, precision: 3);
    }

    [Fact]
    public void Starting_from_rest_holds_the_start_duty_rather_than_the_target()
    {
        var gate = new StartStopGate(Binding());

        var result = gate.Resolve(new Duty(30f), Duty.Off, pairedRpm: 0f, Tick);

        Assert.Equal(22f, result.Percent, precision: 3);
        Assert.True(gate.IsStarting);
    }

    [Fact]
    public void A_tach_confirming_the_fan_is_turning_ends_the_kick_early()
    {
        // The timer is a fallback for when nothing better is available. A tach reporting real RPM
        // is better: the fan has started, so there is nothing left to wait out and the curve
        // should get its fan back on the very next tick.
        var gate = new StartStopGate(Binding());

        var duties = Run(gate, target: new Duty(60f), start: Duty.Off, ticks: 4, pairedRpm: 1200f);

        Assert.Equal(22f, duties[0].Percent, precision: 3);
        Assert.Equal(60f, duties[1].Percent, precision: 3);
        Assert.False(gate.IsStarting);
    }

    [Fact]
    public void A_fan_that_will_not_turn_is_crawled_upward_rather_than_left_at_the_start_duty()
    {
        // The tach is the only evidence that a start attempt failed. Without this the fan sits at
        // 22% forever, reporting zero, and nothing ever escalates.
        var gate = new StartStopGate(Binding());

        var duties = Run(gate, target: new Duty(60f), start: Duty.Off, ticks: 10, pairedRpm: 0f);

        Assert.Equal(22f, duties[0].Percent, precision: 3);
        Assert.True(duties[^1].Percent > 22f, "a stubbornly stopped fan was never pushed harder");

        // Creeping, not jumping: each escalation is a few points.
        Assert.Equal(3f, duties[^1].Percent - duties[^2].Percent, precision: 3);
    }

    [Fact]
    public void Without_a_tach_the_timer_alone_ends_the_kick()
    {
        var gate = new StartStopGate(Binding());

        var duties = Run(gate, target: new Duty(60f), start: Duty.Off, ticks: 8, pairedRpm: null);

        Assert.Equal(22f, duties[0].Percent, precision: 3);
        Assert.Equal(60f, duties[^1].Percent, precision: 3);
    }

    [Fact]
    public void A_stopped_fan_asked_for_a_duty_under_the_threshold_stays_off()
    {
        // No point starting something we are about to command back off.
        var gate = new StartStopGate(Binding());

        Assert.True(gate.Resolve(new Duty(12f), Duty.Off, pairedRpm: 0f, Tick).IsOff);
        Assert.False(gate.IsStarting);
    }

    [Fact]
    public void With_no_start_duty_the_gate_is_a_plain_ramp_limiter()
    {
        var binding = new ControlBinding(SensorId.New())
        {
            MaximumStepUpPerSecond = 10f,
            MaximumStepDownPerSecond = 10f,
        };
        var gate = new StartStopGate(binding);

        // Would have been suppressed as a stall if the feature were on; here it just ramps.
        var result = gate.Resolve(new Duty(100f), new Duty(5f), pairedRpm: null, Tick);

        Assert.Equal(15f, result.Percent, precision: 3);
    }

    [Fact]
    public void Reset_abandons_a_start_in_progress()
    {
        var gate = new StartStopGate(Binding());
        gate.Resolve(new Duty(60f), Duty.Off, pairedRpm: 0f, Tick);

        gate.Reset();

        Assert.False(gate.IsStarting);
    }

    [Fact]
    public void The_loop_writes_zero_to_a_control_whose_curve_asks_for_a_stalling_duty()
    {
        var registry = new FakeSensorRegistry();
        var fan = registry.Add(new FakeControl());
        var curve = new FlatCurve(CurveId.New(), "low", new Duty(10f));

        var loop = new ControlLoop(registry, new ControlOwnershipRegistry(TimeProvider.System), TimeProvider.System);
        loop.Configure(
            [curve],
            [
                new ControlBinding(fan.Id)
                {
                    CurveId = curve.Id,
                    StartDuty = new Duty(22f),
                    StopDuty = new Duty(16f),
                    MaximumStepUpPerSecond = 0f,
                    MaximumStepDownPerSecond = 0f,
                },
            ]);

        loop.Tick(Tick);

        Assert.True(fan.CommandedDuty!.Value.IsOff);
    }

    [Fact]
    public void The_loop_reads_the_paired_tach_when_deciding_a_start_has_worked()
    {
        var registry = new FakeSensorRegistry();
        var fan = registry.Add(new FakeControl());
        var tach = registry.Add(new FakeSensor(SensorKind.FanSpeed, "fan rpm") { Value = 0f });
        var curve = new FlatCurve(CurveId.New(), "run", new Duty(60f));

        var loop = new ControlLoop(registry, new ControlOwnershipRegistry(TimeProvider.System), TimeProvider.System);
        loop.Configure(
            [curve],
            [
                new ControlBinding(fan.Id)
                {
                    CurveId = curve.Id,
                    StartDuty = new Duty(22f),
                    StopDuty = new Duty(16f),
                    PairedFanSensorId = tach.Id,
                    MaximumStepUpPerSecond = 0f,
                    MaximumStepDownPerSecond = 0f,
                },
            ]);

        for (var i = 0; i < 5; i++)
        {
            loop.Tick(Tick);
        }

        // Still reporting zero RPM, so the engine is still trying rather than assuming success.
        Assert.Equal(22f, fan.CommandedDuty!.Value.Percent, precision: 3);

        tach.Value = 900f;
        loop.Tick(Tick);
        loop.Tick(Tick);

        Assert.Equal(60f, fan.CommandedDuty!.Value.Percent, precision: 3);
    }
}
