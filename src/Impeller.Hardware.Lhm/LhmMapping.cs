using Impeller.Core.Abstractions;
using LibreHardwareMonitor.Hardware;

namespace Impeller.Hardware.Lhm;

/// <summary>
/// Translation between LibreHardwareMonitor's vocabulary and Impeller's.
/// </summary>
internal static class LhmMapping
{
    /// <summary>
    /// Maps a LibreHardwareMonitor sensor type onto the kinds the engine understands.
    /// </summary>
    /// <remarks>
    /// Types with no Impeller equivalent map to <see cref="SensorKind.Unknown"/> rather than being
    /// dropped. A sensor the UI cannot label is still worth listing — it is evidence the hardware
    /// was seen, which matters when someone is working out why a fan is missing.
    /// </remarks>
    public static SensorKind ToSensorKind(SensorType type) => type switch
    {
        SensorType.Temperature => SensorKind.Temperature,
        SensorType.Fan => SensorKind.FanSpeed,
        SensorType.Control => SensorKind.Control,
        SensorType.Load => SensorKind.Load,
        SensorType.Power => SensorKind.Power,
        SensorType.Voltage => SensorKind.Voltage,
        SensorType.Current => SensorKind.Current,
        SensorType.Clock => SensorKind.Clock,
        SensorType.Flow => SensorKind.Flow,
        SensorType.Level => SensorKind.Level,
        SensorType.Data or SensorType.SmallData => SensorKind.Data,
        SensorType.Factor => SensorKind.Factor,
        _ => SensorKind.Unknown,
    };

    /// <summary>
    /// The group a piece of hardware belongs to, used for per-group enable toggles and for
    /// reporting which groups failed to initialise.
    /// </summary>
    public static HardwareGroup ToGroup(HardwareType type) => type switch
    {
        HardwareType.Motherboard or HardwareType.SuperIO => HardwareGroup.Motherboard,
        HardwareType.Cpu => HardwareGroup.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => HardwareGroup.Gpu,
        HardwareType.Memory => HardwareGroup.Memory,
        HardwareType.Storage => HardwareGroup.Storage,
        HardwareType.Network => HardwareGroup.Network,
        HardwareType.Cooler => HardwareGroup.Cooler,
        HardwareType.EmbeddedController => HardwareGroup.EmbeddedController,
        HardwareType.Psu => HardwareGroup.Psu,
        HardwareType.Battery => HardwareGroup.Battery,
        _ => HardwareGroup.Other,
    };

    /// <summary>
    /// Derives the hardware component of a fingerprint.
    /// </summary>
    /// <remarks>
    /// LibreHardwareMonitor's <see cref="Identifier"/> is the most stable handle available — it
    /// encodes bus position rather than enumeration order, so it survives a reboot. It is not
    /// perfect: a library update that renames a chip driver changes it, which is exactly why the
    /// engine stores synthetic ids and treats this only as the key used to recognise hardware.
    /// </remarks>
    public static string HardwareKey(IHardware hardware) =>
        hardware.Identifier.ToString() ?? hardware.Name;
}

/// <summary>Coarse hardware categories, matching the per-group toggles in settings.</summary>
public enum HardwareGroup
{
    /// <summary>Anything uncategorised.</summary>
    Other = 0,

    /// <summary>Motherboard and its Super I/O chips — where fan headers live.</summary>
    Motherboard,

    /// <summary>Processor package and core sensors.</summary>
    Cpu,

    /// <summary>Discrete and integrated graphics.</summary>
    Gpu,

    /// <summary>System memory.</summary>
    Memory,

    /// <summary>Drives, read over SMART and therefore slow to poll.</summary>
    Storage,

    /// <summary>Network adapters.</summary>
    Network,

    /// <summary>AIO pumps and external fan controllers.</summary>
    Cooler,

    /// <summary>Embedded controllers, common on laptops.</summary>
    EmbeddedController,

    /// <summary>Digital power supplies.</summary>
    Psu,

    /// <summary>Battery, on portable machines.</summary>
    Battery,
}
