using System.Text.Json.Nodes;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Persistence.Legacy;

namespace Impeller.Core.Engine.Tests.Legacy;

/// <summary>
/// Covers the import against the real configuration it was written for.
/// </summary>
/// <remarks>
/// The fixture is a genuine file from a working machine, not a constructed one — nine controls
/// across a Super I/O chip and a graphics card, four curves, a mix sensor, and calibration tables
/// measured on real fans. Constructed fixtures agree with whatever the importer happens to do; this
/// one has already caught the things that would have been wrong.
/// </remarks>
public class FanControlConfigImporterTests
{
    private const string SuperIo = "/lpc/nct6687d/0";
    private const string Cpu = "/amdcpu/0";

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Legacy", "Fixtures", "userConfig.json");

    /// <summary>
    /// An identity map holding what the LibreHardwareMonitor backend would have registered on the
    /// machine this configuration came from.
    /// </summary>
    /// <remarks>
    /// Note the hardware key: the chip's real identifier keeps the zero index the file has stripped
    /// out of it. That mismatch is the whole reason the importer repairs the path, and seeding the
    /// map with the honest spelling is what makes this test able to notice if it stops.
    /// </remarks>
    private static InMemorySensorIdentityMap LiveHardware()
    {
        var map = new InMemorySensorIdentityMap();

        for (var channel = 0; channel < 8; channel++)
        {
            map.GetOrCreate(new HardwareFingerprint("lhm", SuperIo, channel, SensorKind.Control));
            map.GetOrCreate(new HardwareFingerprint("lhm", SuperIo, channel, SensorKind.FanSpeed));
        }

        map.GetOrCreate(new HardwareFingerprint("lhm", Cpu, 2, SensorKind.Temperature));
        return map;
    }

    private static ImportResult ImportFixture(ISensorIdentityMap map) =>
        new FanControlConfigImporter(map).ImportFile(FixturePath, "Imported");

    [Fact]
    public void Every_curve_in_the_file_arrives()
    {
        var result = ImportFixture(LiveHardware());

        Assert.Equal(4, result.Configuration.Curves.Count);
        Assert.Equal(
            ["Flat 50", "Auto CPU", "Auto GPU", "Auto GPU CPU Mix"],
            result.Configuration.Curves.Select(curve => curve.Name).ToArray());
    }

    [Fact]
    public void A_flat_curve_keeps_its_percentage()
    {
        var result = ImportFixture(LiveHardware());
        var flat = Assert.IsType<FlatCurveDefinition>(
            result.Configuration.Curves.Single(curve => curve.Name == "Flat 50"));

        // Named "Flat 50" and set to 75. The name is the user's, the number is the truth.
        Assert.Equal(75f, flat.Duty.Percent, 3);
    }

    [Fact]
    public void An_auto_curve_keeps_every_parameter_it_was_tuned_with()
    {
        var map = LiveHardware();
        var result = ImportFixture(map);
        var auto = Assert.IsType<AutoCurveDefinition>(
            result.Configuration.Curves.Single(curve => curve.Name == "Auto CPU"));

        Assert.Equal(35f, auto.IdleTemperature, 3);
        Assert.Equal(75f, auto.LoadTemperature, 3);
        Assert.Equal(30f, auto.MinimumDuty.Percent, 3);
        Assert.Equal(100f, auto.MaximumDuty.Percent, 3);
        Assert.Equal(2f, auto.Step, 3);
        Assert.Equal(3f, auto.Deadband, 3);
        Assert.Equal(TimeSpan.FromSeconds(2), auto.ResponseTime);

        map.TryGet(new HardwareFingerprint("lhm", Cpu, 2, SensorKind.Temperature), out var cpu);
        Assert.Equal(cpu, auto.Source);
    }

    [Fact]
    public void A_motherboard_fan_resolves_despite_the_index_missing_from_its_stored_path()
    {
        var map = LiveHardware();
        var result = ImportFixture(map);

        map.TryGet(new HardwareFingerprint("lhm", SuperIo, 0, SensorKind.Control), out var expected);

        var cpuFan = result.Configuration.Controls.Single(control => control.ControlId == expected);
        Assert.True(cpuFan.Enabled);

        // And the tach paired with it, which is stored the same way.
        map.TryGet(new HardwareFingerprint("lhm", SuperIo, 0, SensorKind.FanSpeed), out var tach);
        Assert.Equal(tach, cpuFan.PairedFanSensorId);
    }

