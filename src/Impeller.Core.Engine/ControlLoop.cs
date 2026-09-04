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
/// A control whose owner has nothing to say falls back to its curve, which is the same promise
/// made to a fan whose plugin dies. A control whose curve produces no value holds its previous
/// duty instead: guessing at a fan speed is worse than briefly not changing one, and unlike a
/// silent owner a failed curve has no better answer sitting behind it.
/// </para>
/// <para>
/// Not thread-safe by itself; <see cref="Tick"/> is expected to run on a single scheduler thread.
/// Ownership changes and duty requests from plugins arrive on other threads and are handled by
/// the thread-safe <see cref="ControlOwnershipRegistry"/> and an internal lock on requested duties.
/// </para>
/// </remarks>
public sealed class ControlLoop
{
    private readonly ISensorRegistry _registry;
    private readonly ControlOwnershipRegistry _ownership;
    private readonly TimeProvider _time;

    /// <summary>Builds a loop over a set of hardware and the registry arbitrating it.</summary>
    public ControlLoop(
        ISensorRegistry registry,
        ControlOwnershipRegistry ownership,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(ownership);

        _registry = registry;
        _ownership = ownership;
        _time = timeProvider;

        // Nothing consumed this event before. See ForgetRequestedDuty for what it is for and why
        // the stamp on a requested duty is not enough on its own.
        _ownership.OwnershipChanged += (_, change) => ForgetRequestedDuty(change.ControlId);
    }

    private readonly Lock _requestGate = new();
    private readonly Dictionary<SensorId, RequestedDuty> _requestedDuties = [];
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

