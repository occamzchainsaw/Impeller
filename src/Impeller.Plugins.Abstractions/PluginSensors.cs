namespace Impeller.Plugins.Abstractions;

/// <summary>
/// What a sensor measures, and therefore what its number means.
/// </summary>
/// <remarks>
/// Mirrors the engine's own sensor kinds by name. The host translates with an explicit switch
/// rather than a cast, so adding a kind on one side produces a compiler error rather than a value
/// that silently arrives as something else.
/// </remarks>
public enum PluginSensorKind
{
    /// <summary>The provider could not say.</summary>
    Unknown = 0,

    /// <summary>Degrees Celsius.</summary>
    Temperature,

    /// <summary>Revolutions per minute, from a fan's tachometer wire. Read-only.</summary>
    FanSpeed,

    /// <summary>A writable output level, as a percentage.</summary>
    Control,

    /// <summary>Utilisation, as a percentage.</summary>
    Load,

    /// <summary>Watts.</summary>
    Power,

    /// <summary>Volts.</summary>
    Voltage,

    /// <summary>Amperes.</summary>
    Current,

    /// <summary>Megahertz.</summary>
    Clock,

    /// <summary>Litres per hour.</summary>
    Flow,

    /// <summary>Fill level, as a percentage.</summary>
    Level,

    /// <summary>Gigabytes.</summary>
    Data,

    /// <summary>A dimensionless ratio.</summary>
    Factor,
}

/// <summary>Who is driving a control right now.</summary>
/// <remarks>
/// A plugin needs this to tell "I am not driving that fan because I never claimed it" apart from
/// "I am not driving that fan because the engine took it away from me", which look identical from
/// the outside and call for completely different behaviour.
/// </remarks>
public enum ControlHolder
{
    /// <summary>Its assigned curve. The resting state, and where a released fan returns to.</summary>
    Curve = 0,

    /// <summary>The user, through the Impeller window.</summary>
    User,

    /// <summary>A plugin. Possibly this one — compare <c>HolderId</c> against the manifest id.</summary>
    Plugin,

    /// <summary>The engine's failsafe. Nothing is granted while this holds.</summary>
    Failsafe,
}

/// <summary>A sensor as a plugin sees it.</summary>
/// <param name="Id">Its stable reference, and the only thing worth saving.</param>
/// <param name="Name">What the hardware calls it. Not unique, not identity, free to change.</param>
/// <param name="HardwareName">
/// What it belongs to, for example <c>Nuvoton NCT6687D</c>, or empty when unknown. Separate from
/// the name so a plugin can show either, or both, without having to take a string apart.
/// </param>
/// <param name="Kind">What it measures.</param>
/// <param name="Provider">Which backend produced it.</param>
/// <param name="HardwarePath">
/// A legible rendering of where it came from, for showing a human which sensor is which. Never
/// round-trip this or match on it; it is diagnostic text, and it changes.
/// </param>
/// <param name="Value">Its latest reading, or null when it is not reporting.</param>
public sealed record SensorInfo(
    SensorRef Id,
    string Name,
    string HardwareName,
    PluginSensorKind Kind,
    string Provider,
    string HardwarePath,
    float? Value);

