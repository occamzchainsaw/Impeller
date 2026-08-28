using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Curves;

/// <summary>
/// Ramps linearly between two points, holding flat outside them.
/// </summary>
/// <remarks>
/// Below <see cref="MinimumInput"/> the output sits at <see cref="MinimumDuty"/>; above
/// <see cref="MaximumInput"/> it sits at <see cref="MaximumDuty"/>; between them it interpolates.
/// Suppression is bypassed outside that range, since the output is already saturated there and
/// debouncing would only delay a fan that should have responded.
/// </remarks>
public sealed class LinearCurve : SingleSensorCurveBase
{
    private float _minimumInput;
    private float _maximumInput;

    /// <param name="id">Curve identity.</param>
    /// <param name="name">User-assigned name.</param>
    /// <param name="source">The sensor driving the ramp.</param>
    /// <param name="minimumInput">Input value at which the ramp starts.</param>
    /// <param name="maximumInput">Input value at which the ramp reaches full.</param>
    /// <param name="minimumDuty">Output at or below <paramref name="minimumInput"/>.</param>
    /// <param name="maximumDuty">Output at or above <paramref name="maximumInput"/>.</param>
    /// <param name="hysteresis">Change-suppression settings for the input.</param>
    public LinearCurve(
        CurveId id,
        string name,
        SensorId source,
        float minimumInput,
        float maximumInput,
        Duty minimumDuty,
        Duty maximumDuty,
        HysteresisSettings hysteresis = default)
        : base(id, name, source, hysteresis)
    {
        _minimumInput = minimumInput;
        _maximumInput = maximumInput;
        MinimumDuty = minimumDuty;
        MaximumDuty = maximumDuty;
    }

    /// <summary>Input value at which the ramp starts.</summary>
    public float MinimumInput
    {
        get => _minimumInput;
        set => _minimumInput = value;
    }

    /// <summary>Input value at which the ramp reaches <see cref="MaximumDuty"/>.</summary>
    public float MaximumInput
    {
        get => _maximumInput;
        set => _maximumInput = value;
    }

    /// <summary>Output at or below <see cref="MinimumInput"/>.</summary>
    public Duty MinimumDuty { get; set; }

    /// <summary>Output at or above <see cref="MaximumInput"/>.</summary>
    public Duty MaximumDuty { get; set; }

    /// <inheritdoc />
    protected override Duty MapToDuty(float value)
    {
        var span = _maximumInput - _minimumInput;

        // A degenerate or inverted range has no meaningful slope. Treat the threshold as a
        // step rather than dividing by zero.
        if (span <= 0f)
        {
            return value >= _maximumInput ? MaximumDuty : MinimumDuty;
        }

        if (value <= _minimumInput)
        {
            return MinimumDuty;
        }

        if (value >= _maximumInput)
        {
            return MaximumDuty;
        }

        var t = (value - _minimumInput) / span;
        return new Duty(MinimumDuty.Percent + (t * (MaximumDuty.Percent - MinimumDuty.Percent)));
    }

    /// <inheritdoc />
    protected override bool ShouldBypassSuppression(float rawValue) =>
        rawValue <= _minimumInput || rawValue >= _maximumInput;
}
