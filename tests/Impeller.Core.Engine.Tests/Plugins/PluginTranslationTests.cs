using Impeller.Core.Abstractions;
using Impeller.Plugins.Abstractions;
using Impeller.Plugins.Host;

namespace Impeller.Core.Engine.Tests.Plugins;

/// <summary>
/// Covers the layer that converts between the engine's vocabulary and the plugins'.
/// </summary>
/// <remarks>
/// This is the entire price of the plugin contracts referencing nothing, and the risk it carries is
/// specific: the two sets of enums line up today, and the day one of them gains a value, a
/// translation with a silent fallback turns a new sensor kind into a temperature. These tests walk
/// every value on both sides so that adding one to either fails here rather than in someone's
/// readings.
/// </remarks>
public class PluginTranslationTests
{
    [Fact]
    public void An_id_survives_the_trip_into_a_plugin_and_back()
    {
        var id = SensorId.New();

        Assert.Equal(id, PluginTranslation.ToSensorId(PluginTranslation.ToRef(id)));
    }

    [Fact]
    public void An_id_that_refers_to_nothing_still_refers_to_nothing()
    {
        Assert.True(PluginTranslation.ToRef(SensorId.None).IsNone);
        Assert.True(PluginTranslation.ToSensorId(SensorRef.None).IsNone);
    }

    [Fact]
    public void Every_sensor_kind_has_a_plugin_kind_of_its_own()
    {
        var mapped = Enum.GetValues<SensorKind>()
            .ToDictionary(kind => kind, PluginTranslation.ToPluginKind);

        // No two engine kinds may collapse onto one plugin kind: a fan speed arriving as a
        // temperature is a curve built on the wrong number.
        Assert.Equal(mapped.Count, mapped.Values.Distinct().Count());

        foreach (var (kind, plugin) in mapped)
        {
            Assert.Equal(kind.ToString(), plugin.ToString());
            Assert.Equal(kind, PluginTranslation.ToSensorKind(plugin));
        }
    }

    [Fact]
    public void Every_plugin_kind_comes_back_as_the_engine_kind_it_came_from()
    {
        foreach (var kind in Enum.GetValues<PluginSensorKind>())
        {
            Assert.Equal(kind, PluginTranslation.ToPluginKind(PluginTranslation.ToSensorKind(kind)));
        }
    }

    [Fact]
    public void Every_owner_maps_to_a_holder_of_its_own()
    {
        var mapped = Enum.GetValues<ControlOwnerKind>()
            .ToDictionary(kind => kind, PluginTranslation.ToHolder);

        Assert.Equal(mapped.Count, mapped.Values.Distinct().Count());
        Assert.Equal(ControlHolder.User, mapped[ControlOwnerKind.ManualOverride]);
        Assert.Equal(ControlHolder.Failsafe, mapped[ControlOwnerKind.Failsafe]);
    }

    [Fact]
    public void Every_refusal_maps_to_a_refusal_of_its_own()
    {
        // A NotDriven that arrived as EngineUnavailable would send the user looking for a failsafe
        // that is not happening, instead of at the fan they switched off.
        var mapped = Enum.GetValues<ControlAcquireFailure>()
            .ToDictionary(failure => failure, PluginTranslation.ToFailure);

        Assert.Equal(mapped.Count, mapped.Values.Distinct().Count());
        Assert.Equal(PluginAcquireFailure.NotDriven, mapped[ControlAcquireFailure.NotDriven]);
        Assert.Equal(PluginAcquireFailure.NotPermitted, mapped[ControlAcquireFailure.NotPermitted]);
    }

    [Fact]
    public void Every_ownership_change_gives_a_plugin_a_reason_it_can_act_on()
    {
        foreach (var reason in Enum.GetValues<OwnershipChangeReason>())
        {
            var lost = PluginTranslation.ToLostReason(reason);

            // Never silently Revoked. That reason tells a plugin to stop asking, and telling it that
            // when the truth was a failsafe or a configuration change is worse than saying nothing.
            Assert.True(
                reason == OwnershipChangeReason.Revoked || lost != ControlLostReason.Revoked,
                $"{reason} fell through to Revoked.");
        }

        Assert.Equal(
            ControlLostReason.Failsafe,
            PluginTranslation.ToLostReason(OwnershipChangeReason.Failsafe));

        Assert.Equal(
            ControlLostReason.ConfigurationChanged,
            PluginTranslation.ToLostReason(OwnershipChangeReason.ConfigurationChanged));

        Assert.Equal(
            ControlLostReason.LeaseExpired,
            PluginTranslation.ToLostReason(OwnershipChangeReason.LeaseExpired));
    }

    [Fact]
    public void A_sensor_arrives_with_its_identity_its_kind_and_its_value()
    {
        var sensor = new FakeSensor(SensorKind.Temperature, "CPU Package") { Value = 61.5f };

        var info = PluginTranslation.Describe(sensor);

        Assert.Equal(PluginTranslation.ToRef(sensor.Id), info.Id);
        Assert.Equal("CPU Package", info.Name);
        Assert.Equal(PluginSensorKind.Temperature, info.Kind);
        Assert.Equal("fake", info.Provider);
        Assert.Equal(61.5f, info.Value!.Value, precision: 3);
    }

    [Fact]
    public void A_sensor_that_is_not_reporting_arrives_as_no_value_rather_than_zero()
    {
        var info = PluginTranslation.Describe(new FakeSensor(SensorKind.FanSpeed, "Fan #7"));

        Assert.Null(info.Value);
    }
}
