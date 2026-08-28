using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Curves;

/// <summary>
/// Switches between an idle duty and a load duty at two separate thresholds.
/// </summary>
/// <remarks>
/// <para>
/// The gap between <see cref="IdleInput"/> and <see cref="LoadInput"/> is itself the hysteresis
/// band: once the curve has switched to its load state it stays there until the input falls all
/// the way back to the idle threshold, and vice versa. Inside the band the previous state is
/// held, which is what stops a temperature hovering near one threshold from toggling the fan.
/// </para>
/// <para>
/// Because the latch is the whole point of this curve type, it manages its own state rather than
/// borrowing the shared <see cref="HysteresisGate"/>. Response times still apply, so a brief
/// excursion past a threshold does not flip it.
/// </para>
/// </remarks>
public sealed class TriggerCurve : FanCurveBase
{
    private readonly SensorId[] _dependencies;

    private bool _underLoad;
    private bool _hasState;
    private TimeSpan _pendingFor;
    private bool _pendingTarget;

    /// <param name="id">Curve identity.</param>
    /// <param name="name">User-assigned name.</param>
    /// <param name="source">The sensor driving the trigger.</param>
    /// <param name="idleInput">At or below this input the curve returns to idle.</param>
    /// <param name="loadInput">At or above this input the curve switches to load.</param>
    /// <param name="idleDuty">Output while idle.</param>
    /// <param name="loadDuty">Output while under load.</param>
    /// <param name="responseUp">How long the load threshold must be exceeded before switching.</param>
    /// <param name="responseDown">How long the idle threshold must be met before switching back.</param>
    public TriggerCurve(
        CurveId id,
        string name,
        SensorId source,
        float idleInput,
        float loadInput,
        Duty idleDuty,
        Duty loadDuty,
        TimeSpan responseUp = default,
        TimeSpan responseDown = default)
        : base(id, name)
    {
        Source = source;
        _dependencies = source.IsNone ? [] : [source];
        IdleInput = idleInput;
        LoadInput = loadInput;
        IdleDuty = idleDuty;
        LoadDuty = loadDuty;
        ResponseUp = responseUp;
        ResponseDown = responseDown;
    }

    /// <summary>The sensor driving the trigger.</summary>
    public SensorId Source { get; }

    /// <summary>At or below this input the curve returns to idle.</summary>
    public float IdleInput { get; set; }

    /// <summary>At or above this input the curve switches to load.</summary>
    public float LoadInput { get; set; }

    /// <summary>Output while idle.</summary>
    public Duty IdleDuty { get; set; }

    /// <summary>Output while under load.</summary>
    public Duty LoadDuty { get; set; }

    /// <summary>How long the load threshold must be exceeded before switching to load.</summary>
    public TimeSpan ResponseUp { get; set; }

    /// <summary>How long the idle threshold must be met before switching back to idle.</summary>
    public TimeSpan ResponseDown { get; set; }

    /// <summary>Whether the curve is currently latched into its load state.</summary>
    public bool IsUnderLoad => _underLoad;

    /// <inheritdoc />
    public override IReadOnlyCollection<SensorId> SensorDependencies => _dependencies;

    /// <inheritdoc />
    public override Duty? Evaluate(ICurveEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetSensorValue(Source) is not { } value)
        {
            return _hasState ? Current() : null;
        }

        // Which state the reading argues for. Inside the band it argues for neither, so the
        // latch holds and any part-accumulated switch is abandoned.
        bool? target = value >= LoadInput ? true
            : value <= IdleInput ? false
            : null;

        if (!_hasState)
        {
            // First reading with no latch yet: adopt immediately rather than waiting out a
            // response time against a state we never held. Inside the band, assume idle.
            _underLoad = target ?? false;
            _hasState = true;
            ResetPending();
            return Current();
        }

        if (target is not { } wanted || wanted == _underLoad)
        {
            ResetPending();
            return Current();
        }

        var required = wanted ? ResponseUp : ResponseDown;

        if (_pendingFor > TimeSpan.Zero && _pendingTarget != wanted)
        {
            _pendingFor = TimeSpan.Zero;
        }

        _pendingTarget = wanted;
        _pendingFor += context.Elapsed;

        if (_pendingFor >= required)
        {
            _underLoad = wanted;
            ResetPending();
        }

        return Current();
    }

    /// <inheritdoc />
    public override void Reset()
    {
        _hasState = false;
        _underLoad = false;
        ResetPending();
    }

    private Duty Current() => _underLoad ? LoadDuty : IdleDuty;

    private void ResetPending()
    {
        _pendingFor = TimeSpan.Zero;
        _pendingTarget = false;
    }
}
