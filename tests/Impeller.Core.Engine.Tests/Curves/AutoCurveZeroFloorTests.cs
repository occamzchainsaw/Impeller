using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests.Curves;

/// <summary>
/// A floor of zero is a choice, not an omission.
/// </summary>
/// <remarks>
/// From the field: a GPU curve set to idle its fan off below 35 °C held the fan at 50% at 27 °C,
/// while the configuration, the engine's readback and the UI all said the floor was zero. The
/// curve had substituted its default because <c>default(Duty)</c> and an explicit 0% are the same
/// value.
/// </remarks>
public class AutoCurveZeroFloorTests
{
    private static readonly SensorId Temperature = SensorId.New();

    private static AutoCurve Build(Duty? minimum) =>
        new(
            CurveId.New(),
            "auto",
            Temperature,
            idleTemperature: 35f,
            loadTemperature: 80f,
            minimumDuty: minimum,
            maximumDuty: Duty.Full,
            responseTime: TimeSpan.FromSeconds(1));

    [Fact]
    public void A_floor_of_zero_is_kept_rather_than_replaced_with_the_default()
    {
        var curve = Build(new Duty(0f));

        Assert.Equal(0f, curve.MinimumDuty.Percent, precision: 3);
    }

    [Fact]
    public void A_curve_below_its_idle_temperature_commands_the_floor_it_was_given()
    {
        var curve = Build(new Duty(0f));
        var context = new TestCurveContext().Advancing(TimeSpan.FromSeconds(1));

        // Twice: the first evaluation seeds the controller, the second is the steady answer.
        curve.Evaluate(context.WithSensor(Temperature, 27f));
        var duty = curve.Evaluate(context.WithSensor(Temperature, 27f));

        Assert.Equal(0f, duty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Omitting_the_floor_still_gives_the_default()
    {
        Assert.Equal(50f, Build(null).MinimumDuty.Percent, precision: 3);
    }
}
