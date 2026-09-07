using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

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
    /// The duty this fan needs to break away from rest. Leave it off to disable start and stop
    /// handling entirely.
    /// </summary>
    /// <remarks>
    /// Higher than the duty needed to keep turning, which is why it is a separate value from
    /// <see cref="MinimumDuty"/>: static friction costs more than the running kind.
    /// </remarks>
    public Duty StartDuty { get; set; } = Duty.Off;

    /// <summary>
    /// The duty below which this fan stalls rather than running slowly. Leave it off to derive one
    /// just under <see cref="StartDuty"/>.
    /// </summary>
    public Duty StopDuty { get; set; } = Duty.Off;

    /// <summary>
    /// The duty this control was pinned at by hand, or <see langword="null"/> when its curve drives
    /// it. Restored as a manual claim when the configuration is applied.
    /// </summary>
    public Duty? ManualDuty { get; set; }

    /// <summary>
    /// The tach sensor for this fan, if one has been paired with it.
    /// </summary>
    /// <remarks>
    /// The only direct evidence available that a start attempt actually worked. Without it the
    /// engine has to trust a timer, which is workable but blind.
    /// </remarks>
    public SensorId PairedFanSensorId { get; set; } = SensorId.None;

    /// <summary>How long to hold <see cref="StartDuty"/> before assuming the fan is turning.</summary>
    public TimeSpan StartKickDuration { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// This fan's measured duty-to-speed points, including any bands marked as ones to avoid.
    /// </summary>
    /// <remarks>
    /// Carried on the live binding rather than only on the stored definition because the engine
    /// reads it every tick: an avoided band is a rule about what may be commanded, not a note about
    /// what was once measured.
    /// </remarks>
    public EquatableArray<CalibrationPointDefinition> Calibration { get; set; } = [];

    /// <summary>
    /// The duty at or below which this control is commanded off instead.
    /// </summary>
    /// <remarks>
    /// Zero — the feature disabled — whenever no <see cref="StartDuty"/> is set. Where a start duty
    /// is set but no distinct stop duty is, the threshold sits one point under the start duty:
    /// somebody who has told us where the fan starts has told us most of what we need, and asking
    /// for a second number to make the first one useful is a poor trade.
    /// </remarks>
    public float StopThreshold
    {
        get
        {
            if (StartDuty.IsOff)
            {
                return 0f;
            }

            var threshold = StopDuty.IsOff || StopDuty == StartDuty
                ? StartDuty.Percent - 1f
                : StopDuty.Percent;

            // A floor outranks the threshold. Calibration measures the lowest duty at which a fan
            // still turns and writes it to MinimumDuty, and the stop duty it measures alongside is
            // frequently the same number - at which point the gate would switch off a fan sitting
            // at the floor it was just told to hold, and never restart it, because the curve can
            // no longer ask for anything lower. A fan with a measured floor runs at that floor
            // instead of stopping; one with no floor keeps the old behaviour exactly.
            return MinimumDuty.IsOff ? threshold : MathF.Min(threshold, MinimumDuty.Percent - 1f);
        }
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
    /// Applies this binding's floor and ceiling, then steps the result out of any avoided band.
    /// </summary>
    /// <param name="duty">What the owner asked for.</param>
    /// <param name="current">
    /// The duty standing now, which is what decides which way out of a band the fan takes.
    /// </param>
    /// <remarks>
    /// <para>
    /// Direction of travel picks the edge, but the limits get the final say: an edge outside the
    /// floor or ceiling is not somewhere this control may go, so the other edge is taken instead.
    /// That only comes up on a band sitting against one of the limits, and it is the difference
    /// between crossing the band and being clamped back into the middle of it.
    /// </para>
    /// <para>
    /// When both edges are out of range there is nowhere legal outside the band, and the limits
    /// win. The choice there is between a fan making a noise the user knows about and a fan running
    /// outside the range the user set, and the second is us ignoring an instruction.
    /// </para>
    /// </remarks>
    public Duty Resolve(Duty duty, Duty current)
    {
        var limited = ApplyLimits(duty);

        if (Calibration.Count == 0
            || !CalibrationTable.TryFindAvoidedBand(Calibration, limited, out var below, out var above))
        {
            return limited;
        }

        var preferred = limited >= current ? above : below;
        var fallback = limited >= current ? below : above;

        if (ApplyLimits(preferred) == preferred)
        {
            return preferred;
        }

        return ApplyLimits(fallback) == fallback ? fallback : limited;
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
