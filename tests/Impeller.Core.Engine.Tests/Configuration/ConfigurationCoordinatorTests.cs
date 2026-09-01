using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine.Configuration;
using Impeller.Core.Persistence;

namespace Impeller.Core.Engine.Tests.Configuration;

/// <summary>
/// Covers the path Phase 0 was missing entirely: a document on disk becoming a fan that moves.
/// </summary>
public class ConfigurationCoordinatorTests : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "impeller-config-tests",
        Guid.NewGuid().ToString("N"));

    private readonly FakeSensorRegistry _registry = new();
    private readonly FakeControl _fan;
    private readonly FakeSensor _temperature;
    private readonly ControlLoop _loop;
    private readonly ConfigurationCoordinator _coordinator;

    public ConfigurationCoordinatorTests()
    {
        _fan = _registry.Add(new FakeControl());
        _temperature = _registry.Add(new FakeSensor { Value = 60f });

        _loop = new ControlLoop(_registry, new ControlOwnershipRegistry(TimeProvider.System), TimeProvider.System);
        _coordinator = new ConfigurationCoordinator(
            new ConfigStore(_root, new MigrationRunner([], currentVersion: 0)),
            _loop,
            _registry);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A linear curve from 40 to 80 degrees driving the fan.</summary>
    private ImpellerConfiguration Working(string name = "test")
    {
        var curveId = CurveId.New();

        return new ImpellerConfiguration
        {
            Name = name,
            Curves =
            [
                new LinearCurveDefinition
                {
                    Id = curveId,
                    Name = "ramp",
                    Source = _temperature.Id,
                    MinimumInput = 40f,
                    MaximumInput = 80f,
                    MinimumDuty = Duty.Off,
                    MaximumDuty = Duty.Full,
                },
            ],
            Controls =
            [
                new ControlBindingDefinition
                {
                    ControlId = _fan.Id,
                    CurveId = curveId,
                    Enabled = true,
                    MaximumStepUpPerSecond = 0f,
                    MaximumStepDownPerSecond = 0f,
                },
            ],
        };
    }

    [Fact]
    public void Applying_a_configuration_makes_the_engine_drive_the_fan()
    {
        // The whole point of the milestone: before this, the loop ticked with no curves at all.
        _coordinator.Apply(Working());

        _loop.Tick(Tick);

        Assert.Equal(50f, _fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void A_saved_configuration_still_drives_the_same_fan_after_a_restart()
    {
        _coordinator.Apply(Working("profile"));

        // A second coordinator over the same folder stands in for the service restarting.
        var restarted = new ControlLoop(
            _registry,
            new ControlOwnershipRegistry(TimeProvider.System),
            TimeProvider.System);
        var reloaded = new ConfigurationCoordinator(
            new ConfigStore(_root, new MigrationRunner([], currentVersion: 0)),
            restarted,
            _registry);

        reloaded.Load("profile");
        restarted.Tick(Tick);

        Assert.Equal(50f, _fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void A_first_run_generates_a_configuration_with_every_control_switched_off()
    {
        // Nothing is written to hardware until the user asks. Guessing at a stranger's fan curve
        // is not a defensible default.
        _coordinator.Start();

        _loop.Tick(Tick);

        Assert.Null(_fan.CommandedDuty);
        Assert.Equal(_fan.Id, Assert.Single(_coordinator.Current.Controls).ControlId);
        Assert.False(Assert.Single(_coordinator.Current.Controls).Enabled);
    }

    [Fact]
    public void Start_loads_an_existing_configuration_rather_than_replacing_it()
    {
        _coordinator.Apply(Working(ConfigurationCoordinator.DefaultName));

        var second = new ConfigurationCoordinator(
            new ConfigStore(_root, new MigrationRunner([], currentVersion: 0)),
            _loop,
            _registry);
        second.Start();

        Assert.Single(second.Current.Curves);
    }

    [Fact]
    public void A_configuration_with_errors_never_reaches_the_loop()
    {
        _coordinator.Apply(Working());

        var cycleId = CurveId.New();
        var broken = new ImpellerConfiguration
        {
            Name = "broken",
            Curves =
            [
                new MixCurveDefinition { Id = cycleId, Name = "eats itself", Sources = [cycleId] },
            ],
        };

        var validation = _coordinator.Apply(broken);

        Assert.True(validation.HasErrors);
        Assert.Contains(validation.Errors, issue => issue.Code == "curve-cycle");

        // The good configuration is still what is running, and still what is saved.
        Assert.Equal("test", _coordinator.CurrentName);
        Assert.False(_coordinator.Exists("broken"));

        _loop.Tick(Tick);
        Assert.Equal(50f, _fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Applying_raises_a_change_notice_carrying_the_warnings()
    {
        ConfigurationChanged? seen = null;
        _coordinator.Changed += (_, e) => seen = e;

        var withAbsentSensor = Working() with
        {
            Curves =
            [
                new LinearCurveDefinition
                {
                    Id = CurveId.New(),
                    Name = "points at nothing",
                    Source = SensorId.New(),
                },
            ],
        };

        _coordinator.Apply(withAbsentSensor);

        Assert.NotNull(seen);
        Assert.Contains(seen.Validation.Warnings, issue => issue.Code == "absent-sensor");
    }

    [Fact]
    public void Absent_hardware_is_a_warning_and_the_configuration_still_loads()
    {
        // Unplugging a USB fan hub must not cost someone fan control on everything else.
        var configuration = Working() with
        {
            Controls =
            [
                .. Working().Controls,
                new ControlBindingDefinition { ControlId = SensorId.New(), Enabled = false },
            ],
        };

        var validation = _coordinator.Apply(configuration);

        Assert.False(validation.HasErrors);
        Assert.Contains(validation.Warnings, issue => issue.Code == "absent-control");
    }

    [Fact]
    public void The_name_on_disk_wins_over_the_name_inside_the_document()
    {
        // Renaming a file is a reasonable thing to do; the app should not keep calling it
        // something else afterwards.
        _coordinator.Apply(Working("original"));
        File.Move(
            Path.Combine(_root, "original.json"),
            Path.Combine(_root, "renamed.json"));

        _coordinator.Load("renamed");

        Assert.Equal("renamed", _coordinator.CurrentName);
        Assert.Equal("renamed", _coordinator.Current.Name);
    }

    [Fact]
    public void Several_named_configurations_coexist()
    {
        _coordinator.Apply(Working("quiet"));
        _coordinator.Apply(Working("benchmark"));

        var names = _coordinator.List().Select(entry => entry.Name).ToList();

        Assert.Contains("quiet", names);
        Assert.Contains("benchmark", names);

        Assert.True(_coordinator.Delete("quiet"));
        Assert.False(_coordinator.Exists("quiet"));
        Assert.True(_coordinator.Exists("benchmark"));
    }

    [Fact]
    public void A_configuration_that_round_trips_compares_equal_to_what_was_saved()
    {
        // Reference equality on the collections would make every reload look like a change, which
        // would in turn make "has this been edited" useless over the wire.
        var original = Working("compare");
        _coordinator.Apply(original);

        _coordinator.Load("compare");

        Assert.Equal(original, _coordinator.Current);
    }

    [Fact]
    public void An_unreadable_document_fails_with_something_a_user_can_act_on()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "mangled.json"), "{ \"Curves\": \"not an array\" }");

        var ex = Assert.Throws<ConfigMigrationException>(() => _coordinator.Load("mangled"));

        Assert.Contains("mangled", ex.Message, StringComparison.Ordinal);
    }
}
