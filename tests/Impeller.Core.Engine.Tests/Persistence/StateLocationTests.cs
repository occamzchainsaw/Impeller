using System.Text.Json.Nodes;
using Impeller.Core.Persistence;

namespace Impeller.Core.Engine.Tests.Persistence;

/// <summary>
/// Covers where the sensor identity map lives, and getting an existing one there.
/// </summary>
/// <remarks>
/// This file is the identity of every sensor on the machine. Losing it does not fail loudly — it
/// silently repoints every curve in every configuration at whatever is enumerated first next time,
/// which is a machine that quietly cools the wrong things. So the move is tested rather than
/// assumed, including what happens when it cannot be done.
/// </remarks>
public sealed class StateLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "impeller-state-" + Guid.NewGuid().ToString("N"));

    private string Configurations => Path.Combine(_root, StateLocation.FolderName);

    private string Beside => Path.Combine(_root, StateLocation.IdentityMapName);

    private string Inside => Path.Combine(Configurations, StateLocation.IdentityMapName);

    public StateLocationTests() => Directory.CreateDirectory(Configurations);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that outlives the test run is not worth failing it over.
        }
    }

    [Fact]
    public void The_identity_map_sits_beside_the_configurations_not_among_them()
    {
        // Among them, the config store lists it as a saved configuration — it appears in the
        // shell's dropdown and fails to load when chosen.
        Assert.Equal(Beside, StateLocation.ResolveIdentityMap(Configurations));
    }

    [Fact]
    public void An_existing_map_inside_the_folder_is_moved_out_of_it()
    {
        File.WriteAllText(Inside, """{"entries":[]}""");

        var resolved = StateLocation.ResolveIdentityMap(Configurations);

        Assert.Equal(Beside, resolved);
        Assert.True(File.Exists(Beside));
        Assert.False(File.Exists(Inside));
    }

    [Fact]
    public void The_moved_map_keeps_its_contents()
    {
        const string Contents = """{"entries":[{"id":"x"}]}""";
        File.WriteAllText(Inside, Contents);

        StateLocation.ResolveIdentityMap(Configurations);

        Assert.Equal(Contents, File.ReadAllText(Beside));
    }

    [Fact]
    public void A_map_already_in_the_right_place_wins_over_a_leftover_inside()
    {
        // Both existing means an older build wrote one and a newer build wrote the other. The one
        // in the current location is the one in use, and overwriting it with the stale copy would
        // undo however much identity the newer runs had learned.
        File.WriteAllText(Beside, """{"entries":["new"]}""");
        File.WriteAllText(Inside, """{"entries":["old"]}""");

        var resolved = StateLocation.ResolveIdentityMap(Configurations);

        Assert.Equal(Beside, resolved);
        Assert.Contains("new", File.ReadAllText(Beside), StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_with_no_map_yet_is_told_where_to_put_one()
    {
        var resolved = StateLocation.ResolveIdentityMap(Configurations);

        Assert.Equal(Beside, resolved);
        Assert.False(File.Exists(resolved));
    }

    [Fact]
    public void A_trailing_separator_does_not_change_the_answer()
    {
        Assert.Equal(Beside, StateLocation.ResolveIdentityMap(Configurations + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void The_config_store_no_longer_lists_the_identity_map_as_a_configuration()
    {
        // The symptom that found this. The store lists every JSON file in its folder, so a machine
        // state file living there showed up beside the user's own configurations.
        File.WriteAllText(Inside, """{"entries":[]}""");
        StateLocation.ResolveIdentityMap(Configurations);

        var store = new ConfigStore(Configurations, new MigrationRunner([], currentVersion: 0));
        store.Save("Quiet", new JsonObject { ["Name"] = "Quiet" });

        var names = store.List().Select(entry => entry.Name).ToList();

        Assert.Equal(["Quiet"], names);
    }
}
