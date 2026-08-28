using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine;

/// <summary>
/// Shared plumbing for curve types: identity, naming, and the default "depends on nothing"
/// answers that most curves override for only one of the two dependency kinds.
/// </summary>
public abstract class FanCurveBase(CurveId id, string name) : IFanCurve
{
    /// <inheritdoc />
    public CurveId Id { get; } = id;

    /// <inheritdoc />
    public string Name { get; set; } = name;

    /// <inheritdoc />
    public virtual IReadOnlyCollection<SensorId> SensorDependencies => [];

    /// <inheritdoc />
    public virtual IReadOnlyCollection<CurveId> CurveDependencies => [];

    /// <inheritdoc />
    public abstract Duty? Evaluate(ICurveEvaluationContext context);

    /// <inheritdoc />
    public virtual void Reset()
    {
    }
}

/// <summary>
/// Shared plumbing for curves driven by exactly one sensor, which is most of them.
/// Handles the dependency list and the hysteresis gate so each curve type only has to
/// express its own input-to-duty mapping.
/// </summary>
public abstract class SingleSensorCurveBase : FanCurveBase
{
    private readonly SensorId[] _dependencies;
    private HysteresisGate _gate;

    /// <param name="id">Curve identity.</param>
    /// <param name="name">User-assigned name.</param>
    /// <param name="source">The sensor this curve reads.</param>
    /// <param name="hysteresis">Change-suppression settings applied to the input.</param>
    protected SingleSensorCurveBase(
        CurveId id,
        string name,
        SensorId source,
        HysteresisSettings hysteresis)
        : base(id, name)
    {
        Source = source;
        _dependencies = source.IsNone ? [] : [source];
        _gate = new HysteresisGate(hysteresis);
        Hysteresis = hysteresis;
    }

    /// <summary>The sensor driving this curve.</summary>
    public SensorId Source { get; }

    /// <summary>The change-suppression settings in force.</summary>
    public HysteresisSettings Hysteresis { get; private set; }

    /// <inheritdoc />
    public override IReadOnlyCollection<SensorId> SensorDependencies => _dependencies;

    /// <summary>
    /// Replaces the hysteresis settings, discarding accumulated state so the new deadband
    /// takes effect from the next reading rather than being compared against history gathered
    /// under the old one.
    /// </summary>
    public void UpdateHysteresis(HysteresisSettings settings)
    {
        Hysteresis = settings;
        _gate = new HysteresisGate(settings);
    }

    /// <inheritdoc />
    public sealed override Duty? Evaluate(ICurveEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetSensorValue(Source) is not { } raw)
        {
            // No reading: hold whatever the control is already doing rather than guessing.
            return null;
        }

        var gated = _gate.Offer(raw, context.Elapsed, ShouldBypassSuppression(raw));
        return gated is { } value ? MapToDuty(value) : null;
    }

    /// <summary>
    /// Maps a gated sensor reading to an output duty. This is the curve type's actual behaviour.
    /// </summary>
    protected abstract Duty MapToDuty(float value);

    /// <summary>
    /// Whether suppression should be skipped for this reading. The default skips it once the
    /// input has left the curve's configured range, where the answer is already saturated and
    /// waiting only delays a fan that should already have responded.
    /// </summary>
    protected virtual bool ShouldBypassSuppression(float rawValue) => false;

    /// <inheritdoc />
    public override void Reset() => _gate.Reset();
}
