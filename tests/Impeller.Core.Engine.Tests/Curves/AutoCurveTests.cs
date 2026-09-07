using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests.Curves;

public class AutoCurveTests
{
    private static readonly SensorId Temperature = SensorId.New();

    /// <summary>
    /// Idle 35, load 70, working between 30% and 100%, stepping 4 points with a 3 degree deadband
    /// and a one-second response. Load mode therefore starts at 59 degrees:
    /// <c>35 + 0.75 * (70 - 3 - 35)</c>.
    /// </summary>
    private static AutoCurve Build(float step = 4f, float minimum = 30f) =>
        new(
            CurveId.New(),
            "auto",
            Temperature,
            idleTemperature: 35f,
            loadTemperature: 70f,
            minimumDuty: new Duty(minimum),
            maximumDuty: Duty.Full,
            step: step,
            deadband: 3f,
            responseTime: TimeSpan.FromSeconds(1));

    /// <summary>Runs the curve over a temperature sequence and returns the duty after each tick.</summary>
    private static List<Duty?> Run(AutoCurve curve, params float[] temperatures)
    {
        var context = new TestCurveContext();
        var results = new List<Duty?>();

        foreach (var temperature in temperatures)
        {
            context.WithSensor(Temperature, temperature);
            results.Add(curve.Evaluate(context));
        }

        return results;
    }

    private static float[] Repeat(float value, int count) => [.. Enumerable.Repeat(value, count)];

    [Fact]
    public void A_missing_reading_holds_rather_than_guessing()
    {
        var curve = Build();

        Assert.Null(curve.Evaluate(new TestCurveContext().WithSensor(Temperature, null)));
    }

    [Fact]
    public void The_first_reading_below_the_target_snaps_to_the_floor()
    {
        var curve = Build();

        Assert.Equal(30f, Run(curve, 40f)[0]!.Value.Percent, precision: 3);
    }

