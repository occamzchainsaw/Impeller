namespace Impeller.Core.Abstractions;

/// <summary>
/// What a sensor measures. Determines its unit, how the UI formats it, and which
/// sensors are offered as valid inputs when building a curve.
/// </summary>
public enum SensorKind
{
    /// <summary>Kind could not be determined from the backing provider.</summary>
    Unknown = 0,

    /// <summary>Temperature, in degrees Celsius. The usual curve input.</summary>
    Temperature,

    /// <summary>Measured rotational speed, in RPM. Read-only; produced by a fan's tach wire.</summary>
    FanSpeed,

    /// <summary>A writable PWM/DC output level, as a percentage. Backs <see cref="IControl"/>.</summary>
    Control,

    /// <summary>Utilisation, as a percentage (CPU load, GPU load).</summary>
    Load,

    /// <summary>Power draw, in watts.</summary>
    Power,

    /// <summary>Voltage, in volts.</summary>
    Voltage,

    /// <summary>Current, in amperes.</summary>
    Current,

    /// <summary>Clock frequency, in megahertz.</summary>
    Clock,

    /// <summary>Liquid flow rate, in litres per hour. Reported by some AIO and custom-loop pumps.</summary>
    Flow,

    /// <summary>Fill or reservoir level, as a percentage.</summary>
    Level,

    /// <summary>Stored or transferred data, in gigabytes.</summary>
    Data,

    /// <summary>A dimensionless ratio or multiplier.</summary>
    Factor,
}

/// <summary>Unit helpers for <see cref="SensorKind"/>.</summary>
public static class SensorKindExtensions
{
    /// <summary>
    /// The canonical unit suffix for a kind. Display formatting lives in the UI layer;
    /// this is the raw unit the engine stores values in.
    /// </summary>
    public static string UnitSuffix(this SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "°C",
        SensorKind.FanSpeed => "RPM",
        SensorKind.Control => "%",
        SensorKind.Load => "%",
        SensorKind.Power => "W",
        SensorKind.Voltage => "V",
        SensorKind.Current => "A",
        SensorKind.Clock => "MHz",
        SensorKind.Flow => "L/h",
        SensorKind.Level => "%",
        SensorKind.Data => "GB",
        SensorKind.Factor => "",
        _ => "",
    };

    /// <summary>
    /// Whether a kind is a sensible curve input. Excludes <see cref="SensorKind.Control"/>,
    /// since driving a control from its own output invites feedback loops.
    /// </summary>
    public static bool IsCurveInput(this SensorKind kind) => kind switch
    {
        SensorKind.Temperature => true,
        SensorKind.Load => true,
        SensorKind.Power => true,
        SensorKind.FanSpeed => true,
        SensorKind.Clock => true,
        SensorKind.Flow => true,
        SensorKind.Level => true,
        SensorKind.Current => true,
        SensorKind.Voltage => true,
        _ => false,
    };
}
