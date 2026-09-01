using System.Text.Json;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine.Configuration;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests.Configuration;

/// <summary>
/// Covers the contract everything else in the phase rests on: a configuration survives being
/// written, read back, and turned into the curves that actually drive fans.
/// </summary>
public class ConfigurationSchemaTests
{
    private static readonly SensorId Cpu = SensorId.New();
    private static readonly SensorId Fan = SensorId.New();

    /// <summary>One of every curve type, so nothing can be added without being round-tripped.</summary>
    public static TheoryData<CurveDefinition> EveryCurveType() => new()
    {
        new FlatCurveDefinition { Id = CurveId.New(), Name = "flat", Duty = new Duty(42f) },
        new LinearCurveDefinition
        {
            Id = CurveId.New(),
            Name = "linear",
            Source = Cpu,
            MinimumInput = 30f,
            MaximumInput = 80f,
            MinimumDuty = new Duty(20f),
            MaximumDuty = new Duty(90f),
            Hysteresis = new HysteresisDefinition(2f, 3f, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)),
        },
        new GraphCurveDefinition
        {
            Id = CurveId.New(),
            Name = "graph",
            Source = Cpu,
            Points = [new CurvePointDefinition(40f, new Duty(20f)), new CurvePointDefinition(80f, new Duty(80f))],
        },
        new MixCurveDefinition
        {
            Id = CurveId.New(),
            Name = "mix",
            Function = MixFunction.Average,
            Sources = [CurveId.New(), CurveId.New()],
        },
        new SyncCurveDefinition
        {
            Id = CurveId.New(),
            Name = "sync curve",
            SourceKind = SyncSourceKind.Curve,
            SourceCurve = CurveId.New(),
            Offset = -5f,
        },
        new SyncCurveDefinition
        {
            Id = CurveId.New(),
            Name = "sync control",
            SourceKind = SyncSourceKind.Control,
            SourceControl = Fan,
            Offset = 10f,
            Proportional = true,
        },
        new TriggerCurveDefinition
        {
            Id = CurveId.New(),
            Name = "trigger",
            Source = Cpu,
            IdleInput = 40f,
            LoadInput = 70f,
            IdleDuty = new Duty(30f),
            LoadDuty = new Duty(85f),
            ResponseUp = TimeSpan.FromSeconds(2),
            ResponseDown = TimeSpan.FromSeconds(8),
        },
        new AutoCurveDefinition
        {
            Id = CurveId.New(),
            Name = "auto",
            Source = Cpu,
            IdleTemperature = 35f,
            LoadTemperature = 75f,
            MinimumDuty = new Duty(30f),
            MaximumDuty = Duty.Full,
            Step = 2f,
            Deadband = 3f,
            ResponseTime = TimeSpan.FromSeconds(2),
        },
    };

    [Theory]
    [MemberData(nameof(EveryCurveType))]
    public void Every_curve_type_survives_a_json_round_trip(CurveDefinition curve)
    {
        var original = new ImpellerConfiguration { Name = "test", Curves = [curve] };

        var restored = ImpellerJson.Deserialize(ImpellerJson.Serialize(original));

        Assert.Equal(curve, Assert.Single(restored.Curves));
    }

    [Theory]
    [MemberData(nameof(EveryCurveType))]
    public void Every_curve_type_survives_being_built_and_described_again(CurveDefinition curve)
    {
        // The factory's two directions have to agree, or editing a curve in the UI and saving it
        // would quietly lose whatever the describe side forgot about.
        var rebuilt = CurveFactory.Describe(CurveFactory.Create(curve));

        Assert.Equal(curve, rebuilt);
    }

    [Fact]
    public void The_curve_type_is_written_explicitly_rather_than_inferred_from_its_shape()
    {
        var json = ImpellerJson.Serialize(new ImpellerConfiguration
        {
            Curves = [new FlatCurveDefinition { Id = CurveId.New(), Name = "flat", Duty = new Duty(50f) }],
        });

        Assert.Contains("\"type\": \"flat\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Ids_are_written_as_plain_strings_and_duties_as_plain_numbers()
    {
        // A configuration is hand-edited often enough that {"Value":"..."} around every id is
        // worth removing, and the same converters serve the wire format.
        var id = CurveId.New();
        var json = ImpellerJson.Serialize(new ImpellerConfiguration
        {
            Curves = [new FlatCurveDefinition { Id = id, Name = "flat", Duty = new Duty(42.5f) }],
        });

        Assert.Contains($"\"{id}\"", json, StringComparison.Ordinal);
        Assert.Contains("42.5", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Value\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Enums_are_written_by_name_so_renumbering_one_cannot_repoint_a_config()
    {
        var json = ImpellerJson.Serialize(new ImpellerConfiguration
        {
            Curves = [new MixCurveDefinition { Id = CurveId.New(), Name = "m", Function = MixFunction.Minimum }],
        });

        Assert.Contains("Minimum", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_control_binding_survives_a_round_trip_with_its_calibration()
    {
        var original = new ImpellerConfiguration
        {
            Controls =
            [
                new ControlBindingDefinition
                {
                    ControlId = Fan,
                    CurveId = CurveId.New(),
                    Enabled = true,
                    MinimumDuty = new Duty(20f),
                    MaximumDuty = new Duty(90f),
                    FailsafeDuty = Duty.Full,
                    StartDuty = new Duty(22f),
                    StopDuty = new Duty(16f),
                    PairedFanSensorId = SensorId.New(),
                    Calibration =
                    [
                        new CalibrationPointDefinition(new Duty(10f), 0, Avoid: true),
                        new CalibrationPointDefinition(new Duty(50f), 1208),
                    ],
                },
            ],
        };

        var restored = ImpellerJson.Deserialize(ImpellerJson.Serialize(original));

        Assert.Equal(original.Controls[0], Assert.Single(restored.Controls));
    }

    [Fact]
    public void A_custom_sensor_survives_a_round_trip()
    {
        var original = new ImpellerConfiguration
        {
            CustomSensors =
            [
                new CustomSensorDefinition
                {
                    Id = SensorId.New(),
                    Name = "CPU GPU Mix",
                    Kind = CustomSensorKind.Mix,
                    Function = MixFunction.Maximum,
                    Sources = [Cpu, SensorId.New()],
                },
            ],
        };

        var restored = ImpellerJson.Deserialize(ImpellerJson.Serialize(original));

        Assert.Equal(original.CustomSensors[0], Assert.Single(restored.CustomSensors));
    }

    [Fact]
    public void A_binding_round_trips_through_the_factory()
    {
        var definition = new ControlBindingDefinition
        {
            ControlId = Fan,
            CurveId = CurveId.New(),
            Enabled = true,
            MinimumDuty = new Duty(15f),
            MaximumDuty = new Duty(95f),
            FailsafeDuty = new Duty(80f),
            MaximumStepUpPerSecond = 8f,
            MaximumStepDownPerSecond = 4f,
            StartDuty = new Duty(22f),
            StopDuty = new Duty(16f),
            PairedFanSensorId = SensorId.New(),
        };

        var rebuilt = CurveFactory.Describe(CurveFactory.CreateBinding(definition));

        Assert.Equal(definition, rebuilt);
    }

    [Fact]
    public void A_configuration_written_by_a_newer_build_still_loads()
    {
        // Unknown members are ignored rather than fatal: refusing to control fans because a field
        // was added in a later version is a far worse outcome than ignoring the field.
        const string json = """
            {
              "Name": "future",
              "SomethingAddedLater": { "nested": true },
              "Curves": [ { "type": "flat", "Id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
                            "Name": "flat", "Duty": 50, "UnknownKnob": 7 } ],
              "Controls": []
            }
            """;

        var restored = ImpellerJson.Deserialize(json);

        Assert.Equal("future", restored.Name);
        Assert.Equal(50f, Assert.IsType<FlatCurveDefinition>(Assert.Single(restored.Curves)).Duty.Percent, 3);
    }

    [Fact]
    public void A_duty_written_as_a_quoted_number_is_still_read()
    {
        const string json = """
            { "Curves": [ { "type": "flat", "Id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
                            "Name": "flat", "Duty": "37.5" } ] }
            """;

        var curve = Assert.IsType<FlatCurveDefinition>(Assert.Single(ImpellerJson.Deserialize(json).Curves));

        Assert.Equal(37.5f, curve.Duty.Percent, precision: 3);
    }

    [Fact]
    public void A_curve_naming_a_type_this_build_does_not_know_is_rejected_clearly()
    {
        const string json = """
            { "Curves": [ { "type": "quantum", "Id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff" } ] }
            """;

        Assert.Throws<JsonException>(() => ImpellerJson.Deserialize(json));
    }
}
