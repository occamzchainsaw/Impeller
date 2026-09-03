using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine.Configuration;
using Impeller.Core.Persistence;

namespace Impeller.Core.Engine.Tests.Configuration;

/// <summary>
/// Covers a control pinned by hand surviving a restart.
/// </summary>
/// <remarks>
/// Pinning a fan is an instruction, not a property of the session that issued it. Losing it on
/// restart would mean a fan the user deliberately stopped starts turning again after a reboot,
/// which is the sort of thing nobody notices until they hear it.
/// </remarks>
public class ManualPinTests : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "impeller-pin-tests",
        Guid.NewGuid().ToString("N"));

    // One control id for the life of the test, so a second engine over the same store resolves the
    // same fan the first one pinned.
    private readonly SensorId _controlId;
    private readonly SensorId _sensorId;
    private readonly FakeSensorRegistry _shared = new();

    public ManualPinTests()
    {
        _controlId = _shared.Add(new FakeControl()).Id;
        _sensorId = _shared.Add(new FakeSensor { Value = 40f }).Id;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// An engine over the same store and the same fan, with nothing else carried across.
    /// </summary>
    /// <remarks>
    /// Calling this twice is the restart: the ownership registry and the loop are new, so anything
    /// still in force afterwards came off disk rather than out of memory.
    /// </remarks>
    private (ConfigurationCoordinator Coordinator, ControlLoop Loop, ControlOwnershipRegistry Ownership) Engine()
    {
        var ownership = new ControlOwnershipRegistry(TimeProvider.System);
        var loop = new ControlLoop(_shared, ownership, TimeProvider.System);
        var store = new ConfigStore(_root, new MigrationRunner([], currentVersion: 0));

        return (new ConfigurationCoordinator(store, loop, _shared), loop, ownership);
    }

    private ImpellerConfiguration Pinned(Duty? pin) => new()
    {
        Name = "Pins",
        Controls = [new ControlBindingDefinition
        {
            ControlId = _controlId,
            Enabled = true,
            ManualDuty = pin,
        }],
    };

    private static readonly CurveId Fallback = CurveId.New();

    /// <summary>
    /// The same configuration with a curve behind the pin.
    /// </summary>
    /// <remarks>
    /// A pin with no curve is a supported configuration for a person: the pin is written to disk and
    /// restored on the next start, so there is always something to fall back to. It is not one for a
    /// plugin, whose claim exists only for as long as its process does. Tests about plugin claims
    /// therefore have to say what the fan falls back to, or they exercise the curveless rule by
    /// accident.
    /// </remarks>
    private ImpellerConfiguration PinnedOverCurve(Duty? pin) => new()
    {
        Name = "Pins",
        Curves =
        [
            new GraphCurveDefinition
            {
                Id = Fallback,
                Name = "Fallback",
                Source = _sensorId,
                Points =
                [
                    new CurvePointDefinition(20f, new Duty(10f)),
                    new CurvePointDefinition(80f, new Duty(100f)),
                ],
            },
        ],
        Controls = [new ControlBindingDefinition
        {
            ControlId = _controlId,
            CurveId = Fallback,
            Enabled = true,
            ManualDuty = pin,
        }],
    };

    [Fact]
    public void A_pin_in_a_saved_configuration_is_in_force_as_soon_as_it_is_applied()
    {
        var (coordinator, loop, ownership) = Engine();

        Assert.False(coordinator.Apply(Pinned(new Duty(35f))).HasErrors);

        // The claim is taken by the engine itself, before any shell has connected.
        Assert.Equal(ControlOwnerKind.ManualOverride, ownership.GetOwner(_controlId).Kind);

        loop.Tick(Tick);
        Assert.Equal(35f, loop.GetCommandedDuty(_controlId)!.Value.Percent, 3);
    }

    [Fact]
    public void A_pin_at_zero_holds_the_fan_off_rather_than_reading_as_no_pin_at_all()
    {
        var (coordinator, loop, ownership) = Engine();

        // The case that matters most: a fan the user deliberately stopped.
        coordinator.Apply(Pinned(Duty.Off));
        loop.Tick(Tick);

        Assert.Equal(ControlOwnerKind.ManualOverride, ownership.GetOwner(_controlId).Kind);
        Assert.Equal(0f, loop.GetCommandedDuty(_controlId)!.Value.Percent, 3);
    }

    [Fact]
    public void A_pin_survives_the_engine_being_restarted()
    {
        var first = Engine();
        first.Coordinator.Apply(Pinned(new Duty(60f)));

        // Everything in memory is gone; only the file on disk remains.
        var second = Engine();
        Assert.False(second.Coordinator.Load("Pins").HasErrors);

        Assert.Equal(ControlOwnerKind.ManualOverride, second.Ownership.GetOwner(_controlId).Kind);

        second.Loop.Tick(Tick);
        Assert.Equal(60f, second.Loop.GetCommandedDuty(_controlId)!.Value.Percent, 3);
    }

    [Fact]
    public void Recording_a_pin_saves_it_without_rebuilding_the_configuration()
    {
        var (coordinator, _, _) = Engine();
        coordinator.Apply(Pinned(null));

        Assert.True(coordinator.RecordManualDuty(_controlId, new Duty(80f)));
        Assert.Equal(new Duty(80f), coordinator.Current.Controls[0].ManualDuty);

        // Read back through a fresh engine, because being saved is the whole point of recording it.
        var (reloaded, loop, ownership) = Engine();
        reloaded.Load("Pins");

        Assert.Equal(new Duty(80f), reloaded.Current.Controls[0].ManualDuty);
        Assert.Equal(ControlOwnerKind.ManualOverride, ownership.GetOwner(_controlId).Kind);

        loop.Tick(Tick);
        Assert.Equal(80f, loop.GetCommandedDuty(_controlId)!.Value.Percent, 3);
    }

    [Fact]
    public void Releasing_a_pin_clears_it_from_the_configuration_and_hands_the_fan_back()
    {
        var (coordinator, _, ownership) = Engine();
        coordinator.Apply(Pinned(new Duty(80f)));

        Assert.True(coordinator.RecordManualDuty(_controlId, null));
        Assert.Null(coordinator.Current.Controls[0].ManualDuty);

        // A configuration with no pin releases the claim rather than leaving it holding the fan.
        coordinator.Apply(coordinator.Current);
        Assert.Equal(ControlOwnerKind.Curve, ownership.GetOwner(_controlId).Kind);
    }

    [Fact]
    public void A_control_a_plugin_is_holding_is_not_taken_from_it_by_a_stored_pin()
    {
        var (coordinator, loop, ownership) = Engine();
        coordinator.Apply(PinnedOverCurve(null));

        ownership.TryAcquire(_controlId, ControlOwnerKind.Plugin, "plugin.test");
        loop.TrySetRequestedDuty(_controlId, new Duty(20f), "plugin.test");

        coordinator.Apply(PinnedOverCurve(new Duty(90f)));

        // A live claim outranks a stored one. The alternative is a configuration reload silently
        // seizing a fan from whatever was part-way through doing something with it.
        Assert.Equal(ControlOwnerKind.Plugin, ownership.GetOwner(_controlId).Kind);

        loop.Tick(Tick);
        Assert.Equal(20f, loop.GetCommandedDuty(_controlId)!.Value.Percent, 3);
    }

    [Fact]
    public void Recording_against_a_control_the_configuration_does_not_name_changes_nothing()
    {
        var (coordinator, _, _) = Engine();
        coordinator.Apply(Pinned(null));

        Assert.False(coordinator.RecordManualDuty(SensorId.New(), new Duty(50f)));
    }
}
