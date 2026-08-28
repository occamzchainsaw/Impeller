using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine;

/// <summary>
/// Tracks who is driving each control, and enforces that it is only ever one party.
/// </summary>
/// <remarks>
/// <para>
/// Ownership is exclusive by construction, not by convention. A control rests with its curve;
/// a plugin or a manual override may take it; and while taken, every other claim is refused
/// outright rather than queued. There is deliberately no "last writer wins" path: a fan quietly
/// changing which plugin drives it is a worse failure than a claim the caller has to handle.
/// </para>
/// <para>
/// Releasing always returns the control to its curve — never to a second claimant waiting behind
/// the first — so the resting state is reachable from anywhere and a plugin cannot inherit a
/// control it never asked for.
/// </para>
/// <para>Thread-safe: plugins acquire and release off the tick loop's thread.</para>
/// </remarks>
public sealed class ControlOwnershipRegistry(TimeProvider timeProvider)
{
    private readonly TimeProvider _time = timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<SensorId, ControlOwner> _owners = [];

    private bool _grantsSuspended;

    /// <summary>Raised after ownership of a control changes, for the UI and for diagnostics.</summary>
    public event EventHandler<ControlOwnershipChange>? OwnershipChanged;

    /// <summary>
    /// Who currently drives a control. Unknown controls report as curve-owned, since that is
    /// the state a control enters as soon as it is registered.
    /// </summary>
    public ControlOwner GetOwner(SensorId controlId)
    {
        lock (_gate)
        {
            return _owners.TryGetValue(controlId, out var owner)
                ? owner
                : ControlOwner.ByCurve(_time);
        }
    }

    /// <summary>
    /// Attempts to take a control.
    /// </summary>
    /// <param name="controlId">The control to claim.</param>
    /// <param name="kind">What sort of claimant this is.</param>
    /// <param name="claimantId">
    /// The plugin's manifest id, for a <see cref="ControlOwnerKind.Plugin"/> claim. Ignored otherwise.
    /// </param>
    /// <returns>
    /// A granted result, or a refusal naming the current owner so a conflict can be reported
    /// against something specific rather than failing anonymously.
    /// </returns>
    public ControlAcquireResult TryAcquire(SensorId controlId, ControlOwnerKind kind, string? claimantId = null)
    {
        if (kind == ControlOwnerKind.Curve)
        {
            // "Acquiring the curve" is a release; routing it here would let a caller
            // evict another owner by claiming to be the curve.
            throw new ArgumentException(
                "Use Release to return a control to its curve.", nameof(kind));
        }

        lock (_gate)
        {
            if (_grantsSuspended && kind != ControlOwnerKind.Failsafe)
            {
                return ControlAcquireResult.Refused(ControlAcquireFailure.EngineUnavailable);
            }

            var current = GetOwnerLocked(controlId);

            // The failsafe path outranks everything: when the engine has lost its grip, no
            // existing claim should keep a fan parked at a duty nobody is maintaining.
            if (!current.IsAvailableForClaim && kind != ControlOwnerKind.Failsafe)
            {
                return ControlAcquireResult.Refused(ControlAcquireFailure.AlreadyOwned, current);
            }

            var owner = new ControlOwner(kind, claimantId, _time.GetUtcNow());
            _owners[controlId] = owner;
            Raise(controlId, current, owner);
            return ControlAcquireResult.Granted();
        }
    }

    /// <summary>
    /// Returns a control to its curve, if <paramref name="claimantId"/> is the one holding it.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the control was released. <see langword="false"/> when someone
    /// else holds it, so a confused plugin cannot free a control it does not own.
    /// </returns>
    public bool Release(SensorId controlId, string? claimantId = null)
    {
        lock (_gate)
        {
            var current = GetOwnerLocked(controlId);

            if (current.IsCurve)
            {
                return false;
            }

            if (current.Kind == ControlOwnerKind.Plugin
                && !string.Equals(current.ClaimantId, claimantId, StringComparison.Ordinal))
            {
                return false;
            }

            return ReleaseLocked(controlId, current);
        }
    }

    /// <summary>
    /// Returns a control to its curve regardless of who holds it.
    /// </summary>
    /// <remarks>
    /// Used by the plugin watchdog when a plugin stops answering its heartbeat, and by the
    /// engine when recovering from a failsafe. A hung plugin must not be able to strand a fan
    /// simply by never releasing it.
    /// </remarks>
    public bool ForceRelease(SensorId controlId)
    {
        lock (_gate)
        {
            return ReleaseLocked(controlId, GetOwnerLocked(controlId));
        }
    }

    /// <summary>
    /// Releases every control held by one claimant, returning how many were freed.
    /// Called when a plugin is unloaded, disabled, or found unhealthy.
    /// </summary>
    public int ForceReleaseAllFrom(string claimantId)
    {
        ArgumentNullException.ThrowIfNull(claimantId);

        lock (_gate)
        {
            var held = _owners
                .Where(pair => pair.Value.Kind == ControlOwnerKind.Plugin
                    && string.Equals(pair.Value.ClaimantId, claimantId, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToArray();

            foreach (var controlId in held)
            {
                ReleaseLocked(controlId, _owners[controlId]);
            }

            return held.Length;
        }
    }

    /// <summary>Every control not currently resting with its curve.</summary>
    public IReadOnlyDictionary<SensorId, ControlOwner> ActiveClaims
    {
        get
        {
            lock (_gate)
            {
                return _owners
                    .Where(pair => !pair.Value.IsCurve)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
            }
        }
    }

    /// <summary>
    /// Stops granting new claims, other than failsafe ones. Set while the engine is shutting
    /// down or recovering, so a plugin cannot take a control the engine is about to stop driving.
    /// </summary>
    public void SuspendGrants()
    {
        lock (_gate)
        {
            _grantsSuspended = true;
        }
    }

    /// <summary>Resumes granting claims after a suspension.</summary>
    public void ResumeGrants()
    {
        lock (_gate)
        {
            _grantsSuspended = false;
        }
    }

    private ControlOwner GetOwnerLocked(SensorId controlId) =>
        _owners.TryGetValue(controlId, out var owner) ? owner : ControlOwner.ByCurve(_time);

    private bool ReleaseLocked(SensorId controlId, ControlOwner current)
    {
        if (current.IsCurve)
        {
            return false;
        }

        var owner = ControlOwner.ByCurve(_time);
        _owners[controlId] = owner;
        Raise(controlId, current, owner);
        return true;
    }

    private void Raise(SensorId controlId, ControlOwner previous, ControlOwner current) =>
        OwnershipChanged?.Invoke(this, new ControlOwnershipChange(controlId, previous, current));
}

/// <summary>Describes a change of control ownership.</summary>
/// <param name="ControlId">The control whose owner changed.</param>
/// <param name="Previous">Who held it before.</param>
/// <param name="Current">Who holds it now.</param>
public sealed record ControlOwnershipChange(
    SensorId ControlId,
    ControlOwner Previous,
    ControlOwner Current);
