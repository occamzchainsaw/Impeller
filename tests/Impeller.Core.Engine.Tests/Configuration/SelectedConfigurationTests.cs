using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine.Configuration;
using Impeller.Core.Persistence;

namespace Impeller.Core.Engine.Tests.Configuration;

/// <summary>
/// Covers the engine coming back on the configuration it was last told to run.
/// </summary>
/// <remarks>
/// Found while working out how to verify "reboot, confirm the service resumed the same config": it
/// would not have. The engine started on whatever the settings file named, so switching
/// configuration worked until the next restart and then silently undid itself — which is the kind of
/// bug people blame on themselves rather than report.
/// </remarks>
public sealed class SelectedConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "impeller-selection-" + Guid.NewGuid().ToString("N"));

    private readonly FakeSensorRegistry _registry = new();

    private SelectedConfiguration Selection =>
        new(Path.Combine(_root, StateLocation.SelectionName));

    public SelectedConfigurationTests()
    {
        Directory.CreateDirectory(Configurations);
        _registry.Add(new FakeControl("CPU Fan"));
    }

    private string Configurations => Path.Combine(_root, StateLocation.FolderName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that outlives the run is not worth failing a test over.
        }
    }

    /// <summary>A coordinator over the same folder, as a fresh start would build one.</summary>
    private ConfigurationCoordinator Coordinator()
    {
        var store = new ConfigStore(Configurations, new MigrationRunner([], currentVersion: 0));
        var ownership = new ControlOwnershipRegistry(TimeProvider.System);
        var loop = new ControlLoop(_registry, ownership, TimeProvider.System);

        return new ConfigurationCoordinator(store, loop, _registry, null, Selection);
    }

    [Fact]
    public void Nothing_is_remembered_before_anything_has_been_loaded()
    {
        Assert.Null(Selection.Read());
    }

    [Fact]
    public void Applying_a_configuration_records_it_as_the_one_in_force()
    {
        var coordinator = Coordinator();

        coordinator.Apply(new ImpellerConfiguration { Name = "Quiet" });

        Assert.Equal("Quiet", Selection.Read());
    }

    [Fact]
    public void A_restart_comes_back_on_what_was_last_loaded()
    {
        // The whole point. A second coordinator over the same folder is what the service builds on
        // its next start, and it must not go back to the settings file's default.
        Coordinator().Apply(new ImpellerConfiguration { Name = "Quiet" });

        var restarted = Coordinator();
        restarted.Start("Default");

        Assert.Equal("Quiet", restarted.CurrentName);
    }

    [Fact]
    public void A_remembered_configuration_that_has_since_been_deleted_is_ignored()
    {
        // Otherwise the engine generates an empty configuration under the deleted name and drives
        // nothing, which looks exactly like the engine having failed.
        var coordinator = Coordinator();
        coordinator.Apply(new ImpellerConfiguration { Name = "Quiet" });
        coordinator.Delete("Quiet");

        var restarted = Coordinator();
        restarted.Start("Default");

        Assert.Equal("Default", restarted.CurrentName);
    }

    [Fact]
    public void A_configuration_that_failed_validation_is_not_remembered()
    {
        // Remembering a rejected configuration would mean an engine that comes back on a broken one
        // and cannot be talked out of it without editing a file by hand.
        var coordinator = Coordinator();
        coordinator.Apply(new ImpellerConfiguration { Name = "Good" });

        var broken = new ImpellerConfiguration
        {
            Name = "Broken",
            Controls =
            [
                new ControlBindingDefinition
                {
                    ControlId = _registry.Controls[0].Id,
                    MinimumDuty = new Duty(90f),
                    MaximumDuty = new Duty(10f),
                },
            ],
        };

        Assert.True(coordinator.Apply(broken).HasErrors);
        Assert.Equal("Good", Selection.Read());
    }

    [Fact]
    public void An_unreadable_record_reads_as_nothing_remembered()
    {
        // Falling back to the default configuration cools the machine. Refusing to start does not.
        File.WriteAllText(Path.Combine(_root, StateLocation.SelectionName), "{ this is not json");

        Assert.Null(Selection.Read());

        var coordinator = Coordinator();
        coordinator.Start("Default");

        Assert.Equal("Default", coordinator.CurrentName);
    }

    [Fact]
    public void The_record_sits_beside_the_configurations_not_among_them()
    {
        // Anything ending in .json inside that folder is offered to the user as a configuration.
        Coordinator().Apply(new ImpellerConfiguration { Name = "Quiet" });

        var store = new ConfigStore(Configurations, new MigrationRunner([], currentVersion: 0));

        Assert.Equal(["Quiet"], store.List().Select(entry => entry.Name));
        Assert.True(File.Exists(Path.Combine(_root, StateLocation.SelectionName)));
    }
}
