namespace Impeller.Plugins.Abstractions;

/// <summary>Why a plugin's claim on a control was refused.</summary>
public enum PluginAcquireFailure
{
    /// <summary>The claim was granted. Present so a successful outcome has a value to carry.</summary>
    None = 0,

    /// <summary>No control with that reference exists on this machine.</summary>
    UnknownControl,

    /// <summary>
    /// This control was not granted to this plugin.
    /// </summary>
    /// <remarks>
    /// Requesting <see cref="PluginCapability.ControlFans"/> in a manifest is asking; the user
    /// grants, one fan at a time. A plugin should treat this as "not yet", not as an error — the
    /// user may be looking at the approval prompt right now.
    /// </remarks>
    NotPermitted,

    /// <summary>
    /// Someone else holds it. The outcome names who.
    /// </summary>
    /// <remarks>
    /// Deliberately not queued. A plugin that waits in line for a fan is a plugin that takes it at
    /// an unpredictable moment later; the caller decides what to do instead.
    /// </remarks>
    AlreadyOwned,

    /// <summary>
    /// This fan is not in Impeller's configuration, so there is nothing to drive it through.
    /// </summary>
    /// <remarks>
    /// The fan has to be on Impeller's dashboard. It does not have to be switched on there, and it
    /// does not need a curve. A binding is where a fan's limits, its calibration and its paired
    /// tachometer live, so a fan without one is a fan the engine has nothing to say about — which
    /// in practice means it was taken off the dashboard after this plugin was granted it.
    /// </remarks>
    NotDriven,

    /// <summary>The engine is in failsafe, starting, or stopping, and is granting nothing.</summary>
    EngineUnavailable,

    /// <summary>The connection has not been admitted yet, or was refused.</summary>
    NotAdmitted,
}

/// <summary>The answer to a claim.</summary>
/// <param name="Granted">Whether the plugin now owns the control.</param>
/// <param name="Failure">Why not, when it does not.</param>
/// <param name="Holder">
/// Who holds it instead, when the refusal was <see cref="PluginAcquireFailure.AlreadyOwned"/>.
/// </param>
/// <param name="HolderId">Which specific claimant holds it, where that means anything.</param>
/// <param name="Message">A sentence a plugin can show its user without rewriting.</param>
public sealed record AcquireOutcome(
    bool Granted,
    PluginAcquireFailure Failure,
    ControlHolder Holder,
    string? HolderId,
    string Message)
{
    /// <summary>A granted claim.</summary>
    public static AcquireOutcome Ok(string pluginId) =>
        new(true, PluginAcquireFailure.None, ControlHolder.Plugin, pluginId, "Granted.");

    /// <summary>A refusal.</summary>
    public static AcquireOutcome No(
        PluginAcquireFailure failure,
        string message,
        ControlHolder holder = ControlHolder.Curve,
        string? holderId = null) =>
        new(false, failure, holder, holderId, message);
}

/// <summary>
/// Why a plugin stopped owning a control it had claimed.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is something a plugin needs to react to differently, and without being told
/// it would learn about none of them — its writes would simply start being ignored while its own
/// window went on showing the duty it last asked for.
/// </para>
/// <para>
/// A claim is never restored automatically. Whatever the reason, re-acquiring is the plugin's
/// decision to make, and for several of these reasons it would be the wrong one.
/// </para>
/// </remarks>
public enum ControlLostReason
{
    /// <summary>
    /// The engine engaged its failsafe and took every control.
    /// </summary>
    /// <remarks>
    /// Do not re-acquire on a timer. The engine is in trouble; the fan is at its failsafe duty,
    /// which is the safest place for it to be.
    /// </remarks>
    Failsafe = 0,

    /// <summary>The user revoked this specific fan. The plugin stays connected and may still read.</summary>
    Revoked,

    /// <summary>The user disabled the whole plugin. The connection is closing.</summary>
    PluginDisabled,

    /// <summary>The user took the fan by hand from the Impeller window.</summary>
    TakenByUser,

    /// <summary>
    /// The lease expired: no duty arrived for this control within the window the plugin itself
    /// asked for.
    /// </summary>
    /// <remarks>
    /// This is the plugin's own safety net firing. Something in its update path stopped producing
    /// values while the process stayed alive — which is precisely the failure a dead-process check
    /// cannot catch.
    /// </remarks>
    LeaseExpired,

    /// <summary>
    /// The configuration changed and this control is no longer driven, so it can no longer be held.
    /// </summary>
    /// <remarks>
    /// The grant is checked when a claim is taken, but a user can disable the fan or unassign its
    /// curve at any time afterwards. Rather than leave a plugin holding a control the engine has
    /// stopped writing, the claim is dropped and the plugin told.
    /// </remarks>
    ConfigurationChanged,

    /// <summary>The plugin released it. Reported for symmetry so a plugin's own state machine has one path.</summary>
    Released,
}

/// <summary>How much a plugin wants written to its log.</summary>
/// <remarks>
/// Plugin log lines go to the engine's log file, prefixed with the manifest id. That is the only
/// reason this exists: when a user reports that their fans behaved oddly at 3am, the plugin's own
/// account of what it was doing needs to be in the same file, on the same clock, as the engine's.
/// </remarks>
public enum PluginLogLevel
{
    /// <summary>Detail only useful when something is being diagnosed.</summary>
    Debug = 0,

    /// <summary>Ordinary progress.</summary>
    Information,

    /// <summary>Something went wrong that the plugin worked around.</summary>
    Warning,

    /// <summary>Something went wrong that it did not.</summary>
    Error,
}
