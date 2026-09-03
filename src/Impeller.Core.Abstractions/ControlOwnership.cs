namespace Impeller.Core.Abstractions;

/// <summary>
/// Who is currently driving a control.
/// </summary>
/// <remarks>
/// Exactly one owner at a time, always. This is the invariant that keeps two plugins — or a
/// plugin and a manual override — from fighting over the same fan, and it is enforced by the
/// engine rather than negotiated between claimants.
/// </remarks>
public enum ControlOwnerKind
{
    /// <summary>Driven by its assigned curve. The resting state.</summary>
    Curve = 0,

    /// <summary>Held by the user through the UI, overriding the curve until released.</summary>
    ManualOverride,

    /// <summary>Held by a plugin that acquired it and has not yet released it.</summary>
    Plugin,

    /// <summary>
    /// Held by the failsafe path after the engine lost its grip — a watchdog trip, or a
    /// deliberate shutdown. Released only when the engine confirms it is healthy again.
    /// </summary>
    Failsafe,
}

/// <summary>
/// A claim on a control.
/// </summary>
/// <param name="Kind">What sort of claimant holds it.</param>
/// <param name="ClaimantId">
/// Which specific claimant: a plugin's manifest id, or <see langword="null"/> for
/// <see cref="ControlOwnerKind.Curve"/> and <see cref="ControlOwnerKind.Failsafe"/>.
/// </param>
/// <param name="AcquiredAt">When the claim was taken, for diagnostics and stuck-claim detection.</param>
public readonly record struct ControlOwner(
    ControlOwnerKind Kind,
    string? ClaimantId,
    DateTimeOffset AcquiredAt)
{
    /// <summary>The default owner: the control's own curve.</summary>
    public static ControlOwner ByCurve(TimeProvider time) =>
        new(ControlOwnerKind.Curve, null, time.GetUtcNow());

    /// <summary>True when the curve is free to drive this control.</summary>
    public bool IsCurve => Kind == ControlOwnerKind.Curve;

    /// <summary>
    /// Whether a claim can be taken right now. Only a curve-owned control is available;
    /// every other state means someone already holds it.
    /// </summary>
    public bool IsAvailableForClaim => Kind == ControlOwnerKind.Curve;
}

/// <summary>Why an attempt to acquire a control did not succeed.</summary>
public enum ControlAcquireFailure
{
    /// <summary>No control with that id exists.</summary>
    UnknownControl = 0,

    /// <summary>
    /// The caller has not been granted write access to this control.
    /// </summary>
    /// <remarks>
    /// A plugin manifest <em>requests</em> capabilities; the user grants them, one control at a
    /// time. This is the refusal a plugin sees when it claims a fan nobody has ticked for it, and
    /// it is an ordinary state rather than an error — the user may not have been asked yet.
    /// </remarks>
    NotPermitted,

    /// <summary>Someone else already holds it. Deliberately not queued — the caller decides what to do.</summary>
    AlreadyOwned,

    /// <summary>The engine is shutting down or in a failsafe state and is not granting claims.</summary>
    EngineUnavailable,

    /// <summary>
    /// The engine is not driving this control, so there is nothing to take over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A control is claimable only while it is enabled in the current configuration and has a curve
    /// assigned. The curve is what the control returns to when the claim ends, by release or by the
    /// claimant dying; without one there is no state to fall back to, and the claimant would be the
    /// only thing standing between the fan and a stopped fan.
    /// </para>
    /// <para>
    /// This is a refusal rather than a silent no-op because the tick loop skips a disabled binding
    /// before it looks at ownership: a claim on one would be granted, accepted, and then have every
    /// duty discarded without a word. The trap is that disabling the fan is exactly what a careful
    /// user does so that two programs do not fight over it.
    /// </para>
    /// </remarks>
    NotDriven,
}

/// <summary>
/// The result of trying to take ownership of a control.
/// </summary>
public readonly record struct ControlAcquireResult
{
    private ControlAcquireResult(bool succeeded, ControlAcquireFailure failure, ControlOwner? currentOwner)
    {
        Succeeded = succeeded;
        Failure = failure;
        CurrentOwner = currentOwner;
    }

    /// <summary>Whether the claim was granted.</summary>
    public bool Succeeded { get; }

    /// <summary>Why it was refused. Meaningful only when <see cref="Succeeded"/> is false.</summary>
    public ControlAcquireFailure Failure { get; }

    /// <summary>
    /// Who holds the control instead, when the refusal was <see cref="ControlAcquireFailure.AlreadyOwned"/>.
    /// Surfaced to the user so a conflict names the other claimant rather than failing anonymously.
    /// </summary>
    public ControlOwner? CurrentOwner { get; }

    /// <summary>A granted claim.</summary>
    public static ControlAcquireResult Granted() => new(true, default, null);

    /// <summary>A refusal.</summary>
    public static ControlAcquireResult Refused(ControlAcquireFailure failure, ControlOwner? currentOwner = null) =>
        new(false, failure, currentOwner);
}
