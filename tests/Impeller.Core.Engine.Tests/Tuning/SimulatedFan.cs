using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Tests.Tuning;

/// <summary>
/// A fan that behaves the way a real one does, for driving a tuning procedure without hardware.
/// </summary>
/// <remarks>
/// Three properties are what make the procedures worth testing against it: speed lags a change in
/// duty rather than following it instantly, the fan stalls below one duty and needs a higher one to
/// break away again, and a stalled fan reads zero rather than nothing. A model without the lag would
/// let a procedure pass that never waits for anything to settle.
/// </remarks>
internal sealed class SimulatedFan(
    int maximumRpm = 1800,
    float stallsBelow = 18f,
    float startsAbove = 24f)
{
    /// <summary>Speed at the lowest duty that still turns it. A fan does not ramp from zero.</summary>
    private const float FloorRpm = 200f;

    /// <summary>How much of the remaining gap to the target speed is closed each sample.</summary>
    private const float Response = 0.5f;

    private bool _turning = true;

    /// <summary>What the tacho currently reads.</summary>
    public float Rpm { get; private set; } = maximumRpm;

    /// <summary>Holds the fan at a duty for one sample interval.</summary>
    public void Apply(Duty duty)
    {
        var percent = duty.Percent;

        if (_turning && percent < stallsBelow)
        {
            _turning = false;
        }
        else if (!_turning && percent >= startsAbove)
        {
            _turning = true;
        }

        if (!_turning)
        {
            Rpm = 0f;
            return;
        }

        var target = FloorRpm + ((maximumRpm - FloorRpm) * (percent / 100f));
        Rpm += (target - Rpm) * Response;
    }
}
