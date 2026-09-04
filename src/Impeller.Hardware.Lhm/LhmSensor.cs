using Impeller.Core.Abstractions;
using LhmHw = LibreHardwareMonitor.Hardware;

namespace Impeller.Hardware.Lhm;

/// <summary>
/// Wraps one LibreHardwareMonitor sensor.
/// </summary>
/// <remarks>
/// A thin projection rather than a snapshot: <see cref="Value"/> reads through to the underlying
/// sensor, so a refresh updates every wrapper at once with no copying and no risk of the engine
/// reading a value from a previous tick.
/// </remarks>
internal class LhmSensor(SensorId id, LhmHw.ISensor sensor, HardwareFingerprint fingerprint) : ISensor
{
    /// <summary>The wrapped LibreHardwareMonitor sensor.</summary>
    protected LhmHw.ISensor Sensor { get; } = sensor;

    /// <inheritdoc />
    public SensorId Id { get; } = id;

    /// <inheritdoc />
    public string Name { get; } = sensor.Name;

    /// <inheritdoc />
    public string HardwareName { get; } = sensor.Hardware?.Name ?? string.Empty;

    /// <inheritdoc />
    public SensorKind Kind { get; } = LhmMapping.ToSensorKind(sensor.SensorType);

    /// <inheritdoc />
    public HardwareFingerprint Fingerprint { get; } = fingerprint;

    /// <inheritdoc />
    public float? Value => Sensor.Value;

    public override string ToString() => $"{Name} ({Fingerprint})";
}