        DropClaimsTheConfigurationNoLongerSupports();
        RestoreManualPins(bound.Values);
    }

    /// <summary>
    /// Frees any control that is still held but is no longer one this configuration lets a
    /// claimant hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Claimability is checked when a claim is taken, but the configuration can change under a
    /// standing claim at any time afterwards - someone disables the fan, or unassigns its curve.
    /// Without this the claim survives into a configuration that cannot honour it: the tick loop
    /// skips a disabled binding, so the holder goes on setting duties that are accepted and then
    /// silently discarded, while its own display shows a fan speed nobody is commanding.
    /// </para>
    /// <para>
    /// The claimant is told, through the ownership event, with
    /// <see cref="OwnershipChangeReason.ConfigurationChanged"/>. Being taken off a fan is
    /// recoverable; not being told is not.
    /// </para>
    /// </remarks>
    private void DropClaimsTheConfigurationNoLongerSupports()
    {
        foreach (var (controlId, owner) in _ownership.ActiveClaims)
        {
            var stillAllowed = owner.Kind switch
            {
                ControlOwnerKind.Plugin => CanBeHeldByPlugin(controlId),
                ControlOwnerKind.ManualOverride => IsDriven(controlId),

                // A failsafe claim outranks the configuration entirely. Freeing a fan the engine is
                // holding because it lost its grip would be the one change that makes things worse.
                _ => true,
            };

            if (!stillAllowed)
            {
                _ownership.ForceRelease(controlId, OwnershipChangeReason.ConfigurationChanged);
            }
        }
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

            // A stored pin on a disabled control is kept in the configuration but not taken: the
            // tick loop would not write it, and a claim nothing honours is worse than no claim.
            // Re-enabling the control restores the pin on the next apply.
            if (binding.ManualDuty is { } pinned && binding.Enabled)
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

        // Checked for every kind of owner, not just a plugin one. It used to apply only to
        // plugin-held controls, which left a hole in the other direction: a plugin could set the
        // duty on a fan the user had pinned by hand, and the request would be accepted and driven
        // because it was stored against whoever legitimately owned the control. Nothing reached it
        // before the plugin channel existed, which is why it survived this long.
        if (!string.Equals(owner.ClaimantId, claimantId, StringComparison.Ordinal))
        {
            return false;
        }

        lock (_requestGate)
        {
            // Stamped with the owner it was validated against, not just stored. See RequestedDuty.
            _requestedDuties[controlId] = new RequestedDuty(duty, owner.Kind, owner.ClaimantId);
        }

        return true;
    }

    /// <summary>
    /// Whether the engine writes this control at all: it is bound by the current configuration and
    /// enabled in it.
    /// </summary>
    public bool IsDriven(SensorId controlId) =>
        _bindings.TryGetValue(controlId, out var binding) && binding.Enabled;

    /// <summary>
    /// Whether a plugin may hold this control: the engine drives it, and it has a curve to fall
    /// back to when the plugin lets go or dies.
    /// </summary>
    /// <remarks>
    /// The curve requirement is what makes a plugin claim safe to lose. Without one there is no
    /// state to return to, and the plugin would be the only thing standing between the fan and a
    /// stopped fan - so a process dying would be a cooling failure rather than a fan going back to
    /// its curve. A manual pin is held to the weaker rule on purpose: it is written into the
    /// configuration and restored on the next start, so a permanent pin with no curve is a
    /// configuration the engine supports and a user reasonably wants.
    /// </remarks>
    public bool CanBeHeldByPlugin(SensorId controlId) =>
        _bindings.TryGetValue(controlId, out var binding) && binding.Enabled && !binding.CurveId.IsNone;

    /// <summary>
    /// Takes a control on a claimant's behalf, refusing when this configuration cannot honour it.
    /// </summary>
    /// <remarks>
    /// The one place that decides whether a control can be claimed, and it is the same class that
    /// decides whether to write it - which is the point. The ownership registry knows who holds what
    /// and nothing about configuration, so a claim routed straight to it succeeds on a control the
    /// tick loop skips: granted, duties accepted, every one of them discarded without a word. The
    /// trap is that disabling a fan is exactly what a careful user does so that two programs do not
    /// fight over it.
    /// </remarks>
    /// <param name="controlId">The control to claim.</param>
    /// <param name="kind">What sort of claimant this is.</param>
    /// <param name="claimantId">Which specific claimant.</param>
    public ControlAcquireResult TryAcquire(SensorId controlId, ControlOwnerKind kind, string? claimantId = null)
    {
        if (_registry.GetControl(controlId) is null)
        {
            return ControlAcquireResult.Refused(ControlAcquireFailure.UnknownControl);
        }

        var claimable = kind switch
        {
            ControlOwnerKind.Plugin => CanBeHeldByPlugin(controlId),
            ControlOwnerKind.ManualOverride => IsDriven(controlId),
            _ => true,
        };

        if (!claimable)
        {
            return ControlAcquireResult.Refused(ControlAcquireFailure.NotDriven);
        }

        return _ownership.TryAcquire(controlId, kind, claimantId);
    }

    /// <summary>
    /// Drops whatever duty was last asked for on a control, because it changed hands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second half of the fix for a control inheriting a previous holder's duty, and it covers
    /// a case the stamp cannot: the same claimant taking the same control twice. A plugin whose
    /// process died, or that released and re-acquired, would otherwise find its own last duty
    /// waiting for it - stamped with its own name, so indistinguishable from something it had just
    /// asked for. A claim is a session fact and does not survive the session that made it.
    /// </para>
    /// <para>
    /// Cleanup, not the invariant. The stamp is what makes a stale entry unreadable even if this
    /// never ran; this is what stops one existing in the first place. Neither alone is enough,
    /// which is why both are here.
    /// </para>
    /// </remarks>
    private void ForgetRequestedDuty(SensorId controlId)
    {
        lock (_requestGate)
        {
            _requestedDuties.Remove(controlId);
        }
    }

    /// <summary>The duty most recently written to a control, or null if it has not been written.</summary>
    public Duty? GetCommandedDuty(SensorId controlId) =>
        _commandedDuties.TryGetValue(controlId, out var duty) ? duty : null;

    /// <summary>
    /// The duty the control's current owner last asked for, or null when nobody has asked for one.
    /// </summary>
    /// <remarks>
    /// Not the same number as <see cref="GetCommandedDuty"/>, and the difference is the whole point:
    /// the binding's limits, its avoided bands and its slew limiter all sit between them. A claimant
    /// shown only the commanded duty cannot tell "my request was adjusted" from "my request never
    /// arrived", and one shown only its own request cannot tell that the fan is somewhere else
    /// entirely. Both are published so a plugin's window can be honest about which is which.
    /// </remarks>
    public Duty? GetRequestedDuty(SensorId controlId)
    {
        var owner = _ownership.GetOwner(controlId);

        lock (_requestGate)
        {
            // Stamp-checked exactly as the tick loop checks it, so a request left behind by a
            // previous owner is never reported as the current one's.
            return _requestedDuties.TryGetValue(controlId, out var requested) && requested.BelongsTo(owner)
                ? requested.Duty
                : null;
        }
    }

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
                _ownership.ForceRelease(controlId, OwnershipChangeReason.Released);
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
            // Ahead of ownership resolution, which is only safe because nothing can hold a control
            // this skips: TryAcquire refuses a claim on one, and a configuration change that
            // invalidates a standing claim drops it. Without both of those, this line silently
            // discards the duties of whoever owns the control.
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

            var limited = binding.Resolve(target, current);

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
                    if (_requestedDuties.TryGetValue(binding.ControlId, out var requested)
                        && requested.BelongsTo(owner))
                    {
                        return requested.Duty;
                    }
                }

                // An owner that has not said anything yet falls to the curve rather than holding
                // whatever the last owner left on the fan. A claimant takes a control before it has
                // computed a duty - for a plugin those are two round trips with ticks in between -
                // and the alternative is a fan sitting at a stranger's duty under a new owner's
                // name. Falling back is the same promise made when a plugin dies: no owner with an
                // opinion means the curve.
                goto default;

            default:
                return curveOutputs.TryGetValue(binding.CurveId, out var output) ? output : null;
        }
    }

    /// <summary>
    /// A duty someone asked for, together with who they were when they asked.
    /// </summary>
    /// <param name="Duty">What was asked for.</param>
    /// <param name="Kind">What sort of claimant asked.</param>
    /// <param name="ClaimantId">Which specific claimant.</param>
    /// <remarks>
    /// <para>
    /// The stamp is what stops a control inheriting the previous holder's duty. Entries used to be
    /// written and never removed, so a claimant that acquired a control and did not immediately
    /// write to it was handed whatever the last holder had asked for - at the next tick, under its
    /// own name. The shell has always set a duty in the same breath as taking the claim, which is
    /// why this has not bitten yet; a plugin's acquire and its first duty are two round trips with
    /// ticks in between.
    /// </para>
    /// <para>
    /// Checked on the way out rather than cleaned up on the way in, deliberately. Clearing entries
    /// from the ownership-changed event would hold only for as long as every path that changes an
    /// owner raises it, and the cost of missing one is a fan running at a stranger's duty. A stale
    /// entry that can never be read is not a bug.
    /// </para>
    /// </remarks>
    private readonly record struct RequestedDuty(Duty Duty, ControlOwnerKind Kind, string? ClaimantId)
    {
        /// <summary>Whether this request came from the party that holds the control now.</summary>
        public bool BelongsTo(ControlOwner owner) =>
            owner.Kind == Kind && string.Equals(owner.ClaimantId, ClaimantId, StringComparison.Ordinal);
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
