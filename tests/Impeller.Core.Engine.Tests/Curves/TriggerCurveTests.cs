using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests.Curves;

public class TriggerCurveTests
{
    private static readonly SensorId Cpu = SensorId.New();

    private static TriggerCurve Build(TimeSpan responseUp = default, TimeSpan responseDown = default) =>
        new(
            CurveId.New(),
            "boost",
            Cpu,
            idleInput: 50f,
            loadInput: 70f,
            idleDuty: new Duty(25f),
            loadDuty: new Duty(90f),
            responseUp,
            responseDown);

    [Fact]
    public void Switches_to_load_at_the_upper_threshold()
    {
        var curve = Build();
        var context = new TestCurveContext();

        curve.Evaluate(context.WithSensor(Cpu, 40f));

        Assert.Equal(90f, curve.Evaluate(context.WithSensor(Cpu, 75f))!.Value.Percent);
        Assert.True(curve.IsUnderLoad);
    }

    [Fact]
    public void Holds_its_state_inside_the_band()
    {
        // This is the whole reason for two thresholds: between them nothing changes.
        var curve = Build();
        var context = new TestCurveContext();

        curve.Evaluate(context.WithSensor(Cpu, 75f));

        Assert.Equal(90f, curve.Evaluate(context.WithSensor(Cpu, 60f))!.Value.Percent);
        Assert.True(curve.IsUnderLoad);
    }

    [Fact]
    public void Returns_to_idle_only_after_falling_all_the_way_to_the_lower_threshold()
    {
        var curve = Build();
        var context = new TestCurveContext();

        curve.Evaluate(context.WithSensor(Cpu, 75f));
        curve.Evaluate(context.WithSensor(Cpu, 60f));

        Assert.Equal(25f, curve.Evaluate(context.WithSensor(Cpu, 48f))!.Value.Percent);
        Assert.False(curve.IsUnderLoad);
    }

    [Fact]
    public void A_reading_starting_inside_the_band_assumes_idle()
    {
        var curve = Build();
        var context = new TestCurveContext();

        Assert.Equal(25f, curve.Evaluate(context.WithSensor(Cpu, 60f))!.Value.Percent);
    }

    [Fact]
    public void Response_time_must_elapse_before_the_latch_flips()
    {
        var curve = Build(responseUp: TimeSpan.FromSeconds(3));
        var context = new TestCurveContext();

        curve.Evaluate(context.WithSensor(Cpu, 40f));

        Assert.Equal(25f, curve.Evaluate(context.WithSensor(Cpu, 75f))!.Value.Percent);
        Assert.Equal(25f, curve.Evaluate(context.WithSensor(Cpu, 75f))!.Value.Percent);
        Assert.Equal(90f, curve.Evaluate(context.WithSensor(Cpu, 75f))!.Value.Percent);
    }

    [Fact]
    public void A_spike_shorter_than_the_response_time_never_flips_the_latch()
    {
        var curve = Build(responseUp: TimeSpan.FromSeconds(5));
        var context = new TestCurveContext();

        curve.Evaluate(context.WithSensor(Cpu, 40f));
        curve.Evaluate(context.WithSensor(Cpu, 95f));
        curve.Evaluate(context.WithSensor(Cpu, 95f));

        Assert.Equal(25f, curve.Evaluate(context.WithSensor(Cpu, 40f))!.Value.Percent);
        Assert.False(curve.IsUnderLoad);
    }

    [Fact]
    public void Upward_and_downward_response_times_apply_independently()
    {
        var curve = Build(
            responseUp: TimeSpan.FromSeconds(1),
            responseDown: TimeSpan.FromSeconds(3));
        var context = new TestCurveContext();

        curve.Evaluate(context.WithSensor(Cpu, 40f));
        Assert.Equal(90f, curve.Evaluate(context.WithSensor(Cpu, 75f))!.Value.Percent);

        Assert.Equal(90f, curve.Evaluate(context.WithSensor(Cpu, 45f))!.Value.Percent);
        Assert.Equal(90f, curve.Evaluate(context.WithSensor(Cpu, 45f))!.Value.Percent);
        Assert.Equal(25f, curve.Evaluate(context.WithSensor(Cpu, 45f))!.Value.Percent);
    }

    [Fact]
    public void A_reading_returning_inside_the_band_abandons_a_part_accumulated_switch()
    {
        var curve = Build(responseUp: TimeSpan.FromSeconds(3));
        var context = new TestCurveContext();

        curve.Evaluate(context.WithSensor(Cpu, 40f));
        curve.Evaluate(context.WithSensor(Cpu, 75f));
        curve.Evaluate(context.WithSensor(Cpu, 60f));

        // The two seconds already banked must not carry over.
        Assert.Equal(25f, curve.Evaluate(context.WithSensor(Cpu, 75f))!.Value.Percent);
        Assert.Equal(25f, curve.Evaluate(context.WithSensor(Cpu, 75f))!.Value.Percent);
        Assert.Equal(90f, curve.Evaluate(context.WithSensor(Cpu, 75f))!.Value.Percent);
    }

    [Fact]
    public void Missing_reading_before_any_state_produces_no_value()
    {
        Assert.Null(Build().Evaluate(new TestCurveContext().WithSensor(Cpu, null)));
    }

    [Fact]
    public void Missing_reading_after_a_state_exists_holds_that_state()
    {
        var curve = Build();
        var context = new TestCurveContext();

        curve.Evaluate(context.WithSensor(Cpu, 80f));

        Assert.Equal(90f, curve.Evaluate(context.WithSensor(Cpu, null))!.Value.Percent);
    }

    [Fact]
    public void Reset_clears_the_latch()
    {
        var curve = Build();
        var context = new TestCurveContext();

        curve.Evaluate(context.WithSensor(Cpu, 80f));
        Assert.True(curve.IsUnderLoad);

        curve.Reset();

        Assert.False(curve.IsUnderLoad);
        Assert.Equal(25f, curve.Evaluate(context.WithSensor(Cpu, 60f))!.Value.Percent);
    }
}
