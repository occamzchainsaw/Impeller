using System.Text.Json.Nodes;
using Impeller.Core.Persistence.Legacy;

namespace Impeller.Core.Engine.Tests.Legacy;

/// <summary>
/// Covers recovering a curve's type from an object that never says what it is.
/// </summary>
/// <remarks>
/// The pairs that differ only in how hysteresis is stored are the point of these tests. A rule that
/// looks for a distinctive property gets those wrong, and getting them wrong means reading a
/// hysteresis value as a response time — a curve that still loads and behaves subtly unlike the one
/// the user built.
/// </remarks>
public class LegacyTypeResolverTests
{
    private static JsonObject Curve(params string[] properties)
    {
        var node = new JsonObject();
        foreach (var property in properties)
        {
            node[property] = 0;
        }

        return node;
    }

    [Fact]
    public void A_flat_curve_is_recognised_by_its_percentage()
    {
        Assert.Equal(
            LegacyCurveShape.Flat,
            LegacyTypeResolver.ResolveCurve(Curve("Name", "IsHidden", "CommandMode", "Percent")));
    }

    [Fact]
    public void An_auto_curve_is_recognised_by_its_duty_range()
    {
        var curve = Curve(
            "Name", "IsHidden", "CommandMode", "SelectedTempSource", "Step", "Deadband",
            "SelectedResponseTime", "IdleTemperature", "MinFanSpeed", "MaxFanSpeed", "LoadTemperature");

        Assert.Equal(LegacyCurveShape.Auto, LegacyTypeResolver.ResolveCurve(curve));
    }

    [Fact]
    public void The_older_auto_shape_names_its_range_after_idle_and_load()
    {
        var curve = Curve(
            "Name", "SelectedTempSource", "Step", "Deadband", "SelectedResponseTime",
            "IdleTemperature", "IdleFanSpeed", "LoadFanSpeed", "LoadTemperature");

        Assert.Equal(LegacyCurveShape.AutoLegacy, LegacyTypeResolver.ResolveCurve(curve));
    }

    [Fact]
    public void A_linear_curve_is_told_from_its_older_self_by_the_shape_of_its_hysteresis()
    {
        var shared = new[]
        {
            "Name", "IsHidden", "CommandMode", "SelectedTempSource",
            "MaximumFanSpeed", "MaximumTemperature", "MinimumFanSpeed", "MinimumTemperature",
        };

        // An object under one name, or loose scalars under others. Nothing else separates them.
        Assert.Equal(
            LegacyCurveShape.Linear,
            LegacyTypeResolver.ResolveCurve(Curve([.. shared, "HysteresisConfig"])));

        Assert.Equal(
            LegacyCurveShape.LinearLegacy,
            LegacyTypeResolver.ResolveCurve(Curve(
                [.. shared, "SelectedHysteresis", "SelectedResponseTime", "OneWayHysteresis", "IgnoreHysteresisAtLimits"])));
    }

    [Fact]
    public void A_graph_curve_is_told_from_its_older_self_the_same_way()
    {
        var shared = new[]
        {
            "Name", "IsHidden", "CommandMode", "SelectedTempSource",
            "Points", "MaximumTemperature", "MinimumTemperature", "MaximumCommand",
        };

        Assert.Equal(
            LegacyCurveShape.Graph,
            LegacyTypeResolver.ResolveCurve(Curve([.. shared, "HysteresisConfig"])));

        Assert.Equal(
            LegacyCurveShape.GraphLegacy,
            LegacyTypeResolver.ResolveCurve(Curve(
                [.. shared, "SelectedHysteresis", "SelectedResponseTime", "OneWayHysteresis", "IgnoreHysteresisAtLimits"])));
    }

    [Fact]
    public void A_trigger_curve_is_told_from_its_older_self_by_its_response_times()
    {
        var shared = new[]
        {
            "Name", "IsHidden", "CommandMode", "SelectedTempSource",
            "LoadFanSpeed", "LoadTemperature", "IdleFanSpeed", "IdleTemperature",
        };

        Assert.Equal(
            LegacyCurveShape.Trigger,
            LegacyTypeResolver.ResolveCurve(Curve([.. shared, "ResponseTimeConfig"])));

        Assert.Equal(
            LegacyCurveShape.TriggerLegacy,
            LegacyTypeResolver.ResolveCurve(Curve([.. shared, "SelectedResponseTime"])));
    }

    [Fact]
    public void A_mix_curve_is_told_from_its_older_self_by_holding_a_list_rather_than_a_pair()
    {
        Assert.Equal(
            LegacyCurveShape.Mix,
            LegacyTypeResolver.ResolveCurve(Curve(
                "Name", "IsHidden", "CommandMode", "SelectedFanCurves", "SelectedMixFunction")));

        Assert.Equal(
            LegacyCurveShape.MixLegacy,
            LegacyTypeResolver.ResolveCurve(Curve(
                "Name", "SelectedFanCurveA", "SelectedFanCurveB", "SelectedMixFunction")));
    }

    [Fact]
    public void A_sync_curve_is_recognised_by_the_control_it_mirrors()
    {
        Assert.Equal(
            LegacyCurveShape.Sync,
            LegacyTypeResolver.ResolveCurve(Curve(
                "Name", "IsHidden", "CommandMode", "SelectedControl", "SelectedOffset", "Proportional")));
    }

    [Fact]
    public void An_object_carrying_only_a_name_is_a_reference_not_a_definition()
    {
        // This is what a control's SelectedFanCurve looks like. Scoring it as a curve would invent an
        // empty curve for every control in the file.
        Assert.Equal(LegacyCurveShape.NameOnly, LegacyTypeResolver.ResolveCurve(Curve("Name")));
    }

    [Fact]
    public void An_object_with_nothing_in_common_scores_nothing()
    {
        Assert.Equal(LegacyCurveShape.Unknown, LegacyTypeResolver.ResolveCurve(Curve("Something", "Else")));
    }

    [Fact]
    public void A_mix_sensor_outscores_the_plain_settings_it_shares_properties_with()
    {
        var sensor = Curve("NickName", "Identifier", "IsHidden", "AllowMissingSensor", "SelectedMixFunction", "SelectedSensors");

        Assert.Equal(LegacySensorShape.Mix, LegacyTypeResolver.ResolveSensor(sensor));
    }

    [Theory]
    [InlineData(LegacySensorShape.TimeAverage, "SelectedTempSource", "SelectedTime")]
    [InlineData(LegacySensorShape.File, "FileFullName", "IsHidden")]
    [InlineData(LegacySensorShape.Offset, "Offset", "Proportional")]
    public void Each_derived_sensor_shape_is_recognised(LegacySensorShape expected, string first, string second)
    {
        var sensor = Curve("NickName", "Identifier", "IsHidden", first, second);

        Assert.Equal(expected, LegacyTypeResolver.ResolveSensor(sensor));
    }

    [Fact]
    public void Settings_attached_to_a_hardware_sensor_are_not_a_derived_sensor()
    {
        Assert.Equal(
            LegacySensorShape.Plain,
            LegacyTypeResolver.ResolveSensor(Curve("NickName", "Identifier", "IsHidden")));
    }
}
