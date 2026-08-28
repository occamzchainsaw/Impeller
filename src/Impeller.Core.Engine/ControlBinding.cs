using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine;

/// <summary>
/// Ties one control to the curve that drives it, plus the limits the engine applies on top.
/// </summary>
/// <remarks>
/// Limits are applied by the engine rather than baked into curves, so the same curve can drive
/// several fans that each have their own floor, ceiling, and ramp rate.
/// </remarks>
public sealed class ControlBinding(SensorId controlId)
{
    private float _maximumStepUpPerSecond = 100f;
    private float _maximumStepDownPerSecond = 100f;

    /// <summary>The control this binding drives.</summary>
    public SensorId ControlId { get; } = controlId;

    /// <summary>The curve that drives it while no one has taken ownership.</summary>
    public CurveId CurveId { get; set; } = CurveId.None;

    /// <summary>Whether the engine drives this control at all. A disabled control is left alone entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Lowest duty the engine will command. Raise this above zero for a fan that stalls or
    /// whines at low speed, or that should never fully stop.
    /// </summary>
    public Duty MinimumDuty { get; set; } = Duty.Off;

    /// <summary>Highest duty the engine will command, for capping a fan that is louder than it is useful.</summary>
    public Duty MaximumDuty { get; set; } = Duty.Full;

    /// <summary>
    /// What to command when the engine loses control — a watchdog trip, or a deliberate shutdown.
    /// </summary>
    /// <remarks>
    /// Full speed by default: loud, but never a thermal risk. Worth lowering only for a control
    /// where running flat out is itself undesirable, such as a pump or a case light.
    /// </remarks>
    public Duty FailsafeDuty { get; set; } = Duty.Full;

    /// <summary>
    /// Fastest the duty may rise, in percentage points per second. Limits how abruptly a fan can
    /// spin up, which is mostly an audibility concern.
    /// </summary>
    public float MaximumStepUpPerSecond
    {
        get => _maximumStepUpPerSecond;
        set => _maximumStepUpPerSecond = MathF.Max(0f, value);
    }

    /// <summary>Fastest the duty may fall, in percentage points per second.</summary>
    public float MaximumStepDownPerSecond
    {
        get => _maximumStepDownPerSecond;
        set => _maximumStepDownPerSecond = MathF.Max(0f, value);
    }

    /// <summary>
    /// Applies this binding's floor and ceiling to a duty.
    /// </summary>
    public Duty ApplyLimits(Duty duty)
    {
        if (duty < MinimumDuty)
        {
            return MinimumDuty;
        }

        return duty > MaximumDuty ? MaximumDuty : duty;
    }

    /// <summary>
    /// Moves <paramref name="current"/> toward <paramref name="target"/> no faster than this
    /// binding's ramp limits allow over <paramref name="elapsed"/>.
    /// </summary>
    public Duty ApplySlewLimit(Duty current, Duty target, TimeSpan elapsed)
    {
        var seconds = (float)elapsed.TotalSeconds;
        if (seconds <= 0f)
        {
            return current;
        }

        var rising = target > current;
        var rate = rising ? _maximumStepUpPerSecond : _maximumStepDownPerSecond;

        // A rate of zero means "no limit"; a fan that must never ramp is expressed by pinning
        // the minimum and maximum together instead.
        if (rate <= 0f)
        {
            return target;
        }

        return Duty.StepToward(current, target, rate * seconds);
    }
}