    [Fact]
    public void Every_control_the_machine_still_has_arrives_with_its_limits()
    {
        var result = ImportFixture(LiveHardware());

        // Eight on the Super I/O chip. The ninth is on the graphics card and has no backend yet.
        Assert.Equal(8, result.Configuration.Controls.Count);

        var cpuFan = result.Configuration.Controls[0];
        Assert.Equal(22f, cpuFan.StartDuty.Percent, 3);
        Assert.Equal(16f, cpuFan.StopDuty.Percent, 3);
        Assert.Equal(8f, cpuFan.MaximumStepUpPerSecond, 3);
        Assert.Equal(8f, cpuFan.MaximumStepDownPerSecond, 3);
    }

    [Fact]
    public void Calibration_tables_come_across_measurement_for_measurement()
    {
        var result = ImportFixture(LiveHardware());
        var cpuFan = result.Configuration.Controls[0];

        Assert.Equal(10, cpuFan.Calibration.Count);
        Assert.Equal(10f, cpuFan.Calibration[0].Duty.Percent, 3);
        Assert.Equal(0, cpuFan.Calibration[0].Rpm);
        Assert.Equal(100f, cpuFan.Calibration[^1].Duty.Percent, 3);
        Assert.Equal(2193, cpuFan.Calibration[^1].Rpm);
        Assert.All(cpuFan.Calibration, point => Assert.False(point.Avoid));
    }

    [Fact]
    public void A_control_with_no_curve_behind_it_is_imported_switched_off()
    {
        var map = LiveHardware();
        var result = ImportFixture(map);

        // Channel 4 was enabled in the source but had no curve selected. Enabling it here would mean
        // the engine driving a fan with nothing to drive it from.
        map.TryGet(new HardwareFingerprint("lhm", SuperIo, 4, SensorKind.Control), out var expected);

        var control = result.Configuration.Controls.Single(c => c.ControlId == expected);
        Assert.False(control.Enabled);
        Assert.True(control.CurveId.IsNone);
    }

    [Fact]
    public void The_mix_sensor_arrives_with_an_identity_of_its_own()
    {
        var result = ImportFixture(LiveHardware());
        var sensor = Assert.Single(result.Configuration.CustomSensors);

        Assert.Equal("CPU GPU Mix", sensor.Name);
        Assert.Equal(CustomSensorKind.Mix, sensor.Kind);
        Assert.False(sensor.Id.IsNone);

        // The curve that read it now points at that id, not at the string "Mix/CPU GPU Mix" -- so
        // renaming the sensor no longer breaks the curve, which it would have in the source.
        var curve = Assert.IsType<AutoCurveDefinition>(
            result.Configuration.Curves.Single(c => c.Name == "Auto GPU CPU Mix"));

        Assert.Equal(sensor.Id, curve.Source);
    }

    [Fact]
    public void A_mix_sensor_reads_its_numbers_off_the_sensor_table_not_the_curve_one()
    {
        var result = ImportFixture(LiveHardware());
        var sensor = Assert.Single(result.Configuration.CustomSensors);

        // Stored as 0. On a mix *sensor* that is Average; on a mix *curve* the same 0 is Maximum.
        // Both arrive under a property called SelectedMixFunction and nothing in the file says which
        // table applies, so this assertion is the only thing standing between a working import and a
        // fan that answers to the wrong number.
        Assert.Equal(MixFunction.Average, sensor.Function);
    }

    [Theory]
    [InlineData(0, MixFunction.Maximum)]
    [InlineData(1, MixFunction.Sum)]
    [InlineData(2, MixFunction.Average)]
    [InlineData(3, MixFunction.Minimum)]
    [InlineData(4, MixFunction.Difference)]
    public void A_mix_curve_reads_its_numbers_off_the_curve_table(int stored, MixFunction expected)
    {
        var document = Document(curves:
        [
            new JsonObject { ["Name"] = "a", ["IsHidden"] = false, ["CommandMode"] = 0, ["Percent"] = 50 },
            new JsonObject
            {
                ["Name"] = "mixed",
                ["IsHidden"] = false,
                ["CommandMode"] = 0,
                ["SelectedFanCurves"] = new JsonArray(new JsonObject { ["Name"] = "a" }),
                ["SelectedMixFunction"] = stored,
            },
        ]);

        var result = new FanControlConfigImporter(new InMemorySensorIdentityMap()).Import(document, "t");
        var mix = Assert.IsType<MixCurveDefinition>(result.Configuration.Curves.Single(c => c.Name == "mixed"));

        Assert.Equal(expected, mix.Function);
        Assert.Single(mix.Sources);
    }

