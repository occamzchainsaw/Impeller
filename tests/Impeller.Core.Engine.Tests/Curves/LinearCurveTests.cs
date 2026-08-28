using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests.Curves;

public class LinearCurveTests
{
    private static readonly SensorId Cpu = SensorId.New();

    private static LinearCurve Build(HysteresisSettings hysteresis = default) =>
        new(
            CurveId.New(),
            "CPU ramp",
            Cpu,
            minimumInput: 40f,
            maximumInput: 80f,
            minimumDuty: new Duty(20f),
            maximumDuty: new Duty(100f),
            hysteresis);

    [Theory]
    [InlineData(20f, 20f)]   // below the ramp
    [InlineData(40f, 20f)]   // at the start
    [InlineData(60f, 60f)]   // midpoint
    [InlineData(80f, 100f)]  // at the top
    [InlineData(95f, 100f)]  // beyond the top
    public void Interpolates_between_the_endpoints_and_holds_flat_outside(float input, float expected)
    {
        var curve = Build();
        var context = new TestCurveContext().WithSensor(Cpu, input);

        Assert.Equal(expected, curve.Evaluate(context)!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Missing_reading_produces_no_value_so_the_engine_holds()
    {
        var curve = Build();
        var context = new TestCurveContext().WithSensor(Cpu, null);

        Assert.Null(curve.Evaluate(context));
    }

    [Fact]
    public void Degenerate_range_behaves_as_a_step_rather_than_dividing_by_zero()
    {
        var curve = new LinearCurve(
            CurveId.New(),
            "step",
            Cpu,
            minimumInput: 50f,
            maximumInput: 50f,
            minimumDuty: new Duty(10f),
            maximumDuty: new Duty(90f));

        Assert.Equal(10f, curve.Evaluate(new TestCurveContext().WithSensor(Cpu, 49f))!.Value.Percent);
        Assert.Equal(90f, curve.Evaluate(new TestCurveContext().WithSensor(Cpu, 50f))!.Value.Percent);
    }

    [Fact]
    public void Saturated_inputs_bypass_suppression_so_a_hot_chip_is_answered_immediately()
    {
        // A long response time would otherwise delay the ramp to full.
        var curve = Build(new HysteresisSettings(ResponseUp: TimeSpan.FromSeconds(30)));
        var context = new TestCurveContext();

        curve.Evaluate(context.WithSensor(Cpu, 45f));

        Assert.Equal(100f, curve.Evaluate(context.WithSensor(Cpu, 85f))!.Value.Percent);
    }

    [Fact]
    public void Hysteresis_suppresses_small_wobbles_inside_the_ramp()
    {
        var curve = Build(new HysteresisSettings(DeadbandUp: 5f, DeadbandDown: 5f));
        var context = new TestCurveContext();

        var first = curve.Evaluate(context.WithSensor(Cpu, 60f))!.Value.Percent;
        var second = curve.Evaluate(context.WithSensor(Cpu, 62f))!.Value.Percent;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Reset_clears_suppression_state()
    {
        var curve = Build(new HysteresisSettings(DeadbandUp: 20f, DeadbandDown: 20f));
        var context = new TestCurveContext();

        curve.Evaluate(context.WithSensor(Cpu, 50f));
        curve.Reset();

        // After a reset the next reading is treated as the first, so it is adopted outright.
        Assert.Equal(70f, curve.Evaluate(context.WithSensor(Cpu, 65f))!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Declares_its_sensor_dependency()
    {
        Assert.Equal([Cpu], Build().SensorDependencies);
    }
}
