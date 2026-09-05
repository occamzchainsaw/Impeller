using Impeller.App.ViewModels;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// What happens to the curves reading a computed sensor when it is deleted.
/// </summary>
/// <remarks>
/// The same argument curve deletion makes about the fans bound to it: a curve left pointing at a
/// sensor that no longer exists reads nothing and explains nothing. Cleared, it says "no sensor
/// chosen yet", which is true and on screen. Tested here rather than by deleting things on a real
/// machine, because getting it wrong quietly breaks a configuration the user cares about.
/// </remarks>
public sealed class CustomSensorDeletionTests
{
    private static readonly SensorId Doomed = SensorId.New();
    private static readonly SensorId Other = SensorId.New();

    [Fact]
    public void A_linear_curve_reading_it_is_left_with_no_sensor()
    {
        var curve = new LinearCurveDefinition { Name = "Ramp", Source = Doomed };

        var after = Assert.IsType<LinearCurveDefinition>(SensorsViewModel.Unread(curve, Doomed));

        Assert.True(after.Source.IsNone);
        Assert.Equal("Ramp", after.Name);
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("trigger")]
    [InlineData("auto")]
    public void Every_shape_that_reads_a_sensor_is_cleared(string shape)
    {
        CurveDefinition curve = shape switch
        {
            "graph" => new GraphCurveDefinition { Source = Doomed },
            "trigger" => new TriggerCurveDefinition { Source = Doomed },
            _ => new AutoCurveDefinition { Source = Doomed },
        };

        Assert.True(SensorsViewModel.Reads(curve, Doomed));

        var after = SensorsViewModel.Unread(curve, Doomed);

        Assert.False(SensorsViewModel.Reads(after, Doomed));
    }

    [Fact]
    public void A_curve_reading_something_else_is_untouched()
    {
        var curve = new AutoCurveDefinition { Name = "Auto GPU", Source = Other };

        Assert.False(SensorsViewModel.Reads(curve, Doomed));
        Assert.Same(curve, SensorsViewModel.Unread(curve, Doomed));
    }

    [Fact]
    public void The_shapes_that_read_no_sensor_are_never_touched()
    {
        // A flat curve is a constant, a mix combines other curves, and a sync follows another fan.
        // None of them can be reading a sensor, so none of them can be reading this one.
        CurveDefinition[] curves =
        [
            new FlatCurveDefinition { Duty = Duty.Full },
            new MixCurveDefinition(),
            new SyncCurveDefinition(),
        ];

        foreach (var curve in curves)
        {
            Assert.False(SensorsViewModel.Reads(curve, Doomed), curve.GetType().Name);
            Assert.Same(curve, SensorsViewModel.Unread(curve, Doomed));
        }
    }

    [Theory]
    [InlineData(0, "Nothing reads it, so nothing else changes.")]
    [InlineData(1, "One curve reads it, and will be left with no sensor chosen.")]
    [InlineData(3, "3 things read it, and will be left with no sensor chosen.")]
    public void The_cost_of_deleting_it_is_said_before_it_happens(int readers, string expected)
    {
        Assert.Equal(expected, SensorsViewModel.Cost(readers));
    }
}