    [Theory]
    [InlineData(0, MixFunction.Average)]
    [InlineData(1, MixFunction.Maximum)]
    [InlineData(2, MixFunction.Minimum)]
    [InlineData(3, MixFunction.Sum)]
    [InlineData(4, MixFunction.Difference)]
    public void A_mix_sensor_reads_the_same_numbers_differently(int stored, MixFunction expected)
    {
        var document = Document(sensors:
        [
            new JsonObject
            {
                ["NickName"] = "mixed",
                ["Identifier"] = "Mix/mixed",
                ["IsHidden"] = false,
                ["AllowMissingSensor"] = false,
                ["SelectedMixFunction"] = stored,
                ["SelectedSensors"] = new JsonArray(),
            },
        ]);

        var result = new FanControlConfigImporter(new InMemorySensorIdentityMap()).Import(document, "t");

        Assert.Equal(expected, Assert.Single(result.Configuration.CustomSensors).Function);
    }

    [Fact]
    public void A_curve_pointing_at_absent_hardware_keeps_its_shape_and_is_reported()
    {
        var result = ImportFixture(LiveHardware());

        var gpu = Assert.IsType<AutoCurveDefinition>(
            result.Configuration.Curves.Single(curve => curve.Name == "Auto GPU"));

        // Everything the user tuned survives; only the sensor is missing. Dropping the curve would
        // mean rebuilding it by hand instead of picking one replacement from a dropdown.
        Assert.True(gpu.Source.IsNone);
        Assert.Equal(38f, gpu.IdleTemperature, 3);
        Assert.Equal(80f, gpu.LoadTemperature, 3);
        Assert.Equal(80f, gpu.MaximumDuty.Percent, 3);

        Assert.Contains(
            result.Unresolved,
            note => note.Subject == "Auto GPU" && note.Message.Contains("ADLX", StringComparison.Ordinal));
    }

