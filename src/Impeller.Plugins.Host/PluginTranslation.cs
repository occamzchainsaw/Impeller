using Impeller.Core.Abstractions;
using Impeller.Plugins.Abstractions;

namespace Impeller.Plugins.Host;

/// <summary>
/// Converts between the engine's vocabulary and the one plugins speak.
/// </summary>
/// <remarks>
/// <para>
/// This file is the entire price of <c>Impeller.Plugins.Abstractions</c> referencing nothing. What
/// it buys is that a third-party plugin never takes a dependency on the configuration model — no
/// curve definitions, no calibration tables, no provider interfaces — for the privilege of asking
/// the engine to spin a fan.
/// </para>
/// <para>
/// Every conversion is an explicit switch with no default arm, so adding a value on either side is
/// a compiler error here rather than a value that silently arrives as something else. That is the
/// whole reason not to cast: the enums line up today, and the first time they do not, a cast turns
/// a new sensor kind into a temperature.
/// </para>
/// </remarks>
public static class PluginTranslation
{
    /// <summary>An engine id as a plugin sees it. Wire-identical: both are a bare GUID.</summary>
    public static SensorRef ToRef(SensorId id) => new(id.Value);

    /// <summary>A plugin's reference as the engine sees it.</summary>
    public static SensorId ToSensorId(SensorRef reference) => new(reference.Value);

    /// <summary>What a sensor measures, as a plugin sees it.</summary>
    public static PluginSensorKind ToPluginKind(SensorKind kind) => kind switch
    {
        SensorKind.Unknown => PluginSensorKind.Unknown,
        SensorKind.Temperature => PluginSensorKind.Temperature,
        SensorKind.FanSpeed => PluginSensorKind.FanSpeed,
        SensorKind.Control => PluginSensorKind.Control,
        SensorKind.Load => PluginSensorKind.Load,
        SensorKind.Power => PluginSensorKind.Power,
        SensorKind.Voltage => PluginSensorKind.Voltage,
        SensorKind.Current => PluginSensorKind.Current,
        SensorKind.Clock => PluginSensorKind.Clock,
        SensorKind.Flow => PluginSensorKind.Flow,
        SensorKind.Level => PluginSensorKind.Level,
        SensorKind.Data => PluginSensorKind.Data,
        SensorKind.Factor => PluginSensorKind.Factor,
        _ => PluginSensorKind.Unknown,
    };

    /// <summary>What a plugin says a sensor measures, as the engine sees it.</summary>
    public static SensorKind ToSensorKind(PluginSensorKind kind) => kind switch
    {
        PluginSensorKind.Unknown => SensorKind.Unknown,
        PluginSensorKind.Temperature => SensorKind.Temperature,
        PluginSensorKind.FanSpeed => SensorKind.FanSpeed,
        PluginSensorKind.Control => SensorKind.Control,
        PluginSensorKind.Load => SensorKind.Load,
        PluginSensorKind.Power => SensorKind.Power,
        PluginSensorKind.Voltage => SensorKind.Voltage,
        PluginSensorKind.Current => SensorKind.Current,
        PluginSensorKind.Clock => SensorKind.Clock,
        PluginSensorKind.Flow => SensorKind.Flow,
        PluginSensorKind.Level => SensorKind.Level,
        PluginSensorKind.Data => SensorKind.Data,
        PluginSensorKind.Factor => SensorKind.Factor,
        _ => SensorKind.Unknown,
    };

    /// <summary>Who holds a control, as a plugin sees it.</summary>
    public static ControlHolder ToHolder(ControlOwnerKind kind) => kind switch
    {
        ControlOwnerKind.Curve => ControlHolder.Curve,
        ControlOwnerKind.ManualOverride => ControlHolder.User,
        ControlOwnerKind.Plugin => ControlHolder.Plugin,
        ControlOwnerKind.Failsafe => ControlHolder.Failsafe,
        _ => ControlHolder.Curve,
    };

    /// <summary>Why a claim was refused, as a plugin sees it.</summary>
    public static PluginAcquireFailure ToFailure(ControlAcquireFailure failure) => failure switch
    {
        ControlAcquireFailure.UnknownControl => PluginAcquireFailure.UnknownControl,
        ControlAcquireFailure.NotPermitted => PluginAcquireFailure.NotPermitted,
        ControlAcquireFailure.AlreadyOwned => PluginAcquireFailure.AlreadyOwned,
        ControlAcquireFailure.EngineUnavailable => PluginAcquireFailure.EngineUnavailable,
        ControlAcquireFailure.NotDriven => PluginAcquireFailure.NotDriven,
        _ => PluginAcquireFailure.EngineUnavailable,
    };

    /// <summary>
    /// Why a control changed hands, as the reason a plugin is given for losing it.
    /// </summary>
    /// <remarks>
    /// <see cref="OwnershipChangeReason.Claimed"/> maps to
    /// <see cref="ControlLostReason.TakenByUser"/> because it is the only way a plugin sees one: an
    /// ordinary plugin claim cannot displace an existing owner, so a claim that took a control away
    /// from this plugin was somebody outranking it.
    /// </remarks>
    public static ControlLostReason ToLostReason(OwnershipChangeReason reason) => reason switch
    {
        OwnershipChangeReason.Claimed => ControlLostReason.TakenByUser,
        OwnershipChangeReason.Released => ControlLostReason.Released,
        OwnershipChangeReason.Failsafe => ControlLostReason.Failsafe,
        OwnershipChangeReason.TakenByUser => ControlLostReason.TakenByUser,
        OwnershipChangeReason.ConfigurationChanged => ControlLostReason.ConfigurationChanged,
        OwnershipChangeReason.LeaseExpired => ControlLostReason.LeaseExpired,
        OwnershipChangeReason.Revoked => ControlLostReason.Revoked,
        OwnershipChangeReason.PluginDisabled => ControlLostReason.PluginDisabled,
        OwnershipChangeReason.Unhealthy => ControlLostReason.PluginDisabled,
        _ => ControlLostReason.Revoked,
    };

    /// <summary>
    /// A sensor as a plugin sees it.
    /// </summary>
    /// <param name="sensor">The engine's sensor.</param>
    /// <param name="displayName">
    /// What to call it: the user's own name where they gave one, otherwise the provider's. Passed
    /// in rather than read here, so there is exactly one place that decides what a thing is called
    /// and every surface - window, plugin, diagnostic report - agrees.
    /// </param>
    public static SensorInfo Describe(ISensor sensor, string displayName)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        return new SensorInfo(
            ToRef(sensor.Id),
            displayName,
            sensor.HardwareName,
            ToPluginKind(sensor.Kind),
            sensor.Fingerprint.ProviderId,
            sensor.Fingerprint.ToString(),
            sensor.Value);
    }
}