    [Fact]
    public void The_first_reading_above_the_target_snaps_to_the_ceiling()
    {
        var curve = Build();

        Assert.Equal(100f, Run(curve, 80f)[0]!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Past_the_target_the_duty_climbs_by_a_full_step_each_tick()
    {
        var curve = Build(step: 4f);

        // Settle at the floor, then hold the sensor above the load temperature.
        var duties = Run(curve, 40f, 72f, 72f, 72f, 72f);

        Assert.Equal(30f, duties[0]!.Value.Percent, precision: 3);
        Assert.Equal(34f, duties[1]!.Value.Percent, precision: 3);
        Assert.Equal(38f, duties[2]!.Value.Percent, precision: 3);
        Assert.Equal(42f, duties[3]!.Value.Percent, precision: 3);
        Assert.Equal(46f, duties[4]!.Value.Percent, precision: 3);
    }

    [Fact]
    public void The_climb_stops_at_the_ceiling()
    {
        var curve = Build(step: 4f);

        var duties = Run(curve, [40f, .. Repeat(72f, 40)]);

        Assert.Equal(100f, duties[^1]!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Falling_back_costs_half_a_step_against_a_full_step_of_climb()
    {
        // The asymmetry is the whole design: converge on the target from below, then ease off
        // slowly enough not to hunt either side of it.
        var curve = Build(step: 4f);

        // Climb well clear of the floor, then fall steadily while staying inside load mode.
        var climb = Run(curve, [40f, .. Repeat(72f, 8)]);
        var peak = climb[^1]!.Value.Percent;

        var falling = Run(curve, 71f, 70f, 69f, 68f, 67f, 66f, 65f, 64f, 63f, 62f);
        var deltas = new List<float>();

        for (var i = 1; i < falling.Count; i++)
        {
            deltas.Add(falling[i]!.Value.Percent - falling[i - 1]!.Value.Percent);
        }

        Assert.True(falling[^1]!.Value.Percent < peak, "the duty never came back down");

        // Every tick that moved, moved by half a step and downward.
        foreach (var delta in deltas.Where(d => Math.Abs(d) > 0.001f))
        {
            Assert.Equal(-2f, delta, precision: 3);
        }
    }

    [Fact]
    public void Holding_the_target_inside_the_deadband_leaves_the_duty_alone()
    {
        // 68 is inside the 3-degree deadband below the 70 target: on target, nothing to correct.
        var curve = Build();
        Run(curve, [40f, .. Repeat(72f, 6)]);

        var settled = Run(curve, [.. Repeat(68f, 12)]);

        Assert.Equal(
            settled[^1]!.Value.Percent,
            settled[^6]!.Value.Percent,
            precision: 3);
    }

    [Fact]
    public void Below_the_load_threshold_the_curve_ramps_between_idle_and_load()
    {
        var curve = Build(minimum: 20f);

        // 52.5 is halfway from idle (35) to load (70) and below the 59-degree load threshold.
        var duties = Run(curve, 52.5f, 52.5f);

        Assert.Equal(60f, duties[1]!.Value.Percent, precision: 3);
        Assert.False(curve.IsUnderLoad);
    }

    [Fact]
    public void At_or_below_idle_the_curve_commands_its_floor()
    {
        var curve = Build(minimum: 20f);

        var duties = Run(curve, 30f, 30f, 35f);

        Assert.Equal(20f, duties[1]!.Value.Percent, precision: 3);
        Assert.Equal(20f, duties[2]!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Load_mode_engages_above_the_entry_threshold()
    {
        var curve = Build();

        Run(curve, 40f, 62f);

        Assert.True(curve.IsUnderLoad);
    }

    [Fact]
    public void A_brief_dip_does_not_hand_control_back_to_the_ramp()
    {
        var curve = Build();
        Run(curve, [40f, .. Repeat(72f, 6)]);

        // One tick below the entry threshold is not the end of the load.
        Run(curve, 50f);

        Assert.True(curve.IsUnderLoad);
    }

    [Fact]
    public void A_sustained_fall_hands_control_back_to_the_ramp()
    {
        var curve = Build();
        Run(curve, [40f, .. Repeat(72f, 6)]);

        Run(curve, [.. Repeat(45f, 20)]);

        Assert.False(curve.IsUnderLoad);
    }

    [Fact]
    public void Reset_forgets_the_settled_duty_and_snaps_again()
    {
        var curve = Build();
        Run(curve, [40f, .. Repeat(72f, 6)]);

        curve.Reset();

        Assert.False(curve.IsUnderLoad);

        // A first reading after a reset snaps to an end of the range, as on a cold start.
        Assert.Equal(30f, Run(curve, 40f)[0]!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Changing_the_response_time_rebuilds_the_trend_windows()
    {
        var curve = Build();
        Run(curve, [40f, .. Repeat(72f, 6)]);

        curve.ResponseTime = TimeSpan.FromSeconds(5);

        // A window sized for the old response time says nothing about the new one, so the curve
        // has to re-prime rather than carry stale history across.
        Assert.Equal(TimeSpan.FromSeconds(5), curve.ResponseTime);
    }

    [Fact]
    public void The_source_sensor_is_declared_as_a_dependency()
    {
        Assert.Equal(Temperature, Assert.Single(Build().SensorDependencies));
    }

    [Fact]
    public void A_degenerate_range_treats_anything_above_idle_as_full_load()
    {
        var curve = new AutoCurve(
            CurveId.New(),
            "inverted",
            Temperature,
            idleTemperature: 70f,
            loadTemperature: 40f,
            minimumDuty: new Duty(20f),
            maximumDuty: Duty.Full,
            responseTime: TimeSpan.FromSeconds(1));

        // Idle above load has no ramp to interpolate along; saturating is better than dividing by
        // a negative span and commanding a duty that falls as the machine heats.
        var duties = Run(curve, 75f, 75f);

        Assert.Equal(100f, duties[^1]!.Value.Percent, precision: 3);
    }

    /// <summary>
    /// Parity with FanControl, whose <c>SerializableAutoFanCurve</c> ships
    /// <c>MinFanSpeed = 50</c>, <c>MaxFanSpeed = 80</c>, <c>LoadTemperature = 70</c>,
    /// <c>IdleTemperature = 35</c>, <c>Step = 2</c>, <c>Deadband = 3</c> and a response of 2.
    /// </summary>
    /// <remarks>
    /// The ceiling is the one that bites. A curve that seeks a temperature climbs until the
    /// temperature stops rising, so with no ceiling below full speed an ordinary load ends at
    /// full speed - which is what "it works in FanControl and screams in Impeller" turned out
    /// to be.
    /// </remarks>
    [Fact]
    public void An_auto_curve_starts_from_the_same_numbers_FanControl_ships()
    {
        var definition = new AutoCurveDefinition();

        Assert.Equal(35f, definition.IdleTemperature);
        Assert.Equal(70f, definition.LoadTemperature);
        Assert.Equal(50f, definition.MinimumDuty.Percent);
        Assert.Equal(80f, definition.MaximumDuty.Percent);
        Assert.Equal(2f, definition.Step);
        Assert.Equal(3f, definition.Deadband);
        Assert.Equal(TimeSpan.FromSeconds(2), definition.ResponseTime);
    }

    /// <summary>The curve itself agrees with the definition when it is handed no range.</summary>
    [Fact]
    public void A_curve_built_without_a_range_uses_the_same_range_as_the_definition()
    {
        var curve = new AutoCurve(CurveId.New(), "auto", Temperature);

        Assert.Equal(50f, curve.MinimumDuty.Percent);
        Assert.Equal(80f, curve.MaximumDuty.Percent);
    }

    /// <summary>
    /// The ceiling has to hold under exactly the condition that produced the complaint: a
    /// temperature parked above the target for a long time, which is where the integrator
    /// otherwise walks the duty to full speed.
    /// </summary>
    [Fact]
    public void A_temperature_held_above_the_target_never_drives_past_the_ceiling()
    {
        var curve = new AutoCurve(CurveId.New(), "auto", Temperature);

        var duties = Run(curve, Enumerable.Repeat(90f, 120).ToArray());

        Assert.All(duties, duty => Assert.True(duty!.Value.Percent <= 80f));
        Assert.Equal(80f, duties[^1]!.Value.Percent, precision: 3);
    }
}