    [Fact]
    public void A_control_on_hardware_that_has_no_backend_yet_is_reported_rather_than_dropped_in_silence()
    {
        var result = ImportFixture(LiveHardware());

        Assert.True(result.NeedsAttention);
        Assert.Contains(
            result.Unresolved,
            note => note.Message.Contains("AMD (ADLX)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_mix_sensor_keeps_the_sources_it_can_resolve_and_reports_the_rest()
    {
        var map = LiveHardware();
        var result = ImportFixture(map);
        var sensor = Assert.Single(result.Configuration.CustomSensors);

        map.TryGet(new HardwareFingerprint("lhm", Cpu, 2, SensorKind.Temperature), out var cpu);

        // Two sources in the file: the CPU, which is here, and the graphics card, which is not.
        Assert.Equal([cpu], sensor.Sources.ToArray());
        Assert.Contains(result.Unresolved, note => note.Subject == "CPU GPU Mix");
    }

    [Fact]
    public void An_rpm_curve_is_converted_through_the_calibration_of_the_fan_it_drives()
    {
        var document = Document(
            curves: [new JsonObject
            {
                ["Name"] = "speed",
                ["IsHidden"] = false,
                ["CommandMode"] = 1,
                ["Percent"] = 1000,
            }],
            controls: [Control("/lpc/nct6687d/control/0", "speed", calibrated: true)]);

        var result = new FanControlConfigImporter(LiveHardware()).Import(document, "t");
        var flat = Assert.IsType<FlatCurveDefinition>(Assert.Single(result.Configuration.Curves));

        // 1000 RPM sits between the 20 percent and 60 percent measurements below.
        Assert.Equal(40f, flat.Duty.Percent, 3);
        Assert.Contains(result.Adjusted, note => note.Subject == "speed");
    }

    [Fact]
    public void An_rpm_curve_with_nothing_to_convert_it_is_dropped_rather_than_read_as_a_percentage()
    {
        var document = Document(
            curves: [new JsonObject
            {
                ["Name"] = "speed",
                ["IsHidden"] = false,
                ["CommandMode"] = 1,
                ["Percent"] = 1000,
            }],
            controls: [Control("/lpc/nct6687d/control/0", "speed", calibrated: false)]);

        var result = new FanControlConfigImporter(LiveHardware()).Import(document, "t");

        // Read as a percentage, 1000 saturates at full and the fan simply runs flat out -- which
        // looks like a successful import right up until someone hears it.
        Assert.Empty(result.Configuration.Curves);
        Assert.Contains(result.Skipped, note => note.Subject == "speed");

        // And the control that used it is left unbound rather than pointing at nothing.
        var control = Assert.Single(result.Configuration.Controls);
        Assert.True(control.CurveId.IsNone);
        Assert.False(control.Enabled);
    }

    [Fact]
    public void Two_curves_sharing_a_name_are_both_kept()
    {
        var document = Document(curves:
        [
            new JsonObject { ["Name"] = "same", ["IsHidden"] = false, ["CommandMode"] = 0, ["Percent"] = 20 },
            new JsonObject { ["Name"] = "same", ["IsHidden"] = false, ["CommandMode"] = 0, ["Percent"] = 80 },
        ]);

        var result = new FanControlConfigImporter(new InMemorySensorIdentityMap()).Import(document, "t");

        // The source refuses the whole file over this. A rename costs the user one edit; a refusal
        // costs them the configuration.
        Assert.Equal(["same", "same (2)"], result.Configuration.Curves.Select(c => c.Name).ToArray());
        Assert.Contains(result.Adjusted, note => note.Subject == "same");
    }

    [Fact]
    public void Graph_points_are_read_whichever_way_they_were_written()
    {
        foreach (var points in new[]
        {
            new JsonArray("40,20", "80,90"),
            new JsonArray(
                new JsonObject { ["X"] = 40, ["Y"] = 20 },
                new JsonObject { ["X"] = 80, ["Y"] = 90 }),
            new JsonArray(new JsonArray(40, 20), new JsonArray(80, 90)),
        })
        {
            var document = Document(curves: [new JsonObject
            {
                ["Name"] = "graph",
                ["IsHidden"] = false,
                ["CommandMode"] = 0,
                ["SelectedTempSource"] = new JsonObject { ["Identifier"] = "/amdcpu/0/temperature/2" },
                ["Points"] = points,
                ["MinimumTemperature"] = 30,
                ["MaximumTemperature"] = 90,
                ["MaximumCommand"] = 100,
                ["HysteresisConfig"] = new JsonObject { ["HysteresisValueUp"] = 2, ["HysteresisValueDown"] = 2 },
            }]);

            var result = new FanControlConfigImporter(LiveHardware()).Import(document, "t");
            var graph = Assert.IsType<GraphCurveDefinition>(Assert.Single(result.Configuration.Curves));

            Assert.Equal(2, graph.Points.Count);
            Assert.Equal(40f, graph.Points[0].Input, 3);
            Assert.Equal(20f, graph.Points[0].Duty.Percent, 3);
            Assert.Equal(90f, graph.Points[1].Duty.Percent, 3);
        }
    }

    [Fact]
    public void The_older_hysteresis_shape_converts_the_way_the_source_converts_it()
    {
        var document = Document(curves: [new JsonObject
        {
            ["Name"] = "linear",
            ["IsHidden"] = false,
            ["CommandMode"] = 0,
            ["SelectedTempSource"] = new JsonObject { ["Identifier"] = "/amdcpu/0/temperature/2" },
            ["MinimumTemperature"] = 30,
            ["MaximumTemperature"] = 90,
            ["MinimumFanSpeed"] = 20,
            ["MaximumFanSpeed"] = 100,
            ["SelectedHysteresis"] = 4,
            ["SelectedResponseTime"] = 6,
            ["OneWayHysteresis"] = true,
            ["IgnoreHysteresisAtLimits"] = true,
        }]);

        var result = new FanControlConfigImporter(LiveHardware()).Import(document, "t");
        var linear = Assert.IsType<LinearCurveDefinition>(Assert.Single(result.Configuration.Curves));

        // One-way means damp the fall but not the rise, so the up side collapses to one and the down
        // side keeps what was stored.
        Assert.Equal(1f, linear.Hysteresis.DeadbandUp, 3);
        Assert.Equal(4f, linear.Hysteresis.DeadbandDown, 3);
        Assert.Equal(TimeSpan.FromSeconds(1), linear.Hysteresis.ResponseUp);
        Assert.Equal(TimeSpan.FromSeconds(6), linear.Hysteresis.ResponseDown);
    }

    [Fact]
    public void A_sync_curve_mirrors_the_control_it_names()
    {
        var map = LiveHardware();
        var document = Document(curves: [new JsonObject
        {
            ["Name"] = "follow",
            ["IsHidden"] = false,
            ["CommandMode"] = 0,
            ["SelectedControl"] = new JsonObject { ["Identifier"] = "/lpc/nct6687d/control/0" },
            ["SelectedOffset"] = -5,
            ["Proportional"] = false,
        }]);

        var result = new FanControlConfigImporter(map).Import(document, "t");
        var sync = Assert.IsType<SyncCurveDefinition>(Assert.Single(result.Configuration.Curves));

        map.TryGet(new HardwareFingerprint("lhm", SuperIo, 0, SensorKind.Control), out var expected);

        // A control, not the curve behind it -- so the mirrored fan inherits that control's own
        // limits and ramp rate, which is what "the same percentage" actually means to a user.
        Assert.Equal(SyncSourceKind.Control, sync.SourceKind);
        Assert.Equal(expected, sync.SourceControl);
        Assert.Equal(-5f, sync.Offset, 3);
    }

    [Fact]
    public void A_per_control_offset_that_was_doing_something_is_reported()
    {
        var control = Control("/lpc/nct6687d/control/0", curveName: null, calibrated: false);
        control["SelectedOffset"] = 5;

        var result = new FanControlConfigImporter(LiveHardware()).Import(Document(controls: [control]), "t");

        // Impeller has no per-control offset. Dropping it silently would change what the fan does.
        Assert.Contains(result.Adjusted, note => note.Message.Contains("offset", StringComparison.Ordinal));
    }

    [Fact]
    public void A_setting_left_at_its_default_is_not_worth_reporting()
    {
        var result = ImportFixture(LiveHardware());

        // Every control in the fixture has a zero offset and no manual override. Reporting those
        // would bury the two notes that actually need reading.
        Assert.DoesNotContain(result.Adjusted, note => note.Message.Contains("offset", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_that_is_not_json_fails_with_something_worth_reading()
    {
        var path = Path.Combine(Path.GetTempPath(), $"impeller-legacy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ \"FanControl\": { \"Controls\": [ ");

        try
        {
            var importer = new FanControlConfigImporter(new InMemorySensorIdentityMap());
            var error = Assert.Throws<LegacyImportException>(() => importer.ImportFile(path));

            Assert.Contains("not valid JSON", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_document_that_is_not_a_configuration_at_all_fails_before_importing_anything()
    {
        var importer = new FanControlConfigImporter(new InMemorySensorIdentityMap());

        Assert.Throws<LegacyImportException>(() => importer.Import(new JsonObject { ["Something"] = 1 }, "t"));
        Assert.False(FanControlConfigImporter.LooksLegacy(new JsonObject { ["Something"] = 1 }));
        Assert.True(FanControlConfigImporter.LooksLegacy(new JsonObject { ["FanControl"] = new JsonObject() }));
    }

    // ------------------------------------------------------------------ builders

    private static JsonObject Document(
        JsonArray? curves = null,
        JsonArray? controls = null,
        JsonArray? sensors = null) =>
        new()
        {
            ["__VERSION__"] = "275",
            ["FanControl"] = new JsonObject
            {
                ["Controls"] = controls ?? [],
                ["CustomSensors"] = sensors ?? [],
                ["FanCurves"] = curves ?? [],
            },
        };

    private static JsonObject Control(string identifier, string? curveName, bool calibrated) => new()
    {
        ["NickName"] = "fan",
        ["Identifier"] = identifier,
        ["IsHidden"] = false,
        ["Enable"] = true,
        ["SelectedFanCurve"] = curveName is null ? null : new JsonObject { ["Name"] = curveName },
        ["SelectedOffset"] = 0,
        ["SelectedStart"] = 0,
        ["SelectedStop"] = 0,
        ["MinimumPercent"] = 0,
        ["SelectedCommandStepUp"] = 8,
        ["SelectedCommandStepDown"] = 8,
        ["Calibration"] = calibrated
            ? new JsonArray(
                new JsonArray(10, 0, false),
                new JsonArray(20, 500, false),
                new JsonArray(60, 1500, false))
            : [],
    };
}
