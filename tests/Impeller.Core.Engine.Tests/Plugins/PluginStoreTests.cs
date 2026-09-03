using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Configuration;
using Impeller.Core.Persistence;
using Impeller.Plugins.Abstractions;
using Impeller.Plugins.Host;

namespace Impeller.Core.Engine.Tests.Plugins;

/// <summary>
/// Covers <c>plugins.json</c>: where it lives, what survives a round trip, and what happens when it
/// cannot be read.
/// </summary>
/// <remarks>
/// The failure direction is the interesting part. An unreadable selection file falls back to the
/// default configuration, which is merely inconvenient; an unreadable plugin record falls back to
/// every plugin pending and granted nothing. The user is prompted again, and until they answer,
/// nothing outside the engine drives a fan. That is the safe direction and it is worth the prompts.
/// </remarks>
public sealed class PluginStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "impeller-pluginstore-" + Guid.NewGuid().ToString("N"));

    public PluginStoreTests() => Directory.CreateDirectory(_root);

    private string Path_ => System.IO.Path.Combine(_root, StateLocation.PluginsName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over.
        }
    }

    private static PluginRecord Record(params SensorId[] controls) => new(
        "com.example.plugin",
        "Example",
        "1.2.3",
        PluginAdmissionState.Approved,
        Enabled: true,
        Requested: [PluginCapability.ReadSensors, PluginCapability.ControlFans],
        Capabilities: [PluginCapability.ControlFans],
        Controls: controls,
        Approved: new PluginIdentity(@"C:\Programs\Rig\Rig.exe", "S-1-5-21-1-2-3-1001"),
        LastSeen: new PluginIdentity(@"C:\Programs\Rig\Rig.exe", "S-1-5-21-1-2-3-1001"),
        FirstSeenAt: DateTimeOffset.UnixEpoch,
        LastSeenAt: DateTimeOffset.UnixEpoch.AddDays(1));

    [Fact]
    public void Nothing_stored_reads_as_nothing_rather_than_failing()
    {
        var store = new PluginStore(Path_);

        Assert.Empty(store.Read());
        Assert.Null(store.LastError);
    }

    [Fact]
    public void A_record_comes_back_exactly_as_it_went_in()
    {
        var store = new PluginStore(Path_);
        var fan = SensorId.New();

        Assert.True(store.Write([Record(fan)]));

        var read = Assert.Single(new PluginStore(Path_).Read());

        Assert.Equal(Record(fan), read);
    }

    [Fact]
    public void An_unreadable_record_leaves_every_plugin_pending_rather_than_stopping_the_engine()
    {
        File.WriteAllText(Path_, "{ this is not json");

        var store = new PluginStore(Path_);

        Assert.Empty(store.Read());
        Assert.NotNull(store.LastError);
    }

    [Fact]
    public void A_record_written_by_a_newer_build_is_ignored_rather_than_guessed_at()
    {
        // Guessing here means handing a program permissions from a schema whose meaning has changed.
        File.WriteAllText(Path_, """{ "version": 99, "plugins": [ { "id": "com.example.plugin" } ] }""");

        Assert.Empty(new PluginStore(Path_).Read());
    }

    [Fact]
    public void An_entry_with_an_id_no_plugin_could_present_is_dropped()
    {
        // It can never match a connecting plugin, so keeping it only clutters the Plugins page.
        File.WriteAllText(Path_, """
            {
              "version": 1,
              "plugins": [
                { "id": "rigfan", "state": "Approved" },
                { "id": "com.example.plugin", "state": "Approved" }
              ]
            }
            """);

        var read = Assert.Single(new PluginStore(Path_).Read());

        Assert.Equal("com.example.plugin", read.Id);
    }

    [Fact]
    public void A_control_id_that_is_not_an_id_is_dropped_without_taking_the_entry_with_it()
    {
        // A hand-edited file should cost the user one grant, not every grant.
        var fan = SensorId.New();

        File.WriteAllText(Path_, $$"""
            {
              "version": 1,
              "plugins": [
                {
                  "id": "com.example.plugin",
                  "state": "Approved",
                  "capabilities": [ "ControlFans" ],
                  "requested": [ "ControlFans" ],
                  "controls": [ "not-a-guid", "{{fan}}", "" ]
                }
              ]
            }
            """);

        var read = Assert.Single(new PluginStore(Path_).Read());

        Assert.Equal([fan], read.Controls);
    }

    [Fact]
    public void An_entry_with_no_display_name_falls_back_to_its_id_rather_than_showing_blank()
    {
        File.WriteAllText(Path_, """
            { "version": 1, "plugins": [ { "id": "com.example.plugin" } ] }
            """);

        Assert.Equal("com.example.plugin", new PluginStore(Path_).Read()[0].DisplayName);
    }

    [Fact]
    public void A_write_that_cannot_happen_is_reported_rather_than_thrown()
    {
        // A failure costs the user's answer at the next restart, which is worth far less than the
        // running engine that throwing would take down.
        var blocked = System.IO.Path.Combine(_root, "blocked");
        Directory.CreateDirectory(blocked);

        var store = new PluginStore(blocked);

        Assert.False(store.Write([Record()]));
        Assert.NotNull(store.LastError);
    }

    [Fact]
    public void The_record_sits_beside_the_configurations_rather_than_among_them()
    {
        // Anything ending in .json inside that folder is offered to the user as a configuration
        // they could load, and this is installation state that must not travel with one.
        var configurations = System.IO.Path.Combine(_root, StateLocation.FolderName);
        Directory.CreateDirectory(configurations);

        var path = StateLocation.ResolvePlugins(configurations);
        new PluginStore(path).Write([Record()]);

        var store = new ConfigStore(configurations, new MigrationRunner([], currentVersion: 0));

        Assert.Empty(store.List());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void The_file_is_legible_to_someone_who_opens_it()
    {
        // It records which programs may drive the fans in this machine. Someone should be able to
        // read that without a tool.
        new PluginStore(Path_).Write([Record(SensorId.New())]);

        var text = File.ReadAllText(Path_);

        Assert.Contains("\n", text, StringComparison.Ordinal);
        Assert.Contains("\"ControlFans\"", text, StringComparison.Ordinal);
        Assert.Contains("\"Approved\"", text, StringComparison.Ordinal);
    }
}
