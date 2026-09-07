using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Core.Engine.Tests.Tuning;

/// <summary>
/// The floor a calibration measures has to reach the binding that enforces it.
/// </summary>
public class CalibrationFloorTests
{
    private static ControlBinding Bind(float minimum, float start, float stop)
    {
        var binding = new ControlBinding(SensorId.New())
        {
            MinimumDuty = new Duty(minimum),
            StartDuty = new Duty(start),
            StopDuty = new Duty(stop),
        };

        return binding;
    }

    /// <summary>
    /// The case from the field: calibration measured a fan turning at 40% and stalling below it,
    /// wrote a stop duty of 40, and the gate then switched the fan off at exactly the duty the
    /// floor was holding it at - with no way back, because the curve could not ask for less.
    /// </summary>
    [Fact]
    public void A_fan_is_not_switched_off_at_the_floor_it_was_told_to_hold()
    {
        var binding = Bind(minimum: 40f, start: 42f, stop: 40f);

        Assert.True(binding.StopThreshold < binding.MinimumDuty.Percent);
    }

    /// <summary>A fan with no floor keeps the threshold its calibration measured.</summary>
    [Fact]
    public void A_fan_with_no_floor_keeps_the_measured_stop_duty()
    {
        var binding = Bind(minimum: 0f, start: 42f, stop: 40f);

        Assert.Equal(40f, binding.StopThreshold, precision: 3);
    }

    /// <summary>A floor below the stop duty is the user's, and is left alone.</summary>
    [Fact]
    public void A_floor_below_the_stop_duty_does_not_move_the_threshold()
    {
        var binding = Bind(minimum: 10f, start: 42f, stop: 40f);

        Assert.Equal(9f, binding.StopThreshold, precision: 3);
    }

    /// <summary>The floor still applies to whatever a curve produces.</summary>
    [Fact]
    public void A_curve_asking_below_the_floor_gets_the_floor()
    {
        var binding = Bind(minimum: 40f, start: 42f, stop: 40f);

        Assert.Equal(40f, binding.ApplyLimits(new Duty(12f)).Percent, precision: 3);
    }
}
