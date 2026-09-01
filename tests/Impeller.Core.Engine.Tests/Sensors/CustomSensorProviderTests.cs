using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine.Configuration;
using Impeller.Core.Engine.Curves;
using Impeller.Core.Engine.Sensors;

namespace Impeller.Core.Engine.Tests.Sensors;

public class CustomSensorProviderTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly FakeSensorRegistry _registry = new();
    private readonly FakeSensor _cpu;
    private readonly FakeSensor _gpu;
    private readonly CustomSensorProvider _provider;

    public CustomSensorProviderTests()
    {
        _cpu = _registry.Add(new FakeSensor(name: "cpu") { Value = 60f });
        _gpu = _registry.Add(new FakeSensor(name: "gpu") { Value = 75f });
        _provider = new CustomSensorProvider(TimeProvider.System) { Registry = _registry };
    }

    private CustomSensorDefinition MixOf(string name, params SensorId[] sources) => new()
    {
        Id = SensorId.New(),
        Name = name,
        Kind = CustomSensorKind.Mix,
        Function = MixFunction.Maximum,
        Sources = sources,
    };

    [Fact]
    public async Task Sensors_appear_as_ordinary_sensors()
    {
        var mix = MixOf("hottest", _cpu.Id, _gpu.Id);
        _provider.SetDefinitions([mix]);

        await _provider.RefreshAsync(CancellationToken.None);

        var sensor = Assert.Single(_provider.Sensors);
        Assert.Equal(mix.Id, sensor.Id);
        Assert.Equal(75f, sensor.Value!.Value, precision: 3);
        Assert.Equal(SensorKind.Temperature, sensor.Kind);
        Assert.Empty(_provider.Controls);
    }

    [Fact]
    public async Task A_sensor_derived_from_another_is_correct_within_one_tick()
    {
        // Evaluated in dependency order, so a chain does not lag a tick per link.
        var inner = MixOf("hottest", _cpu.Id, _gpu.Id);
        var outer = MixOf("hotter still", inner.Id);

        // Registered outer-first on purpose: the ordering must come from the dependencies, not
        // from the order somebody happened to define them in.
        _provider.SetDefinitions([outer, inner]);

        await _provider.RefreshAsync(CancellationToken.None);

        var result = _provider.Sensors.Single(sensor => sensor.Id == outer.Id);
        Assert.Equal(75f, result.Value!.Value, precision: 3);
    }

    [Fact]
    public async Task A_sensor_that_reads_itself_is_dropped_rather_than_evaluated()
    {
        var id = SensorId.New();
        var looping = new CustomSensorDefinition
        {
            Id = id,
            Name = "ouroboros",
            Kind = CustomSensorKind.Mix,
            Sources = [id],
        };

        _provider.SetDefinitions([looping, MixOf("fine", _cpu.Id)]);
        await _provider.RefreshAsync(CancellationToken.None);

        Assert.DoesNotContain(_provider.Sensors, sensor => sensor.Id == id);
        Assert.Single(_provider.Sensors);
    }

    [Fact]
    public async Task A_two_sensor_loop_drops_both_of_them()
    {
        var first = SensorId.New();
        var second = SensorId.New();

        _provider.SetDefinitions(
        [
            new CustomSensorDefinition { Id = first, Name = "a", Sources = [second] },
            new CustomSensorDefinition { Id = second, Name = "b", Sources = [first] },
        ]);

        await _provider.RefreshAsync(CancellationToken.None);

        Assert.Empty(_provider.Sensors);
    }

    [Fact]
    public void A_surviving_sensor_keeps_its_averaging_history_across_a_reconfiguration()
    {
        // Adjusting an unrelated sensor should not cost a time-average sensor its window.
        var averaged = new CustomSensorDefinition
        {
            Id = SensorId.New(),
            Name = "smoothed",
            Kind = CustomSensorKind.TimeAverage,
            Sources = [_cpu.Id],
            Window = TimeSpan.FromSeconds(30),
        };

        _provider.SetDefinitions([averaged]);
        var before = _provider.Sensors.Single();

        _provider.SetDefinitions([averaged, MixOf("new one", _gpu.Id)]);

        Assert.Same(before, _provider.Sensors.Single(sensor => sensor.Id == averaged.Id));
    }

    [Fact]
    public async Task Custom_sensors_refresh_after_the_hardware_they_read()
    {
        // The registry orders derived providers last. Without that a mix would report a value
        // computed from readings taken before the hardware provider had refreshed.
        var registry = new AggregatingSensorRegistry();
        var hardware = new StepningProvider();

        await registry.AddAsync(hardware, CancellationToken.None);

        var derived = new CustomSensorProvider(TimeProvider.System) { Registry = registry };
        derived.SetDefinitions(
        [
            new CustomSensorDefinition
            {
                Id = SensorId.New(),
                Name = "follows",
                Kind = CustomSensorKind.Offset,
                Sources = [hardware.Sensor.Id],
            },
        ]);

        await registry.AddAsync(derived, CancellationToken.None);

        await registry.RefreshDueAsync(DateTimeOffset.UnixEpoch, CancellationToken.None);

        // The hardware sensor moved to 1 this pass; the derived sensor must already show it.
        Assert.Equal(1f, derived.Sensors.Single().Value!.Value, precision: 3);
    }

    [Fact]
    public async Task A_curve_drives_a_fan_from_a_custom_sensor_end_to_end()
    {
        // The shape the reference config actually uses: two temperatures mixed into one sensor,
        // and a curve driving a fan from it. Without custom sensors that config imports broken.
        var registry = new AggregatingSensorRegistry();
        var hardware = new StaticProvider(cpu: 60f, gpu: 90f);
        await registry.AddAsync(hardware, CancellationToken.None);

        var custom = new CustomSensorProvider(TimeProvider.System) { Registry = registry };
        await registry.AddAsync(custom, CancellationToken.None);

        var root = Path.Combine(Path.GetTempPath(), "impeller-custom-e2e", Guid.NewGuid().ToString("N"));

        try
        {
            var loop = new ControlLoop(
                registry,
                new ControlOwnershipRegistry(TimeProvider.System),
                TimeProvider.System);

            var coordinator = new ConfigurationCoordinator(
                new Core.Persistence.ConfigStore(root, new Core.Persistence.MigrationRunner([], 0)),
                loop,
                registry,
                custom);

            var mixId = SensorId.New();
            var curveId = CurveId.New();

            var validation = coordinator.Apply(new ImpellerConfiguration
            {
                Name = "mixed",
                CustomSensors =
                [
                    new CustomSensorDefinition
                    {
                        Id = mixId,
                        Name = "CPU GPU Mix",
                        Kind = CustomSensorKind.Mix,
                        Function = MixFunction.Maximum,
                        Sources = [hardware.Cpu.Id, hardware.Gpu.Id],
                    },
                ],
                Curves =
                [
                    new LinearCurveDefinition
                    {
                        Id = curveId,
                        Name = "ramp",
                        Source = mixId,
                        MinimumInput = 50f,
                        MaximumInput = 100f,
                        MinimumDuty = Duty.Off,
                        MaximumDuty = Duty.Full,
                    },
                ],
                Controls =
                [
                    new ControlBindingDefinition
                    {
                        ControlId = hardware.Fan.Id,
                        CurveId = curveId,
                        Enabled = true,
                        MaximumStepUpPerSecond = 0f,
                        MaximumStepDownPerSecond = 0f,
                    },
                ],
            });

            Assert.False(validation.HasErrors);

            await registry.RefreshDueAsync(DateTimeOffset.UnixEpoch, CancellationToken.None);
            loop.Tick(Tick);

            // The mix is the hotter of 60 and 90; 90 is 80% of the way along a 50-to-100 ramp.
            Assert.Equal(80f, hardware.Fan.CommandedDuty!.Value.Percent, precision: 3);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>A provider holding two temperatures and a fan, all with fixed readings.</summary>
    private sealed class StaticProvider(float cpu, float gpu) : ISensorProvider
    {
        public FakeSensor Cpu { get; } = new(name: "cpu") { Value = cpu };

        public FakeSensor Gpu { get; } = new(name: "gpu") { Value = gpu };

        public FakeControl Fan { get; } = new();

        public string ProviderId => "static";

        public string DisplayName => "Static";

        public TimeSpan PollInterval => TimeSpan.Zero;

        public IReadOnlyList<ISensor> Sensors => [Cpu, Gpu, Fan];

        public IReadOnlyList<IControl> Controls => [Fan];

        public event EventHandler? TopologyChanged
        {
            add { }
            remove { }
        }

        public Task<ProviderInitializationResult> InitializeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ProviderInitializationResult.Success(3, 1));

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A provider whose sensor counts up by one on every refresh.</summary>
    private sealed class StepningProvider : ISensorProvider
    {
        public FakeSensor Sensor { get; } = new() { Value = 0f };

        public string ProviderId => "stepping";

        public string DisplayName => "Stepping";

        public TimeSpan PollInterval => TimeSpan.Zero;

        public IReadOnlyList<ISensor> Sensors => [Sensor];

        public IReadOnlyList<IControl> Controls => [];

        public event EventHandler? TopologyChanged
        {
            add { }
            remove { }
        }

        public Task<ProviderInitializationResult> InitializeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ProviderInitializationResult.Success(1, 0));

        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            Sensor.Value = (Sensor.Value ?? 0f) + 1f;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
