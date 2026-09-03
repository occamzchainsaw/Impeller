namespace Impeller.Plugins.Abstractions;

/// <summary>
/// A sensor a plugin offers to contribute.
/// </summary>
/// <param name="Key">
/// A key unique within the declaring plugin and stable across its restarts. The engine combines it
/// with the plugin's id to mint a <see cref="SensorRef"/>; a key that changes between runs is a
/// sensor the user's curves stop finding.
/// </param>
/// <param name="Name">What to call it.</param>
/// <param name="Kind">What it measures.</param>
public sealed record DeclaredSensor(string Key, string Name, PluginSensorKind Kind);

/// <summary>
/// A control a plugin offers to contribute.
/// </summary>
/// <param name="Key">Unique within the plugin, stable across restarts.</param>
/// <param name="Name">What to call it.</param>
/// <param name="SupportsAutomaticMode">Whether the hardware can be handed back to its own firmware.</param>
/// <param name="FailsafeDuty">
/// Where the plugin will drive this control itself if it loses contact with the engine.
/// </param>
/// <remarks>
/// <para>
/// <paramref name="FailsafeDuty"/> is the load-bearing field, and the reason this contract is
/// settled now rather than when the feature is built. The engine cannot failsafe a control it does
/// not own the hardware for: writing to it means sending a message to the very process that may be
/// the reason the engine is failsafing. If that process is dead or wedged, the fan is stuck and
/// nothing in the engine can move it.
/// </para>
/// <para>
/// So the failsafe for contributed hardware lives in the plugin. The SDK holds this duty and drives
/// the control to it when the connection drops, without asking the plugin's own code — which is why
/// it is declared up front rather than negotiated later.
/// </para>
/// </remarks>
public sealed record DeclaredControl(
    string Key,
    string Name,
    bool SupportsAutomaticMode,
    float FailsafeDuty);

/// <summary>
/// A device a plugin contributes, with everything on it.
/// </summary>
/// <param name="HardwareKey">
/// Identifies the device within the plugin. Together with the plugin's id it is the fingerprint the
/// identity map records, so it must be the same string every run for the same physical thing.
/// </param>
/// <param name="DisplayName">What to call the device.</param>
/// <param name="Sensors">Its readable sensors.</param>
/// <param name="Controls">Its writable controls.</param>
/// <remarks>
/// Declared once, at the handshake, and never incrementally. The engine's provider contract states
/// that a provider's sensor set is fixed between initialisations, and a plugin that could add
/// hardware mid-session would break that for every consumer of the sensor registry — including the
/// curve editor the user has open.
/// </remarks>
public sealed record HardwareDeclaration(
    string HardwareKey,
    string DisplayName,
    IReadOnlyList<DeclaredSensor> Sensors,
    IReadOnlyList<DeclaredControl> Controls);

/// <summary>What became of a hardware declaration.</summary>
public enum ProviderOutcome
{
    /// <summary>The hardware was taken on.</summary>
    Accepted = 0,

    /// <summary>The manifest did not request <see cref="PluginCapability.ProvideHardware"/>.</summary>
    NotRequested,

    /// <summary>It was requested but the user has not granted it.</summary>
    NotPermitted,

    /// <summary>
    /// This build speaks the contract but does not implement the engine side of it.
    /// </summary>
    /// <remarks>
    /// A distinct value rather than a refusal, deliberately. An author who gets "refused" goes
    /// looking for a permission they need to ask the user for, and there is no such permission to
    /// find — they would be debugging a missing feature as if it were a policy problem. This says
    /// plainly: the shape is right, come back for a later build.
    /// </remarks>
    NotImplementedInThisBuild,

    /// <summary>Something in the declaration was malformed. The message says what.</summary>
    InvalidDeclaration,
}

/// <summary>The engine's answer to a hardware declaration.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ProviderId">
/// The identity the engine minted for this plugin's hardware, when it was accepted.
/// </param>
/// <param name="Sensors">The references assigned to the declared sensors, keyed by their declared key.</param>
/// <param name="Controls">The references assigned to the declared controls, keyed by their declared key.</param>
/// <param name="Message">A sentence explaining the outcome.</param>
/// <remarks>
/// The provider id is minted by the engine from the manifest id and is not the plugin's to choose.
/// Letting a plugin name its own provider would let it claim <c>lhm</c> and have its sensors
/// resolve against identities the real hardware minted.
/// </remarks>
public sealed record HardwareAdmission(
    ProviderOutcome Outcome,
    string? ProviderId,
    IReadOnlyDictionary<string, SensorRef> Sensors,
    IReadOnlyDictionary<string, SensorRef> Controls,
    string Message)
{
    /// <summary>Whether the hardware was taken on.</summary>
    public bool Accepted => Outcome == ProviderOutcome.Accepted;

    /// <summary>The answer this build gives to every declaration.</summary>
    public static HardwareAdmission NotImplemented() => new(
        ProviderOutcome.NotImplementedInThisBuild,
        null,
        new Dictionary<string, SensorRef>(),
        new Dictionary<string, SensorRef>(),
        "This engine speaks the hardware-provider contract but does not implement it yet. "
        + "Your declaration is well-formed; nothing is wrong with your plugin or its permissions.");
}

/// <summary>A value for a sensor the plugin contributed.</summary>
/// <param name="Key">The declared key, not the minted reference — a plugin knows its own keys.</param>
/// <param name="Value">The reading, or null when the plugin cannot currently read it.</param>
public readonly record struct ProvidedReading(string Key, float? Value);