/// <summary>A fan control as a plugin sees it.</summary>
/// <param name="Id">Its stable reference.</param>
/// <param name="Name">What the hardware calls it.</param>
/// <param name="HardwareName">What it belongs to, or empty when unknown.</param>
/// <param name="Provider">Which backend produced it.</param>
/// <param name="HardwarePath">Diagnostic text. Not identity.</param>
/// <param name="Tachometer">
/// The sensor reporting this fan's actual speed, or <see cref="SensorRef.None"/> when the engine
/// does not know of one.
/// </param>
/// <param name="RequestedDuty">
/// What its current owner asked for, or null when nobody has asked for anything.
/// </param>
/// <param name="CommandedDuty">What was actually written to the hardware.</param>
/// <param name="Holder">Who is driving it.</param>
/// <param name="HolderId">Which specific claimant, where that means anything.</param>
/// <param name="Granted">Whether this plugin has been granted the right to claim it.</param>
/// <param name="Driven">
/// Whether the engine is actually driving this control — it is enabled in the current
/// configuration and has a curve to fall back to. A control that is not driven cannot be claimed:
/// see <see cref="PluginAcquireFailure.NotDriven"/>.
/// </param>
/// <remarks>
/// <para>
/// <see cref="Tachometer"/> is here because the engine already knows the answer and a plugin
/// cannot work it out. Impeller pairs a fan header with its tachometer during calibration, using
/// measurements; a plugin can only guess from names, and the app this contract was designed
/// against was doing exactly that - taking the control's identifier and replacing "control" with
/// "fan". That works on one Super I/O chip and nowhere else.
/// </para>
/// <para>
/// Subscribe to it like any other sensor. It requires <see cref="PluginCapability.ReadSensors"/> to
/// read, but it is reported either way, so a settings window can say "you will get a speed readout
/// once you allow reading" rather than showing an empty box.
/// </para>
/// </remarks>
public sealed record ControlInfo(
    SensorRef Id,
    string Name,
    string HardwareName,
    string Provider,
    string HardwarePath,
    SensorRef Tachometer,
    float? RequestedDuty,
    float? CommandedDuty,
    ControlHolder Holder,
    string? HolderId,
    bool Granted,
    bool Driven);

/// <summary>Everything a plugin needs to know about the machine, on request.</summary>
/// <param name="EngineVersion">Which engine is answering.</param>
/// <param name="FailsafeEngaged">
/// Whether the engine has taken every control. Nothing can be claimed while this is true, and any
/// claim the plugin held has already been lost.
/// </param>
/// <param name="Sensors">Every readable sensor. Empty unless <see cref="PluginCapability.ReadSensors"/> was granted.</param>
/// <param name="Controls">Every fan control, whether or not this plugin was granted it.</param>
/// <remarks>
/// Controls are listed even when ungranted, on purpose: a plugin's settings UI has to be able to
/// offer the user a fan to pick before the user has granted it. Listing is not permission —
/// claiming an ungranted control returns <see cref="PluginAcquireFailure.NotPermitted"/>.
/// </remarks>
public sealed record PluginSnapshot(
    string EngineVersion,
    bool FailsafeEngaged,
    IReadOnlyList<SensorInfo> Sensors,
    IReadOnlyList<ControlInfo> Controls);

/// <summary>One sensor's current value.</summary>
/// <param name="Id">Which sensor.</param>
/// <param name="Value">
/// Its reading, or null when it is not reporting. Null is not zero: a missing temperature read as
/// zero would idle a fan that should be ramping.
/// </param>
public readonly record struct SensorSample(SensorRef Id, float? Value);

/// <summary>One control's current state.</summary>
/// <param name="Id">Which control.</param>
/// <param name="RequestedDuty">What its owner asked for.</param>
/// <param name="CommandedDuty">What the engine actually wrote.</param>
/// <param name="Holder">Who is driving it.</param>
/// <param name="HolderId">Which specific claimant.</param>
/// <remarks>
/// <para>
/// The two duties are separate because they genuinely differ, and a UI that shows only one of them
/// will lie to its user. The engine applies the binding's minimum and maximum, its avoided speed
/// bands, its slew limiter and its start/stop behaviour to a plugin's request exactly as it does to
/// a curve's — so a plugin asking for 30% may be commanded to 45% because of a configured minimum,
/// or may take several seconds to ramp there.
/// </para>
/// </remarks>
public readonly record struct ControlSample(
    SensorRef Id,
    float? RequestedDuty,
    float? CommandedDuty,
    ControlHolder Holder,
    string? HolderId);

/// <summary>One tick's worth of readings, pushed to a subscribed plugin.</summary>
/// <param name="Tick">The engine's tick number, so a plugin can notice gaps.</param>
/// <param name="Taken">When the tick ran.</param>
/// <param name="Sensors">Readings for the sensors this plugin subscribed to, and only those.</param>
/// <param name="Controls">
/// State for every control this plugin was granted, subscribed or not — losing track of a fan you
/// are driving is not something a plugin should have to remember to subscribe to.
/// </param>
public sealed record PluginReadings(
    long Tick,
    DateTimeOffset Taken,
    IReadOnlyList<SensorSample> Sensors,
    IReadOnlyList<ControlSample> Controls);
