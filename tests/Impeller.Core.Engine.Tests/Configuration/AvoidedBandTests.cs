using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Core.Engine.Tests.Configuration;

/// <summary>
/// Covers stepping a duty past a band the user asked not to sit in.
/// </summary>
/// <remarks>
/// This is the case-resonance feature: somewhere around 40% the fan and the panel find a note, and
/// the answer is to cross that band rather than to settle in it.
/// </remarks>
public class AvoidedBandTests
{
    private static EquatableArray<CalibrationPointDefinition> Table(params float[] avoided)
    {
        var points = new List<CalibrationPointDefinition>();

        for (var duty = 100f; duty >= 0f; duty -= 10f)
        {
            points.Add(new CalibrationPointDefinition(
                new Duty(duty),
                (int)(duty * 18f),
                avoided.Contains(duty)));
        }

        return [.. points];
    }

    [Fact]
    public void A_duty_outside_every_band_is_left_alone()
    {
        var table = Table(40f, 50f);

        Assert.Equal(70f, CalibrationTable.SnapPastAvoided(table, new Duty(70f), new Duty(60f)).Percent);
        Assert.Equal(30f, CalibrationTable.SnapPastAvoided(table, new Duty(30f), new Duty(20f)).Percent);
    }

    [Fact]
    public void A_table_with_nothing_marked_changes_nothing()
    {
        var table = Table();

        Assert.Equal(45f, CalibrationTable.SnapPastAvoided(table, new Duty(45f), new Duty(20f)).Percent);
    }

    [Fact]
    public void A_duty_climbing_into_a_band_is_pushed_out_of_the_top()
    {
        // Snapping to the nearer edge instead would push it back below the band, where the curve
        // climbs into it again next tick and the fan oscillates on the lower edge for as long as the
        // temperature sits there.
        var table = Table(40f, 50f);

        var snapped = CalibrationTable.SnapPastAvoided(table, new Duty(45f), new Duty(38f));

        Assert.Equal(51f, snapped.Percent);
    }

    [Fact]
    public void A_duty_falling_into_a_band_is_pushed_out_of_the_bottom()
    {
        var table = Table(40f, 50f);

        var snapped = CalibrationTable.SnapPastAvoided(table, new Duty(45f), new Duty(60f));

        Assert.Equal(39f, snapped.Percent);
    }

    [Fact]
    public void Two_separate_bands_stay_separate()
    {
        // Adjacent marks form one band; marks a long way apart are two, and the duty between them is
        // a perfectly good place to sit.
        var table = Table(20f, 70f, 80f);

        Assert.Equal(45f, CalibrationTable.SnapPastAvoided(table, new Duty(45f), new Duty(30f)).Percent);
        Assert.Equal(81f, CalibrationTable.SnapPastAvoided(table, new Duty(75f), new Duty(60f)).Percent);
        Assert.Equal(19f, CalibrationTable.SnapPastAvoided(table, new Duty(20f), new Duty(30f)).Percent);
    }

    [Fact]
    public void A_band_against_the_ceiling_can_only_be_left_downward()
    {
        var table = Table(90f, 100f);

        // Rising, but there is nowhere above it to go.
        Assert.Equal(89f, CalibrationTable.SnapPastAvoided(table, new Duty(95f), new Duty(80f)).Percent);
    }

    [Fact]
    public void A_band_against_the_floor_can_only_be_left_upward()
    {
        var table = Table(0f, 10f);

        Assert.Equal(11f, CalibrationTable.SnapPastAvoided(table, new Duty(5f), new Duty(30f)).Percent);
    }

    [Fact]
    public void An_edge_outside_the_bindings_limits_sends_the_duty_the_other_way()
    {
        // Climbing, so the top edge is where it wants to go — but the ceiling is inside the band, so
        // going up means being clamped back into the middle of the thing it was crossing.
        var binding = new ControlBinding(SensorId.New())
        {
            MaximumDuty = new Duty(60f),
            Calibration = Table(50f, 60f, 70f),
        };

        Assert.Equal(49f, binding.Resolve(new Duty(55f), new Duty(40f)).Percent);

        // Capped at 60 first, which lands inside the band, and the same way out applies.
        Assert.Equal(49f, binding.Resolve(new Duty(90f), new Duty(40f)).Percent);
    }

    [Fact]
    public void A_band_with_no_legal_way_out_leaves_the_limits_in_charge()
    {
        // Both edges lie outside the range this control may run in. The choice is then between a fan
        // making a noise the user knows about and a fan running outside the range the user set, and
        // the second is us ignoring an instruction.
        var binding = new ControlBinding(SensorId.New())
        {
            MinimumDuty = new Duty(40f),
            MaximumDuty = new Duty(60f),
            Calibration = Table(30f, 40f, 50f, 60f, 70f),
        };

        Assert.Equal(50f, binding.Resolve(new Duty(50f), new Duty(40f)).Percent);
    }

    [Fact]
    public void A_binding_with_no_calibration_only_applies_its_limits()
    {
        var binding = new ControlBinding(SensorId.New())
        {
            MinimumDuty = new Duty(20f),
            MaximumDuty = new Duty(80f),
        };

        Assert.Equal(45f, binding.Resolve(new Duty(45f), new Duty(30f)).Percent);
        Assert.Equal(20f, binding.Resolve(Duty.Off, new Duty(30f)).Percent);
        Assert.Equal(80f, binding.Resolve(Duty.Full, new Duty(30f)).Percent);
    }
}
