using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests.Curves;

public class GraphCurveTests
{
    private static readonly SensorId Gpu = SensorId.New();

    private static GraphCurve Build(params CurvePoint[] points) =>
        new(CurveId.New(), "GPU graph", Gpu, points);

    private static GraphCurve Default() => Build(
        new CurvePoint(30f, 0f),
        new CurvePoint(50f, 40f),
        new CurvePoint(70f, 100f));

    [Theory]
    [InlineData(30f, 0f)]
    [InlineData(40f, 20f)]   // halfway along the first segment
    [InlineData(50f, 40f)]
    [InlineData(60f, 70f)]   // halfway along the second, which has a steeper slope
    [InlineData(70f, 100f)]
    public void Interpolates_linearly_within_each_segment(float input, float expected)
    {
        var curve = Default();
        var context = new TestCurveContext().WithSensor(Gpu, input);

        Assert.Equal(expected, curve.Evaluate(context)!.Value.Percent, precision: 3);
    }

    [Theory]
    [InlineData(10f, 0f)]
    [InlineData(120f, 100f)]
    public void Holds_flat_outside_the_drawn_range_rather_than_extrapolating(float input, float expected)
    {
        // Extrapolating past where the user stopped drawing would invent speeds they never chose.
        var curve = Default();
        var context = new TestCurveContext().WithSensor(Gpu, input);

        Assert.Equal(expected, curve.Evaluate(context)!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Points_are_sorted_regardless_of_the_order_they_arrive_in()
    {
        var curve = Build(
            new CurvePoint(70f, 100f),
            new CurvePoint(30f, 0f),
            new CurvePoint(50f, 40f));

        Assert.Equal([30f, 50f, 70f], curve.Points.Select(p => p.Input));
        Assert.Equal(20f, curve.Evaluate(new TestCurveContext().WithSensor(Gpu, 40f))!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Coincident_inputs_form_a_step()
    {
        var curve = Build(
            new CurvePoint(50f, 20f),
            new CurvePoint(50f, 80f));

        Assert.Equal(20f, curve.Evaluate(new TestCurveContext().WithSensor(Gpu, 49f))!.Value.Percent, precision: 3);
        Assert.Equal(80f, curve.Evaluate(new TestCurveContext().WithSensor(Gpu, 50f))!.Value.Percent, precision: 3);
    }

    [Fact]
    public void A_single_point_behaves_as_a_flat_curve()
    {
        var curve = Build(new CurvePoint(50f, 65f));

        Assert.Equal(65f, curve.Evaluate(new TestCurveContext().WithSensor(Gpu, 10f))!.Value.Percent);
        Assert.Equal(65f, curve.Evaluate(new TestCurveContext().WithSensor(Gpu, 90f))!.Value.Percent);
    }

    [Fact]
    public void An_empty_curve_commands_off_rather_than_throwing()
    {
        var curve = Build();

        Assert.Equal(0f, curve.Evaluate(new TestCurveContext().WithSensor(Gpu, 60f))!.Value.Percent);
    }

    [Fact]
    public void SetPoints_replaces_and_re_sorts_the_vertices()
    {
        var curve = Default();
        curve.SetPoints([new CurvePoint(80f, 100f), new CurvePoint(20f, 10f)]);

        Assert.Equal([20f, 80f], curve.Points.Select(p => p.Input));
        Assert.Equal(55f, curve.Evaluate(new TestCurveContext().WithSensor(Gpu, 50f))!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Missing_reading_produces_no_value()
    {
        Assert.Null(Default().Evaluate(new TestCurveContext().WithSensor(Gpu, null)));
    }
}
