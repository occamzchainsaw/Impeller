using Impeller.Core.Abstractions;
using Impeller.Core.Persistence;

namespace Impeller.Core.Engine.Tests.Configuration;

/// <summary>
/// Covers the names a user gives their fans: where they live, and what happens to them.
/// </summary>
/// <remarks>
/// The least replaceable state in the product after the identity map. Nobody remembers which header
/// they called "seat blower" once it is gone, and unlike a curve it cannot be worked out again from
/// anything. So the interesting cases here are all about not losing them.
/// </remarks>
public sealed class SensorNameTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "impeller-names-" + Guid.NewGuid().ToString("N"));

    public SensorNameTests() => Directory.CreateDirectory(_root);

    private string Path_ => System.IO.Path.Combine(_root, StateLocation.NamesName);

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

    [Fact]
    public void A_name_survives_a_restart()
    {
        var fan = SensorId.New();

        Assert.True(new JsonSensorNames(Path_).SetName(fan, "Seat blower"));

        Assert.Equal("Seat blower", new JsonSensorNames(Path_).GetName(fan));
    }

    [Fact]
    public void An_unnamed_sensor_shows_what_the_hardware_calls_it()
    {
        var names = new JsonSensorNames(Path_);

        Assert.Equal("System Fan #4", names.Resolve(SensorId.New(), "System Fan #4"));
    }

    [Fact]
    public void Clearing_a_name_restores_the_hardware_s_own()
    {
        // An empty field is an undo, not a name. A fan labelled with nothing at all is worse than
        // one labelled "System Fan #4", so blank has to mean removal.
        var fan = SensorId.New();
        var names = new JsonSensorNames(Path_);

        names.SetName(fan, "Seat blower");

        Assert.True(names.SetName(fan, "   "));
        Assert.Null(names.GetName(fan));
        Assert.Equal("System Fan #4", names.Resolve(fan, "System Fan #4"));
    }

    [Fact]
    public void Setting_the_same_name_twice_changes_nothing()
    {
        // The engine announces a change to every client on this, and a rename that announced itself
        // when nothing moved would refresh every window for no reason.
        var fan = SensorId.New();
        var names = new JsonSensorNames(Path_);

        Assert.True(names.SetName(fan, "Seat blower"));
        Assert.False(names.SetName(fan, "Seat blower"));
        Assert.False(names.SetName(fan, "  Seat blower  "));
    }

    [Fact]
    public void A_name_is_trimmed_and_capped()
    {
        var fan = SensorId.New();
        var names = new JsonSensorNames(Path_);

        names.SetName(fan, new string('x', JsonSensorNames.MaxLength + 40));

        Assert.Equal(JsonSensorNames.MaxLength, names.GetName(fan)!.Length);
    }

    [Fact]
    public void An_unreadable_file_costs_the_labels_and_not_the_engine()
    {
        // The safe direction. Losing labels is annoying; throwing on startup would cost the user
        // fan control entirely.
        File.WriteAllText(Path_, "{ this is not json");

        var names = new JsonSensorNames(Path_);

        Assert.Empty(names.All);
        Assert.NotNull(names.LastError);
        Assert.Equal("CPU Fan", names.Resolve(SensorId.New(), "CPU Fan"));
    }

    [Fact]
    public void A_key_that_is_not_an_id_costs_one_label_rather_than_all_of_them()
    {
        var fan = SensorId.New();

        File.WriteAllText(Path_, $$"""
            {
              "not-a-guid": "Nonsense",
              "{{fan}}": "Seat blower"
            }
            """);

        var names = new JsonSensorNames(Path_);

        Assert.Equal("Seat blower", names.GetName(fan));
        Assert.Single(names.All);
    }

    [Fact]
    public void Seeding_never_overwrites_a_name_the_user_already_chose()
    {
        // An import brings the names from the app being left behind. If the user has since named
        // something here, that is the more recent decision and it wins.
        var mine = SensorId.New();
        var theirs = SensorId.New();
        var names = new JsonSensorNames(Path_);

        names.SetName(mine, "Seat blower");

        var added = names.Seed(new Dictionary<SensorId, string>
        {
            [mine] = "Front intake",
            [theirs] = "Radiator out",
        });

        Assert.Equal(1, added);
        Assert.Equal("Seat blower", names.GetName(mine));
        Assert.Equal("Radiator out", names.GetName(theirs));
    }

    [Fact]
    public void The_file_sits_beside_the_configurations_rather_than_among_them()
    {
        // Anything ending in .json inside that folder is offered to the user as a configuration
        // they could load, and this is machine state that must not travel with one.
        var configurations = System.IO.Path.Combine(_root, StateLocation.FolderName);
        Directory.CreateDirectory(configurations);

        var path = StateLocation.ResolveNames(configurations);
        new JsonSensorNames(path).SetName(SensorId.New(), "Seat blower");

        var store = new ConfigStore(configurations, new MigrationRunner([], currentVersion: 0));

        Assert.Empty(store.List());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Naming_a_sensor_that_is_not_one_does_nothing()
    {
        Assert.False(new JsonSensorNames(Path_).SetName(SensorId.None, "Seat blower"));
    }
}
