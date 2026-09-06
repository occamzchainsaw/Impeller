namespace Impeller.Hardware.Lhm.Tests;

/// <summary>
/// Restating an unchanged duty has to reach the hardware.
/// </summary>
/// <remarks>
/// <para>
/// LibreHardwareMonitor drops a control write whose value equals the one it is already holding —
/// <c>Control.SoftwareValue</c> guards its setter on <c>_softwareValue != value</c>, so the event
/// that would carry the write down to the chip never fires. That makes the one write this engine
/// most depends on the one write that silently does nothing: the periodic restatement that keeps a
/// header claimed and re-asserts the chip's manual-mode bit.
/// </para>
/// <para>
/// The way past it is to ask for a percentage that differs from the one being held but converts to
/// the same PWM register value, so the fan sees no change and the guard still lets the write
/// through. That trick is only safe if the conversion really does land on the same byte, which is
/// float arithmetic across a truncating cast — hence pinning it rather than trusting it.
/// </para>
/// </remarks>
public sealed class RestatedWriteTests
{
    /// <summary>LibreHardwareMonitor's own conversion, in <c>SuperIOHardware</c>.</summary>
    private static byte ToRegister(float percent) => (byte)(percent * 2.55f);

    [Fact]
    public void A_restated_duty_asks_for_a_different_number_than_the_one_being_held()
    {
        for (var percent = 0f; percent <= 100f; percent += 0.05f)
        {
            Assert.NotEqual(percent, LhmControl.SameRegisterValueAs(percent));
        }
    }

    [Fact]
    public void A_restated_duty_still_lands_on_the_same_register_value()
    {
        for (var percent = 0f; percent <= 100f; percent += 0.05f)
        {
            Assert.Equal(ToRegister(percent), ToRegister(LhmControl.SameRegisterValueAs(percent)));
        }
    }

    /// <summary>
    /// The ends of the range are where a nudge would run out of room, so they are checked exactly.
    /// </summary>
    /// <remarks>
    /// A fan at a standstill and a fan at full are the two duties a user is most likely to have set
    /// deliberately, and the two a rounding error would be most obvious on.
    /// </remarks>
    [Theory]
    [InlineData(0f)]
    [InlineData(100f)]
    public void The_ends_of_the_range_restate_without_moving_the_fan(float percent)
    {
        var restated = LhmControl.SameRegisterValueAs(percent);

        Assert.NotEqual(percent, restated);
        Assert.Equal(ToRegister(percent), ToRegister(restated));
    }
}
