using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine;

/// <summary>What one tick did, for diagnostics and for the UI's live view.</summary>
/// <param name="CurveOutputs">Each curve's output this tick. A null value means the curve could not produce one.</param>
/// <param name="CommandedDuties">The duty now standing at each control the engine drives.</param>
/// <param name="ControlsWritten">How many controls actually received a hardware write.</param>
/// <param name="Faults">Controls whose write threw, paired with the exception.</param>
public readonly record struct TickResult(
    IReadOnlyDictionary<CurveId, Duty?> CurveOutputs,
    IReadOnlyDictionary<SensorId, Duty> CommandedDuties,
    int ControlsWritten,
    IReadOnlyDictionary<SensorId, Exception> Faults);

/// <summary>
/// Turns sensor readings into hardware writes, once per tick.
/// </summary>
/// <remarks>
/// <para>
/// The order within a tick is fixed: evaluate every curve in dependency order, then resolve each
/// control against whoever currently owns it, then apply that binding's limits and ramp rate,
/// then write. Curves never touch hardware and never learn who owns what, which is what keeps
/// them pure functions of their inputs.
/// </para>
/// <para>
/// A control whose owner produces no value holds its previous duty rather than falling back to
/// a default. Guessing at a fan speed is worse than briefly not changing one.
/// </para>
/// <para>
/// Not thread-safe by itself; <see cref="Tick"/> is expected to run on a single scheduler thread.
/// Ownership changes and duty requests from plugins arrive on other threads and are handled by
/// the thread-safe <see cref="ControlOwnershipRegistry"/> and an internal lock on requested duties.
/// </para>
/// </remarks>
public sealed class ControlLoop(
    ISensorRegistry registry,
    ControlOwnershipRegistry ownership,
    TimeProvider timeProvider)
{
    private readonly ISensorRegistry _registry = registry;
    private readonly ControlOwnershipRegistry _ownership = ownership;
    private readonly TimeProvider _time = timeProvider;

    private readonly Lock _requestGate = new();
    private readonly Dictionary<SensorId, Duty> _requestedDuties = [];
    private readonly Dictionary<SensorId, Duty> _commandedDuties = [];

    private IReadOnlyList<IFanCurve> _orderedCurves = [];
    private IReadOnlyDictionary<SensorId, ControlBinding> _bindings =
        new Dictionary<SensorId, ControlBinding>();

    // One per bound control, rebuilt with the bindings so a reconfigured control never inherits a
    // half-finished start attempt from the configuration it replaced.
    private Dictionary<SensorId, StartStopGate> _startStop = [];

    private bool _failsafeEngaged;

    /// <summary>Whether the engine is currently holding every control at its failsafe duty.</summary>
    public bool IsFailsafeEngaged => _failsafeEngaged;

    /// <summary>The curves being evaluated, in dependency order.</summary>
    public IReadOnlyList<IFanCurve> Curves => _orderedCurves;

    /// <summary>The control bindings in force, keyed by control.</summary>
    public IReadOnlyDictionary<SensorId, ControlBinding> Bindings => _bindings;

    /// <summary>
    /// Replaces the configuration.
    /// </summary>
    /// <param name="curves">The curves to evaluate. Sorted into dependency order here.</param>
    /// <param name="bindings">Which curve drives which control, and the limits applied on top.</param>
    /// <exception cref="ArgumentException">
    /// The curves contain a dependency cycle. Callers should validate with
    /// <see cref="CurveGraph.Sort"/> and surface the cycle to the user rather than relying on this.
    /// </exception>
    public void Configure(IEnumerable<IFanCurve> curves, IEnumerable<ControlBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(curves);
        ArgumentNullException.ThrowIfNull(bindings);

        // Bindings first: the sort needs to know which curve drives which control before it can
        // follow a sync curve's control edge back to the curve behind it.
        var bound = bindings.ToDictionary(binding => binding.ControlId);
        var controlCurves = bound.ToDictionary(entry => entry.Key, entry => entry.Value.CurveId);

        var order = CurveGraph.Sort(curves, controlCurves);
        if (!order.IsValid)
        {
            var cycle = string.Join(" -> ", order.Cycle);
            throw new ArgumentException(
                $"Curves contain a dependency cycle: {cycle}", nameof(curves));
        }

        _orderedCurves = order.Ordered;
        _bindings = bound;
        _startStop = bound.ToDictionary(entry => entry.Key, entry => new StartStopGate(entry.Value));

        RestoreManualPins(bound.Values);
    }

    /// <summary>
    /// Takes back the manual claims a saved configuration describes.
    /// </summary>
    /// <remarks>
    /// A pin the user set is an instruction, not a session detail, so it survives a restart. This
    /// runs on every configure rather than only at startup, which also means a pin removed from an
    /// edited configuration is released rather than left holding the fan.
    /// </remarks>
    private void RestoreManualPins(IEnumerable<ControlBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            var owner = _ownership.GetOwner(binding.ControlId);

            if (binding.ManualDuty is { } pinned)
            {
                // Anything already holding it stays: a plugin mid-claim, or a failsafe that has not
                // cleared, both outrank a stored value.
                if (owner.IsCurve)
                {
                    _ownership.TryAcquire(
                        binding.ControlId,
                        ControlOwnerKind.ManualOverride,
                        ControlOwnershipRegistry.ManualClaimant);
                }

                TrySetRequestedDuty(binding.ControlId, pinned, ControlOwnershipRegistry.ManualClaimant);
            }
            else if (owner.Kind == ControlOwnerKind.ManualOverride
                && string.Equals(owner.ClaimantId, ControlOwnershipRegistry.ManualClaimant, StringComparison.Ordinal))
            {
                _ownership.Release(binding.ControlId, ControlOwnershipRegistry.ManualClaimant);
            }
        }
    }

    /// <summary>
    /// Records the duty an owning plugin or manual override wants a control held at.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the caller does not own the control, in which case the request
    /// is discarded. Ownership is checked here so a stale plugin cannot keep writing after it was
    /// force-released.
    /// </returns>
    public bool TrySetRequestedDuty(SensorId controlId, Duty duty, string? claimantId = null)
    {
        var owner = _ownership.GetOwner(controlId);

        if (owner.IsCurve || owner.Kind == ControlOwnerKind.Failsafe)
        {
            return false;
        }

        if (owner.Kind == ControlOwnerKind.Plugin
            && !string.Equals(owner.ClaimantId, claimantId, StringComparison.Ordinal))
        {
            return false;
        }

        lock (_requestGate)
        {
            _requestedDuties[controlId] = duty;
        }

        return true;
    }

    /// <summary>The duty most recently written to a control, or null if it has not been written.</summary>
    public Duty? GetCommandedDuty(SensorId controlId) =>
        _commandedDuties.TryGetValue(controlId, out var duty) ? duty : null;

    /// <summary>
    /// Drives every control to its failsafe duty and holds it there.
    /// </summary>
    /// <remarks>
    /// Bypasses limits and ramp rates deliberately: this runs when something has gone wrong, and
    /// easing a fan up to a safe speed defeats the point of having one. Grants are suspended so a
    /// plugin cannot take a control back mid-failsafe.
    /// </remarks>
    public TickResult EngageFailsafe()
    {
        _failsafeEngaged = true;
        _ownership.SuspendGrants();

        var written = 0;
        var faults = new Dictionary<SensorId, Exception>();
        var commanded = new Dictionary<SensorId, Duty>();

        foreach (var binding in _bindings.Values)
        {
            if (_registry.GetControl(binding.ControlId) is not { } control)
            {
                continue;
            }

            _ownership.TryAcquire(binding.ControlId, ControlOwnerKind.Failsafe);

            // Try handing the device back to its own firmware first. Most hardware cannot do
            // this, so a false result is expected rather than exceptional.
            if (control.SupportsAutomaticMode && control.TryRestoreAutomaticMode())
            {
                continue;
            }

            try
            {
                control.Write(binding.FailsafeDuty);
                _commandedDuties[binding.ControlId] = binding.FailsafeDuty;
                commanded[binding.ControlId] = binding.FailsafeDuty;
                written++;
            }
            catch (Exception ex)
            {
                faults[binding.ControlId] = ex;
            }
        }

        return new TickResult(
            new Dictionary<CurveId, Duty?>(),
            commanded,
            written,
            faults);
    }

    /// <summary>
    /// Leaves the failsafe state, returning every control to its curve and allowing claims again.
    /// Called once the engine has confirmed it is healthy.
    /// </summary>
    public void ClearFailsafe()
    {
        if (!_failsafeEngaged)
        {
            return;
        }

        foreach (var controlId in _bindings.Keys)
        {
            if (_ownership.GetOwner(controlId).Kind == ControlOwnerKind.Failsafe)
            {
                _ownership.ForceRelease(controlId);
            }
        }

        _failsafeEngaged = false;
        _ownership.ResumeGrants();
    }

    /// <summary>
    /// Runs one tick.
    /// </summary>
    /// <param name="elapsed">
    /// Time since the previous tick. Drives ramp limiting and every curve's response timing, so
    /// a late tick behaves correctly rather than assuming a fixed interval.
    /// </param>
    public TickResult Tick(TimeSpan elapsed)
    {
        if (_failsafeEngaged)
        {
            // Hold. Recovery is an explicit decision by the host, not something a tick decides.
            return new TickResult(
                new Dictionary<CurveId, Duty?>(),
                new Dictionary<SensorId, Duty>(_commandedDuties),
                0,
                new Dictionary<SensorId, Exception>());
        }

        var curveOutputs = EvaluateCurves(elapsed);
        var written = 0;
        var faults = new Dictionary<SensorId, Exception>();

        foreach (var binding in _bindings.Values)
        {
            if (!binding.Enabled || _registry.GetControl(binding.ControlId) is not { } control)
            {
                continue;
            }

            if (ResolveTarget(binding, curveOutputs) is not { } target)
            {
                continue;
            }

            var current = _commandedDuties.TryGetValue(binding.ControlId, out var last)
                ? last
                : control.CommandedDuty ?? target;

            var limited = binding.ApplyLimits(target);

            // Start and stop handling wraps the ramp limiter rather than following it: a fan being
            // kicked into motion needs its start duty immediately, and easing up to it is exactly
            // what fails to start it.
            var pairedRpm = binding.PairedFanSensorId.IsNone
                ? null
                : _registry.GetValue(binding.PairedFanSensorId);

            var next = _startStop.TryGetValue(binding.ControlId, out var gate)
                ? gate.Resolve(limited, current, pairedRpm, elapsed)
                : binding.ApplySlewLimit(current, limited, elapsed);

            // Skip writes that would change nothing. Some Super I/O chips are slow to write,
            // and at one tick a second the redundant traffic adds up.
            if (_commandedDuties.ContainsKey(binding.ControlId)
                && Duty.Distance(current, next) < 0.01f)
            {
                continue;
            }

            try
            {
                control.Write(next);
                _commandedDuties[binding.ControlId] = next;
                written++;
            }
            catch (Exception ex)
            {
                faults[binding.ControlId] = ex;
            }
        }

        return new TickResult(
            curveOutputs,
            new Dictionary<SensorId, Duty>(_commandedDuties),
            written,
            faults);
    }

    /// <summary>
    /// Evaluates every curve in dependency order, so a composite sees this tick's inputs.
    /// A curve that throws yields no value rather than taking the tick down with it.
    /// </summary>
    private Dictionary<CurveId, Duty?> EvaluateCurves(TimeSpan elapsed)
    {
        var outputs = new Dictionary<CurveId, Duty?>(_orderedCurves.Count);
        var context = new EvaluationContext(
            _registry,
            outputs,
            _commandedDuties,
            elapsed,
            _time.GetUtcNow());

        foreach (var curve in _orderedCurves)
        {
            try
            {
                outputs[curve.Id] = curve.Evaluate(context);
            }
            catch (Exception)
            {
                outputs[curve.Id] = null;
            }
        }

        return outputs;
    }

    /// <summary>
    /// Works out what duty a control should be heading toward, based on who owns it.
    /// Returns null when the owner has nothing to say, in which case the control holds.
    /// </summary>
    private Duty? ResolveTarget(ControlBinding binding, Dictionary<CurveId, Duty?> curveOutputs)
    {
        var owner = _ownership.GetOwner(binding.ControlId);

        switch (owner.Kind)
        {
            case ControlOwnerKind.Failsafe:
                return binding.FailsafeDuty;

            case ControlOwnerKind.Plugin:
            case ControlOwnerKind.ManualOverride:
                lock (_requestGate)
                {
                    return _requestedDuties.TryGetValue(binding.ControlId, out var requested)
                        ? requested
                        : null;
                }

            default:
                return curveOutputs.TryGetValue(binding.CurveId, out var output) ? output : null;
        }
    }

    /// <summary>
    /// The per-tick view handed to curves. Reads sensors live and curve outputs from the
    /// dictionary being built, which is safe because evaluation runs in dependency order.
    /// </summary>
    private sealed class EvaluationContext(
        ISensorRegistry registry,
        Dictionary<CurveId, Duty?> outputs,
        Dictionary<SensorId, Duty> commandedDuties,
        TimeSpan elapsed,
        DateTimeOffset timestamp) : ICurveEvaluationContext
    {
        public TimeSpan Elapsed { get; } = elapsed;

        public DateTimeOffset Timestamp { get; } = timestamp;

        public float? GetSensorValue(SensorId id) => registry.GetValue(id);

        public Duty? GetCurveOutput(CurveId id) => outputs.GetValueOrDefault(id);

        public Duty? GetControlDuty(SensorId controlId)
        {
            if (commandedDuties.TryGetValue(controlId, out var commanded))
            {
                return commanded;
            }

            // Nothing written yet this run. Fall back to whatever the hardware says it is already
            // doing, so a sync curve has something to mirror on the very first tick instead of
            // leaving its fan parked until the control it follows happens to move.
            return registry.GetControl(controlId)?.CommandedDuty;
        }
    }
}
