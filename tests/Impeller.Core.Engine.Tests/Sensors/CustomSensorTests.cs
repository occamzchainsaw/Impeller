using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine.Sensors;

namespace Impeller.Core.Engine.Tests.Sensors;

public class CustomSensorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    private readonly FakeSensorRegistry _registry = new();
    private readonly FakeSensor _cpu;
    private readonly FakeSensor _gpu;

    public CustomSensorTests()
    {
        _cpu = _registry.Add(new FakeSensor(name: "cpu") { Value = 60f });
        _gpu = _registry.Add(new FakeSensor(name: "gpu") { Value = 75f });
    }

    private CustomSensorDefinition Mix(
        MixFunction function = MixFunction.Maximum,
        bool allowMissing = false,
        params SensorId[] sources) =>
        new()
        {
            Id = SensorId.New(),
            Name = "mix",
            Kind = CustomSensorKind.Mix,
            Function = function,
            AllowMissingSource = allowMissing,
            Sources = sources.Length == 0 ? [_cpu.Id, _gpu.Id] : sources,
        };

    private float? Evaluate(CustomSensorDefinition definition, DateTimeOffset? at = null)
    {
        var sensor = new CustomSensor(definition);
        sensor.Refresh(_registry.GetValue, at ?? Start);
        return sensor.Value;
    }

    [Theory]
    [InlineData(MixFunction.Maximum, 75f)]
    [InlineData(MixFunction.Minimum, 60f)]
    [InlineData(MixFunction.Average, 67.5f)]
    [InlineData(MixFunction.Sum, 135f)]
    [InlineData(MixFunction.Difference, -15f)]
    public void A_mix_combines_its_sources(MixFunction function, float expected)
    {
        Assert.Equal(expected, Evaluate(Mix(function))!.Value, precision: 3);
    }

    [Fact]
    public void A_mix_reports_nothing_when_a_source_stops_answering()
    {
        // A "hottest of CPU and GPU" sensor that keeps reporting after the GPU goes quiet is
        // reporting something other than what it claims to be.
        _gpu.Value = null;

        Assert.Null(Evaluate(Mix()));
    }

    [Fact]
    public void A_mix_can_be_told_to_carry_on_without_a_missing_source()
    {
        _gpu.Value = null;

        Assert.Equal(60f, Evaluate(Mix(allowMissing: true))!.Value, precision: 3);
    }

    [Fact]
    public void A_mix_with_every_source_missing_reports_nothing_either_way()
    {
        _cpu.Value = null;
        _gpu.Value = null;

        Assert.Null(Evaluate(Mix(allowMissing: true)));
    }

    [Theory]
    [InlineData(5f, false, 65f)]
    [InlineData(-5f, false, 55f)]
    [InlineData(10f, true, 66f)]
    public void An_offset_shifts_or_scales_its_source(float offset, bool proportional, float expected)
    {
        var definition = new CustomSensorDefinition
        {
            Id = SensorId.New(),
            Name = "offset",
            Kind = CustomSensorKind.Offset,
            Sources = [_cpu.Id],
            Offset = offset,
            Proportional = proportional,
        };

        Assert.Equal(expected, Evaluate(definition)!.Value, precision: 3);
    }

    [Fact]
    public void A_time_average_reports_from_its_first_sample_rather_than_waiting_for_a_full_window()
    {
        // Waiting for the window to fill would leave a fan holding for ten seconds after every
        // restart. A short average is a worse answer than a long one; no answer is worse than both.
        var definition = new CustomSensorDefinition
        {
            Id = SensorId.New(),
            Name = "smoothed",
            Kind = CustomSensorKind.TimeAverage,
            Sources = [_cpu.Id],
            Window = TimeSpan.FromSeconds(10),
        };

        var sensor = new CustomSensor(definition);
        sensor.Refresh(_registry.GetValue, Start);

        Assert.Equal(60f, sensor.Value!.Value, precision: 3);
    }

    [Fact]
    public void A_time_average_smooths_a_step_and_then_settles_on_it()
    {
        var definition = new CustomSensorDefinition
        {
            Id = SensorId.New(),
            Name = "smoothed",
            Kind = CustomSensorKind.TimeAverage,
            Sources = [_cpu.Id],
            Window = TimeSpan.FromSeconds(4),
        };

        var sensor = new CustomSensor(definition);

        sensor.Refresh(_registry.GetValue, Start);
        _cpu.Value = 80f;
        sensor.Refresh(_registry.GetValue, Start + TimeSpan.FromSeconds(1));

        // Halfway between the old and new readings, not straight to the new one.
        Assert.Equal(70f, sensor.Value!.Value, precision: 3);

        // Once the old sample has aged out of the window, only the new value remains.
        for (var i = 2; i <= 8; i++)
        {
            sensor.Refresh(_registry.GetValue, Start + TimeSpan.FromSeconds(i));
        }

        Assert.Equal(80f, sensor.Value!.Value, precision: 3);
    }

    [Fact]
    public void A_file_sensor_reads_a_number_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"impeller-file-sensor-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "42.5\n");

        try
        {
            var definition = new CustomSensorDefinition
            {
                Id = SensorId.New(),
                Name = "from file",
                Kind = CustomSensorKind.File,
                Path = path,
            };

            Assert.Equal(42.5f, Evaluate(definition)!.Value, precision: 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_that_is_not_there_reads_as_no_value_rather_than_throwing()
    {
        // Faulting here would take down the whole refresh pass and every other custom sensor with it.
        var definition = new CustomSensorDefinition
        {
            Id = SensorId.New(),
            Name = "from file",
            Kind = CustomSensorKind.File,
            Path = Path.Combine(Path.GetTempPath(), $"impeller-absent-{Guid.NewGuid():N}.txt"),
        };

        Assert.Null(Evaluate(definition));
    }

    [Fact]
    public void Renaming_a_custom_sensor_keeps_its_identity()
    {
        // The app this replaces derives the identifier from the name, so a rename silently
        // orphans every curve pointing at it.
        var definition = Mix();
        var sensor = new CustomSensor(definition);

        sensor.Update(definition with { Name = "something else" });

        Assert.Equal(definition.Id, sensor.Id);
        Assert.Equal("something else", sensor.Name);
    }
}
