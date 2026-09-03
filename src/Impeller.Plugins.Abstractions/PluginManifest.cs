namespace Impeller.Plugins.Abstractions;

/// <summary>
/// Something a plugin asks to be allowed to do.
/// </summary>
/// <remarks>
/// A manifest <em>requests</em> capabilities; only the user grants them. A plugin that asks for
/// everything and is granted nothing is a perfectly ordinary state, and is what every plugin looks
/// like the first time it connects.
/// </remarks>
public enum PluginCapability
{
    /// <summary>
    /// See the sensor list and receive readings for the sensors it subscribes to.
    /// </summary>
    /// <remarks>
    /// Granted all-or-nothing, because per-sensor read consent across a couple of hundred sensors
    /// is a dialog nobody would read. What is per-sensor is the <em>subscription</em>: a plugin
    /// receives only the ids it asked for, so the engine is not serialising the whole machine once
    /// a second for every connected plugin.
    /// </remarks>
    ReadSensors = 0,

    /// <summary>
    /// Claim controls and set their duty.
    /// </summary>
    /// <remarks>
    /// Granted <em>per fan</em>. That is the difference between "this app may drive fans" and "this
    /// app may drive that fan", and it is the whole reason the grant list exists.
    /// </remarks>
    ControlFans,

    /// <summary>
    /// Contribute the plugin's own sensors and controls to the engine.
    /// </summary>
    /// <remarks>
    /// The wire shape exists in this build; the engine side does not. A request for it is answered
    /// with <see cref="ProviderOutcome.NotImplementedInThisBuild"/> rather than a refusal, so an
    /// author is never debugging a permissions problem that is really a missing feature.
    /// </remarks>
    ProvideHardware,
}

/// <summary>
/// What a plugin says about itself when it connects.
/// </summary>
/// <param name="Id">
/// Reverse-DNS and stable for the life of the plugin, for example
/// <c>com.occamzchainsaw.rigfan</c>. This is the key everything is stored under.
/// </param>
/// <param name="DisplayName">What to call it in the UI. Free to change at any time.</param>
/// <param name="Version">The plugin's own version, shown to the user and recorded in diagnostics.</param>
/// <param name="ProtocolVersion">Which version of this contract the plugin was built against.</param>
/// <param name="Requests">The capabilities it would like. Requesting is not being granted.</param>
/// <remarks>
/// <para>
/// The split between <paramref name="Id"/> and <paramref name="DisplayName"/> is the most
/// important line in this contract, and it exists because of the app Impeller replaces: there a
/// plugin's name is simultaneously its UI label, its configuration key, its disable key and its
/// eviction key, so renaming a plugin breaks every binding the user has. Nothing here keys off the
/// display name.
/// </para>
/// </remarks>
public sealed record PluginManifest(
    string Id,
    string DisplayName,
    string Version,
    int ProtocolVersion,
    IReadOnlyList<PluginCapability> Requests);

/// <summary>Where a plugin stands with the engine.</summary>
public enum PluginAdmissionState
{
    /// <summary>
    /// Seen, remembered, and granted nothing. The state every plugin starts in.
    /// </summary>
    /// <remarks>
    /// A pending plugin may connect, say hello, and wait. It is the only defensible default for a
    /// program that has just turned up asking to control the cooling.
    /// </remarks>
    Pending = 0,

    /// <summary>Approved by the user, with whatever capabilities and fans they granted.</summary>
    Approved,

    /// <summary>Turned away. <see cref="PluginAdmission.Refusal"/> says why.</summary>
    Refused,
}

/// <summary>Why a connection was turned away.</summary>
public enum PluginRefusal
{
    /// <summary>Not refused.</summary>
    None = 0,

    /// <summary>The manifest id is not a well-formed plugin id, or is one Impeller reserves.</summary>
    InvalidId,

    /// <summary>Built against a protocol this engine no longer speaks.</summary>
    ProtocolTooOld,

    /// <summary>
    /// Built against a protocol newer than this engine.
    /// </summary>
    /// <remarks>
    /// Refused rather than tolerated. A newer plugin guessing at an older engine's behaviour is how
    /// a fan ends up being driven by two different sets of assumptions.
    /// </remarks>
    ProtocolTooNew,

    /// <summary>
    /// Another live session is already using this manifest id and is answering.
    /// </summary>
    /// <remarks>
    /// One session per id is what keeps a claimant id an honest identity: two connections sharing
    /// one could release each other's controls and free each other's fans by dying.
    /// </remarks>
    AlreadyConnected,

    /// <summary>The user disabled this plugin. It stays refused until they enable it again.</summary>
    Disabled,

    /// <summary>The engine is starting, stopping, or otherwise not taking plugins right now.</summary>
    EngineUnavailable,
}

/// <summary>
/// The engine's answer to a handshake, and to any later change in what a plugin may do.
/// </summary>
/// <param name="State">Whether the plugin is pending, approved, or refused.</param>
/// <param name="Granted">The capabilities actually granted. Never more than were requested.</param>
/// <param name="Controls">
/// The specific controls this plugin may claim. Empty unless <see cref="PluginCapability.ControlFans"/>
/// was granted, and never a wildcard.
/// </param>
/// <param name="Refusal">Why, when <see cref="State"/> is <see cref="PluginAdmissionState.Refused"/>.</param>
/// <param name="Message">A sentence for the plugin's own log or UI. Always populated.</param>
/// <param name="EngineVersion">Which engine answered, for the plugin's diagnostics.</param>
/// <param name="ProtocolVersion">The protocol version the engine is speaking.</param>
/// <remarks>
/// Pushed again, unsolicited, whenever the user changes anything — approving, revoking a fan,
/// disabling the plugin. A plugin that only reads this at the handshake will believe it still holds
/// permissions it lost ten minutes ago.
/// </remarks>
public sealed record PluginAdmission(
    PluginAdmissionState State,
    IReadOnlyList<PluginCapability> Granted,
    IReadOnlyList<SensorRef> Controls,
    PluginRefusal Refusal,
    string Message,
    string EngineVersion,
    int ProtocolVersion)
{
    /// <summary>Whether the plugin may make calls beyond the handshake.</summary>
    public bool IsAdmitted => State != PluginAdmissionState.Refused;

    /// <summary>Whether a capability was granted, as opposed to merely requested.</summary>
    public bool Has(PluginCapability capability) => Granted.Contains(capability);

    /// <summary>Whether this specific control was granted.</summary>
    public bool MayControl(SensorRef control) =>
        Has(PluginCapability.ControlFans) && Controls.Contains(control);

    /// <summary>A refusal, with the reason and a sentence explaining it.</summary>
    public static PluginAdmission Refused(PluginRefusal refusal, string message, string engineVersion) =>
        new(PluginAdmissionState.Refused, [], [], refusal, message, engineVersion, PluginProtocol.CurrentVersion);
}
