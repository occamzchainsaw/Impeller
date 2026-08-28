using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests.Curves;

public class MixCurveTests
{
    private static readonly CurveId Cpu = CurveId.New();
    private static readonly CurveId Gpu = CurveId.New();
    private static readonly CurveId Drive = CurveId.New();

    private static MixCurve Build(MixFunction function, params CurveId[] sources) =>
        new(CurveId.New(), "mix", function, sources);

    private static TestCurveContext Inputs(float? cpu, float? gpu, float? drive = null) =>
        new TestCurveContext()
            .WithCurve(Cpu, cpu is { } c ? new Duty(c) : null)
            .WithCurve(Gpu, gpu is { } g ? new Duty(g) : null)
            .WithCurve(Drive, drive is { } d ? new Duty(d) : null);

    [Theory]
    [InlineData(MixFunction.Maximum, 70f)]
    [InlineData(MixFunction.Minimum, 30f)]
    [InlineData(MixFunction.Average, 50f)]
    [InlineData(MixFunction.Sum, 100f)]
    [InlineData(MixFunction.Difference, 0f)]
    public void Combines_inputs_according_to_its_function(MixFunction function, float expected)
    {
        var curve = Build(function, Cpu, Gpu);

        Assert.Equal(expected, curve.Evaluate(Inputs(30f, 70f))!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Difference_subtracts_later_inputs_from_the_first()
    {
        var curve = Build(MixFunction.Difference, Cpu, Gpu);

        Assert.Equal(50f, curve.Evaluate(Inputs(80f, 30f))!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Sum_saturates_at_full_rather_than_overflowing()
    {
        var curve = Build(MixFunction.Sum, Cpu, Gpu);

        Assert.Equal(100f, curve.Evaluate(Inputs(70f, 60f))!.Value.Percent);
    }

    [Fact]
    public void Difference_saturates_at_off_rather_than_going_negative()
    {
        var curve = Build(MixFunction.Difference, Cpu, Gpu);

        Assert.Equal(0f, curve.Evaluate(Inputs(20f, 60f))!.Value.Percent);
    }

    [Fact]
    public void An_input_with_no_value_is_skipped_rather_than_counted_as_zero()
    {
        // A dead sensor must not drag a minimum mix down to silence.
        var curve = Build(MixFunction.Minimum, Cpu, Gpu);

        Assert.Equal(40f, curve.Evaluate(Inputs(40f, null))!.Value.Percent);
    }

    [Fact]
    public void Average_divides_by_the_number_of_inputs_that_reported()
    {
        var curve = Build(MixFunction.Average, Cpu, Gpu, Drive);

        // 40 and 60 reported; the third contributes nothing, including to the divisor.
        Assert.Equal(50f, curve.Evaluate(Inputs(40f, 60f))!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Produces_no_value_when_nothing_reported()
    {
        var curve = Build(MixFunction.Maximum, Cpu, Gpu);

        Assert.Null(curve.Evaluate(Inputs(null, null)));
    }

    [Fact]
    public void Produces_no_value_when_it_has_no_inputs_at_all()
    {
        Assert.Null(Build(MixFunction.Maximum).Evaluate(Inputs(50f, 50f)));
    }

    [Fact]
    public void Declares_its_curve_dependencies()
    {
        Assert.Equal([Cpu, Gpu], Build(MixFunction.Maximum, Cpu, Gpu).CurveDependencies);
    }
}
