using Impeller.Core.Abstractions;
using Impeller.Plugins.Abstractions;

namespace Impeller.Plugins.Host;

/// <summary>A sensor a plugin declared, holding whatever value it last pushed.</summary>
public class PluginProvidedSensor(
    SensorId id,
    string name,
    string hardwareName,
    SensorKind kind,
    HardwareFingerprint fingerprint) : ISensor
{
    private volatile Box _value = new(null);

    /// <inheritdoc />
    public SensorId Id { get; } = id;

    /// <inheritdoc />
    public string Name { get; } = name;

    /// <inheritdoc />
    public string HardwareName { get; } = hardwareName;

    /// <inheritdoc />
    public SensorKind Kind { get; } = kind;

    /// <inheritdoc />
    public float? Value => _value.Value;

    /// <inheritdoc />
    public HardwareFingerprint Fingerprint { get; } = fingerprint;

    /// <summary>Records a value the plugin pushed, or null when it currently has none to report.</summary>
    public void SetValue(float? value) => _value = new Box(value);

    // A reference-type box rather than a lock, so a concurrent read never tears — the sensor is
    // read from the tick thread while PushReadingsAsync writes it from the plugin's connection.
    private sealed record Box(float? Value);
}

/// <summary>A control a plugin declared: a fan it, and only it, can actually drive.</summary>
/// <remarks>
/// Writing is a queued notification to the owning session's plugin, never a blocking call — the
/// engine must never wait on a plugin from the tick loop, the same rule every other route to a
/// plugin follows. See <see cref="IPluginClient.OnWriteRequestedAsync"/>.
/// </remarks>
public sealed class PluginProvidedControl(
    SensorId id,
    string name,
    string hardwareName,
    HardwareFingerprint fingerprint,
    PluginSession session,
    SensorRef reference,
    bool supportsAutomaticMode)
    : PluginProvidedSensor(id, name, hardwareName, SensorKind.Control, fingerprint), IControl
{
    private volatile bool _hasCommanded;
    private float _commandedPercent;

    /// <inheritdoc />
    public Duty? CommandedDuty => _hasCommanded ? new Duty(_commandedPercent) : null;

    /// <inheritdoc />
    public bool SupportsAutomaticMode => supportsAutomaticMode;

    /// <inheritdoc />
    public void Write(Duty duty)
    {
        _commandedPercent = duty.Percent;
        _hasCommanded = true;

        session.Post(client => client.OnWriteRequestedAsync(reference, duty.Percent));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always refused. The wire protocol has no call for it — <c>OnWriteRequestedAsync</c> is the
    /// only thing the engine can ask a plugin's hardware to do — so the engine's ordinary failsafe
    /// (writing a duty) is what protects this control, exactly as it protects hardware with no
    /// automatic mode at all.
    /// </remarks>
    public bool TryRestoreAutomaticMode() => false;
}
