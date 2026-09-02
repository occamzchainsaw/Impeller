using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Core.Engine.Tests.Legacy;

/// <summary>
/// Covers reading a duty back out of a measured duty-to-speed table.
/// </summary>
/// <remarks>
/// This is what makes an RPM-targeting curve importable at all, and the stalled-row case is the one
/// that matters: a table almost always opens with a duty that produced no rotation.
/// </remarks>
public class CalibrationTableTests
{
    private static readonly EquatableArray<CalibrationPointDefinition> Measured =
    [
        new CalibrationPointDefinition(new Duty(10f), 0),
        new CalibrationPointDefinition(new Duty(20f), 500),
        new CalibrationPointDefinition(new Duty(40f), 1000),
        new CalibrationPointDefinition(new Duty(60f), 1500),
    ];

    [Fact]
    public void A_measured_speed_returns_the_duty_that_produced_it()
    {
        Assert.Equal(40f, CalibrationTable.DutyForRpm(Measured, 1000f)!.Value.Percent, 3);
    }

    [Fact]
    public void A_speed_between_measurements_interpolates()
    {
        // Halfway between 1000 and 1500 RPM, so halfway between 40 and 60 percent.
        Assert.Equal(50f, CalibrationTable.DutyForRpm(Measured, 1250f)!.Value.Percent, 3);
    }

    [Fact]
    public void A_speed_past_either_end_clamps_to_the_nearest_measurement()
    {
        // Extrapolating would invent a duty nobody measured.
        Assert.Equal(20f, CalibrationTable.DutyForRpm(Measured, 100f)!.Value.Percent, 3);
        Assert.Equal(60f, CalibrationTable.DutyForRpm(Measured, 9000f)!.Value.Percent, 3);
    }

    [Fact]
    public void A_row_where_the_fan_was_not_turning_is_not_treated_as_a_way_to_reach_zero()
    {
        // 10 percent produced 0 RPM because the fan was stalled, not because 10 percent is how you
        // ask for 0 RPM. Honouring that row would map every slow target onto a duty that cannot turn
        // the fan at all -- which is precisely the stall the start/stop handling exists to avoid.
        Assert.Equal(20f, CalibrationTable.DutyForRpm(Measured, 0f)!.Value.Percent, 3);
    }

    [Fact]
    public void A_table_with_nothing_measured_answers_nothing()
    {
        Assert.Null(CalibrationTable.DutyForRpm([], 800f));

        EquatableArray<CalibrationPointDefinition> allStalled =
        [
            new CalibrationPointDefinition(new Duty(5f), 0),
            new CalibrationPointDefinition(new Duty(10f), 0),
        ];

        Assert.Null(CalibrationTable.DutyForRpm(allStalled, 800f));
    }
}
