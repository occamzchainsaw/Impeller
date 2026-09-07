using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine;

/// <summary>What one tick did, for diagnostics and for the UI's live view.</summary>
/// <param name="CurveOutputs">Each curve's output this tick. A null value means the curve could not produce one.</param>
/// <param name="CommandedDuties">The duty now standing at each control the engine drives.</param>
/// <param name="TargetDuties">
/// What each control's owner asked for this tick, before the binding's limits and the start/stop
/// gate. Carried because the difference between this and <paramref name="CommandedDuties"/> is
/// invisible from the outside and is the answer to "why is my fan off".
/// </param>
/// <param name="ControlsWritten">How many controls actually received a hardware write.</param>
/// <param name="Faults">Controls whose write threw, paired with the exception.</param>
public readonly record struct TickResult(
    IReadOnlyDictionary<CurveId, Duty?> CurveOutputs,
    IReadOnlyDictionary<SensorId, Duty> CommandedDuties,
    IReadOnlyDictionary<SensorId, Duty> TargetDuties,
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

    /// <summary>
    /// How far a duty must move before it is written for having changed.
    /// </summary>
    /// <remarks>
    /// Below this the write would land on the same value at the hardware, so it is left for the
    /// restatement schedule to make rather than made twice.
    /// </remarks>
    private const float WriteThreshold = 0.01f;

    /// <summary>
    /// The longest a control's duty may stand at the hardware without being written again.
    /// </summary>
    /// <remarks>
    /// A floor rather than a period: at most one control is restated per tick, so with several fans
    /// a full round takes as many ticks as there are fans. Short enough that a header firmware has
    /// reclaimed comes back before a person notices it, long enough that the restatements are a
    /// rounding error next to the work a tick already does.
    /// </remarks>
    private static readonly TimeSpan RestatementInterval = TimeSpan.FromSeconds(2);

    private readonly Lock _requestGate = new();
    private readonly Dictionary<SensorId, RequestedDuty> _requestedDuties = [];

    /// <summary>How long since each control was last written, for the restatement schedule.</summary>
    private readonly Dictionary<SensorId, TimeSpan> _sinceWrite = [];

    /// <summary>Controls currently handed back because nothing is driving them.</summary>
    /// <remarks>
    /// Remembered so the handover happens once. Calling into the firmware every tick would be
    /// pointless traffic on a chip that is slow to write, and on hardware that answers a restore
    /// with a fresh default it would fight anything the firmware then did.
    /// </remarks>
    private readonly HashSet<SensorId> _resting = [];
    private readonly Dictionary<SensorId, Duty> _commandedDuties = [];

    /// <summary>
    /// What each control's owner asked for on the last tick, whoever the owner was.
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="_requestedDuties"/>, which only holds what a claimant asked for
    /// and is empty for the ordinary case of a fan on a curve. This is the output of
    /// <see cref="ResolveTarget"/> — the number the binding's limits and the start/stop gate are
    /// then applied to — and it exists so the difference between "the curve wants nothing" and
    /// "the curve wants 30% and this fan stalls below 40%" can be shown rather than inferred.
    /// </remarks>
    private readonly Dictionary<SensorId, Duty> _targetDuties = [];

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

        var previous = _bindings;

        // Bindings first: the sort needs to know which curve drives which control before it can
        // follow a sync curve's control edge back to the curve behind it.
        var bound = bindings.ToDictionary(binding => binding.ControlId);

        // A control that was resting may now have a curve, and one that had a curve may not, so
        // the record of a rest goes when the rules behind that rest change - and only then. This
        // used to clear the lot, which was free while resting happened once, on a transition. The
        // tick loop rests every switched-off fan now, and this runs on every save, which while a
        // slider is moving is four times a second: clearing it wholesale would re-write every fan
        // the user had switched off, several times a second, for as long as they dragged.
        _resting.RemoveWhere(controlId =>
            !bound.TryGetValue(controlId, out var now)
            || !previous.TryGetValue(controlId, out var was)
            || now.Enabled != was.Enabled
            || now.CurveId != was.CurveId
            || now.FailsafeDuty != was.FailsafeDuty);
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
        RestControlsNoLongerDriven(previous);
        RestoreManualPins(bound.Values);
    }

    /// <summary>
    /// Hands back any control this configuration has stopped driving, or dropped altogether.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Switching a fan off in Impeller, or taking it off the page, used to leave it at whatever
    /// duty was last written to it for as long as the machine stayed on. Nothing walks a control
    /// the configuration no longer drives, so nothing ever moved the fan again: a user who pinned
    /// one at 20 % and then switched it off got a fan permanently at 20 %, and no reason to look.
    /// </para>
    /// <para>
    /// So the handover happens on the way out, once, while the engine still remembers what the
    /// control's binding said. This is the same hazard the resting state fixed for a curveless
    /// fan; these two were the last ways left to reach it.
    /// </para>
    /// <para>
    /// The test is whether the engine ever commanded the control, not whether its binding was
    /// switched on. Those were the same thing until a claimant could hold a switched-off fan, and
    /// the difference is a fan a plugin was driving when the user removed it — which needs the
    /// handover exactly as much, and which the old test skipped. A control the engine has never
    /// written to is left alone, because handing back a fan nobody took is still a write.
    /// </para>
    /// <para>
    /// A write that fails here is not reported: the tick loop will never look at this control
    /// again, so there is nowhere to report it to. The fan stays where it was, which is exactly
    /// what happened before this existed.
    /// </para>
    /// </remarks>
    private void RestControlsNoLongerDriven(IReadOnlyDictionary<SensorId, ControlBinding> previous)
    {
        var unreported = new Dictionary<SensorId, Exception>();

        foreach (var (controlId, was) in previous)
        {
            if ((_bindings.TryGetValue(controlId, out var now) && now.Enabled)
                || !_commandedDuties.ContainsKey(controlId)
                || _registry.GetControl(controlId) is not { } control)
            {
                continue;
            }

            Rest(was, control, unreported);
        }
    }

    /// <summary>
    /// Frees any control that is still held but is no longer one this configuration lets a
    /// claimant hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Claimability is checked when a claim is taken, but the configuration can change under a
    /// standing claim at any time afterwards - someone takes the fan off the page. Without this the
    /// claim survives into a configuration that cannot honour it: the tick loop only walks the
    /// bindings it has, so the holder goes on setting duties that are accepted and then silently
    /// discarded, while its own display shows a fan speed nobody is commanding.
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
                ControlOwnerKind.Plugin or ControlOwnerKind.ManualOverride => CanBeClaimed(controlId),

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

            // Taken whether or not the binding is switched on. A pin is itself the instruction to
            // drive the fan, and the tick loop honours a claim on a switched-off binding, so making
            // the pin wait for a curve it was written to ignore only ever lost it.
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
    /// The sensor reporting this control's actual speed, or none when nothing is paired with it.
    /// </summary>
    /// <remarks>
    /// Worth publishing rather than leaving each consumer to guess. The pairing is established by
    /// measurement during calibration - drive a fan, see which tachometer moves - and no amount of
    /// name matching reproduces that reliably across two backends that disagree about what to call
    /// anything.
    /// </remarks>
    public SensorId GetPairedFanSensor(SensorId controlId) =>
        _bindings.TryGetValue(controlId, out var binding)
            ? binding.PairedFanSensorId
            : SensorId.None;

    /// <summary>
    /// Whether anything may hold this control: it is a fan the user has put in the configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole rule, and it is deliberately almost nothing. It used to require a curve, and then
    /// it required the binding to be switched on, and both were one mistake in different clothes:
    /// they made handing a fan to a plugin - or to your own hand - conditional on first setting
    /// that fan up in Impeller to be driven by Impeller. For a plugin that is backwards. Not
    /// having to configure the fan in Impeller is the entire reason the plugin channel exists, and
    /// a user who granted a fan to a program and was then told to go and give it a curve was being
    /// asked to invent something they did not want in order to have it ignored.
    /// </para>
    /// <para>
    /// What made the rule look necessary was the tick loop skipping a switched-off binding before
    /// it resolved ownership, so a claim on one was granted and every duty then discarded in
    /// silence. The honest fix was there rather than here: switched off means Impeller's own
    /// curves leave this fan alone, not that nobody may drive it, and <see cref="Rest"/> gives it
    /// somewhere defined to go the moment the claimant lets go.
    /// </para>
    /// <para>
    /// What remains is that the fan has to be in the configuration - less a restriction than a
    /// fact. A binding is where the fan's limits, its calibration and its paired tachometer live,
    /// and there is nothing to drive a fan through without one.
    /// </para>
    /// </remarks>
    public bool CanBeClaimed(SensorId controlId) => _bindings.ContainsKey(controlId);

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
            ControlOwnerKind.Plugin or ControlOwnerKind.ManualOverride => CanBeClaimed(controlId),
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
    /// What the control's owner asked for on the last tick, whoever the owner is.
    /// </summary>
    /// <remarks>
    /// The curve's output for a curve-driven fan, the claimant's request for a held one. Sits one
    /// step before <see cref="GetCommandedDuty"/>: the binding's minimum and maximum, its avoided
    /// bands, the slew limiter and the start/stop gate all come after it. Published so a fan
    /// commanded 0% can say the curve asked for 30% and this fan stalls below 40%, rather than
    /// leaving somebody to conclude the curve is broken.
    /// </remarks>
    public Duty? GetTargetDuty(SensorId controlId) =>
        _targetDuties.TryGetValue(controlId, out var duty) ? duty : null;

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
        _resting.Clear();

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
            new Dictionary<SensorId, Duty>(_targetDuties),
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
        _resting.Clear();
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
                new Dictionary<SensorId, Duty>(_targetDuties),
                0,
                new Dictionary<SensorId, Exception>());
        }

        var curveOutputs = EvaluateCurves(elapsed);
        var restate = SelectForRestatement(elapsed);
        var written = 0;
        var faults = new Dictionary<SensorId, Exception>();

        foreach (var binding in _bindings.Values)
        {
            if (_registry.GetControl(binding.ControlId) is not { } control)
            {
                continue;
            }

            // A measuring run drives the hardware itself, and while one holds a control the tick
            // loop must not touch it at all.
            //
            // The run claims each control it needs and then writes duties straight to the device,
            // deliberately bypassing ramp limits and the start/stop gate: a calibration measures
            // what a fan does at a duty, not what it does while being eased toward one. But it
            // never registers a requested duty, so ResolveTarget saw a claimant with no opinion
            // and fell back to the curve - correct for a plugin that has taken a fan and not yet
            // said anything, and exactly wrong here. The loop then wrote the curve's answer over
            // the run's, once whenever the curve moved and again on every restatement, and the fan
            // never sat still at the duty being measured. Pairing looked for a speed that fell by
            // a fifth and found nothing, or found something else.
            if (IsBeingMeasured(binding.ControlId))
            {
                continue;
            }

            // Switched off means Impeller's own curves leave this fan alone. It has never meant
            // that nobody may drive it, and reading it that way here - ahead of ownership, so a
            // claimant's duties were accepted and then dropped on the floor - is what forced the
            // rule that a fan needed setting up in Impeller before a plugin could be handed it. A
            // claimant holding the fan is something driving the fan.
            //
            // Unclaimed, it goes to its resting state: back to the board's own firmware where the
            // hardware allows that, and to its failsafe duty where it does not. Rest does it once
            // and remembers, so this is not a write per tick.
            //
            // Only if the engine has actually commanded it, though. A fan that has been switched
            // off since the day it was added has never been touched, and handing back a fan nobody
            // ever took would break the promise that nothing reaches the hardware until the user
            // asks - on a first run, once per header on the board.
            if (!binding.Enabled && _ownership.GetOwner(binding.ControlId).IsCurve)
            {
                // Nobody is asking for anything, so there is no target to report. Left behind, it
                // would have the card claim a curve wants something for a fan that is switched off.
                _targetDuties.Remove(binding.ControlId);

                if (_commandedDuties.ContainsKey(binding.ControlId) && Rest(binding, control, faults))
                {
                    written++;
                }

                continue;
            }

            if (ResolveTarget(binding, curveOutputs) is not { } target)
            {
                // A control with a curve that momentarily says nothing holds its last duty - a
                // sensor that stopped reporting for one tick is not a reason to move a fan. A
                // control with no curve at all is a different thing: there is nothing for it to
                // fall back to, ever, so rather than freezing at whatever the last owner left on
                // it, it goes to a resting state that is actually defined.
                if (binding.CurveId.IsNone
                    && _ownership.GetOwner(binding.ControlId).IsCurve
                    && Rest(binding, control, faults))
                {
                    written++;
                }
                else if (binding.ControlId == restate
                    && _commandedDuties.TryGetValue(binding.ControlId, out var held))
                {
                    // Holding a duty is something the engine keeps doing to the hardware, not
                    // something it decides once and remembers. A sensor that has gone quiet is the
                    // longest a header ever goes without a fresh value, which makes it the case
                    // most likely to be reclaimed, so a held duty is restated like any other.
                    if (TryWrite(binding, control, held, faults))
                    {
                        written++;
                    }
                }

                continue;
            }

            _resting.Remove(binding.ControlId);
            _targetDuties[binding.ControlId] = target;

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

            // A duty that has moved is written now; one that has not is written when its turn
            // comes round. Skipping the unchanged write entirely is what this replaces, and it was
            // wrong: a duty is not a setting the hardware stores, it is a claim on a header that
            // lapses as soon as nothing restates it. Board firmware takes an unattended header back
            // and drives it from its own curve, and the failure is silent in the worst way - the
            // engine goes on reporting the duty it last wrote, so the fans run up under load while
            // every screen in the shell reads 35%. A machine sitting at a steady temperature is the
            // worst case rather than the safest one, because a curve whose output is not moving is
            // a header nothing is touching.
            if (Duty.Distance(current, next) >= WriteThreshold
                || !_commandedDuties.ContainsKey(binding.ControlId)
                || binding.ControlId == restate)
            {
                if (TryWrite(binding, control, next, faults))
                {
                    written++;
                }
            }
        }

        return new TickResult(
            curveOutputs,
            new Dictionary<SensorId, Duty>(_commandedDuties),
            new Dictionary<SensorId, Duty>(_targetDuties),
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
    /// Chooses the one control whose standing duty is restated to the hardware this tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One per tick rather than all of them, because a write is not free. On the Nuvoton parts this
    /// runs on, a single duty write takes about a tenth of a second: the driver opens a fan
    /// configuration phase, sleeps, sets the manual-mode bit and the value, commits, and sleeps
    /// again — all of it holding the ISA bus that sensor reads also have to take. Restating six
    /// headers every tick would spend half of every second in that path and leave the readings
    /// fighting the writes for the bus.
    /// </para>
    /// <para>
    /// So the restatements are spread out. The control that has gone longest without a write goes
    /// first, which needs no ordering from the binding collection and self-corrects around whatever
    /// the change-driven writes happen to have covered already. With one board's worth of fans that
    /// puts a full round at a few seconds — long enough to be cheap, short enough that a header
    /// firmware has taken back is reclaimed before anyone hears it.
    /// </para>
    /// </remarks>
    /// <returns>The control to restate, or <see cref="SensorId.None"/> when none is due.</returns>
    private SensorId SelectForRestatement(TimeSpan elapsed)
    {
        var oldest = SensorId.None;
        var longest = RestatementInterval;

        foreach (var controlId in _bindings.Keys)
        {
            // A control the engine has not commanded has nothing to restate, one that is resting
            // has deliberately been handed back to firmware, and one being measured belongs to
            // the run - restating a duty at it is the same interference as writing a new one.
            if (!_commandedDuties.ContainsKey(controlId)
                || _resting.Contains(controlId)
                || IsBeingMeasured(controlId))
            {
                _sinceWrite.Remove(controlId);
                continue;
            }

            var age = _sinceWrite.GetValueOrDefault(controlId) + elapsed;
            _sinceWrite[controlId] = age;

            if (age >= longest)
            {
                longest = age;
                oldest = controlId;
            }
        }

        return oldest;
    }

    /// <summary>
    /// Writes a duty to the hardware and records it as the duty now standing at that control.
    /// </summary>
    /// <remarks>
    /// A write that throws is collected rather than raised: one header that has stopped answering
    /// must not cost every other fan on the board its tick.
    /// </remarks>
    /// <returns>Whether the hardware accepted the write.</returns>
    private bool TryWrite(
        ControlBinding binding,
        IControl control,
        Duty duty,
        Dictionary<SensorId, Exception> faults)
    {
        try
        {
            control.Write(duty);
            _commandedDuties[binding.ControlId] = duty;
            _sinceWrite[binding.ControlId] = TimeSpan.Zero;
            return true;
        }
        catch (Exception ex)
        {
            faults[binding.ControlId] = ex;
            return false;
        }
    }

    /// <summary>
    /// Puts a control with nothing driving it into a defined resting state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Handed back to its own firmware where the hardware can do that, which is the honest answer:
    /// Impeller is not driving this fan, so the thing that was driving it before Impeller existed
    /// should have it back. Where it cannot, the binding's failsafe duty applies - the same value
    /// used whenever the engine has to leave a fan somewhere safe without knowing what it should be
    /// doing.
    /// </para>
    /// <para>
    /// The alternative, and what this replaces, was to hold the last commanded duty forever. That
    /// left a fan parked at whatever number some plugin chose before it exited, with nothing
    /// managing it and no way for the user to tell that had happened.
    /// </para>
    /// </remarks>
    /// <returns>Whether a duty was written.</returns>
    private bool Rest(ControlBinding binding, IControl control, Dictionary<SensorId, Exception> faults)
    {
        if (!_resting.Add(binding.ControlId))
        {
            return false;
        }

        // Resting is nobody asking for anything. Whatever the last owner wanted is no longer a
        // live request, and reporting it would explain a fan's duty by something that stopped
        // applying.
        _targetDuties.Remove(binding.ControlId);

        if (control.SupportsAutomaticMode && control.TryRestoreAutomaticMode())
        {
            // The firmware has it now, so the engine no longer knows what duty is standing at it -
            // and reporting a stale number would be worse than reporting none.
            _commandedDuties.Remove(binding.ControlId);
            return false;
        }

        try
        {
            control.Write(binding.FailsafeDuty);
            _commandedDuties[binding.ControlId] = binding.FailsafeDuty;
            return true;
        }
        catch (Exception ex)
        {
            faults[binding.ControlId] = ex;
            return false;
        }
    }

    /// <summary>
    /// Works out what duty a control should be heading toward, based on who owns it.
    /// Returns null when the owner has nothing to say, in which case the control holds.
    /// </summary>
    /// <summary>
    /// Whether a calibration or pairing run currently holds this control.
    /// </summary>
    /// <remarks>
    /// Identified by claimant rather than by a flag on the loop, because a run is a claim like any
    /// other and there is exactly one claimant name for it. Nothing else about ownership needs a
    /// special case: the run takes the control the ordinary way, and this is only about which of
    /// two writers gets to talk to the hardware while it holds it.
    /// </remarks>
    private bool IsBeingMeasured(SensorId controlId)
    {
        var owner = _ownership.GetOwner(controlId);

        return owner.Kind == ControlOwnerKind.ManualOverride
            && string.Equals(owner.ClaimantId, Tuning.TuningCoordinator.Claimant, StringComparison.Ordinal);
    }

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
