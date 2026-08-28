using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Tests;

public class DutyTests
{
    [Theory]
    [InlineData(-40f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(42.5f, 42.5f)]
    [InlineData(100f, 100f)]
    [InlineData(180f, 100f)]
    public void Constructor_clamps_into_range(float input, float expected)
    {
        Assert.Equal(expected, new Duty(input).Percent);
    }

    [Fact]
    public void NaN_becomes_off_rather_than_propagating()
    {
        // A NaN reaching hardware would be meaningless; saturating to off is the safe reading.
        Assert.Equal(0f, new Duty(float.NaN).Percent);
    }

    [Fact]
    public void StepToward_moves_at_most_the_step_size()
    {
        var result = Duty.StepToward(new Duty(20f), new Duty(80f), maxStep: 5f);
        Assert.Equal(25f, result.Percent);
    }

    [Fact]
    public void StepToward_moves_downward_by_the_step_size()
    {
        var result = Duty.StepToward(new Duty(80f), new Duty(20f), maxStep: 5f);
        Assert.Equal(75f, result.Percent);
    }

    [Fact]
    public void StepToward_arrives_exactly_when_within_one_step()
    {
        var result = Duty.StepToward(new Duty(48f), new Duty(50f), maxStep: 5f);
        Assert.Equal(50f, result.Percent);
    }

    [Fact]
    public void StepToward_with_no_limit_jumps_straight_to_target()
    {
        var result = Duty.StepToward(new Duty(0f), new Duty(100f), maxStep: 0f);
        Assert.Equal(100f, result.Percent);
    }

    [Fact]
    public void Full_is_the_failsafe_value()
    {
        Assert.Equal(100f, Duty.Full.Percent);
        Assert.True(Duty.Off.IsOff);
    }

    [Fact]
    public void Fraction_round_trips()
    {
        Assert.Equal(0.5f, new Duty(50f).Fraction, precision: 5);
        Assert.Equal(50f, Duty.FromFraction(0.5f).Percent, precision: 5);
    }
}
