using System.Text.Json.Nodes;
using Impeller.Core.Persistence;

namespace Impeller.Core.Engine.Tests.Persistence;

/// <summary>
/// Gives each test its own temporary configuration folder, removed afterwards.
/// </summary>
public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "impeller-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>A migration that renames one property, standing in for a real schema change.</summary>
    private sealed class RenameMigration(int fromVersion, string from, string to) : IConfigMigration
    {
        public int FromVersion { get; } = fromVersion;

        public string Description => $"rename {from} to {to}";

        public void Apply(JsonObject document)
        {
            if (document.TryGetPropertyValue(from, out var node))
            {
                document.Remove(from);
                document[to] = node?.DeepClone();
            }
        }
    }

    private ConfigStore Build(int currentVersion = 1, params IConfigMigration[] migrations) =>
        new(_root, new MigrationRunner(migrations, currentVersion));

    [Fact]
    public void Listing_a_folder_that_does_not_exist_yet_returns_nothing()
    {
        // The normal first-run state; it should not be an error.
        Assert.Empty(Build().List());
    }

    [Fact]
    public void A_saved_configuration_can_be_loaded_back()
    {
        var store = Build();
        var document = store.CreateEmpty();
        document["fanName"] = "rear exhaust";

        store.Save("default", document);
        var (loaded, _) = store.Load("default");

        Assert.Equal("rear exhaust", loaded["fanName"]!.GetValue<string>());
    }

    [Fact]
    public void Saving_stamps_the_current_schema_version()
    {
        var store = Build(currentVersion: 4);
        store.Save("default", new JsonObject());

        var (loaded, _) = store.Load("default");

        Assert.Equal(4, MigrationRunner.ReadVersion(loaded));
    }

    [Fact]
    public void Several_named_configurations_coexist()
    {
        var store = Build();
        store.Save("quiet", store.CreateEmpty());
        store.Save("benchmark", store.CreateEmpty());

        var names = store.List().Select(entry => entry.Name).ToList();

        Assert.Equal(2, names.Count);
        Assert.Contains("quiet", names);
        Assert.Contains("benchmark", names);
    }

    [Fact]
    public void Exists_reports_whether_a_configuration_is_present()
    {
        var store = Build();
        Assert.False(store.Exists("default"));

        store.Save("default", store.CreateEmpty());

        Assert.True(store.Exists("default"));
    }

    [Fact]
    public void Deleting_removes_a_configuration_and_reports_whether_it_was_there()
    {
        var store = Build();
        store.Save("scratch", store.CreateEmpty());

        Assert.True(store.Delete("scratch"));
        Assert.False(store.Delete("scratch"));
        Assert.False(store.Exists("scratch"));
    }

    [Fact]
    public void Loading_a_missing_configuration_reports_which_one()
    {
        Assert.Throws<FileNotFoundException>(() => Build().Load("nope"));
    }

    [Fact]
    public void An_outdated_configuration_is_migrated_on_load()
    {
        var store = Build(2, new RenameMigration(1, "oldName", "newName"));

        // Write a version-1 document directly, as an older build would have left it.
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            store.PathFor("default"),
            """{ "schemaVersion": 1, "oldName": "front intake" }""");

        var (document, outcome) = store.Load("default");

        Assert.True(outcome.Changed);
        Assert.Equal(1, outcome.FromVersion);
        Assert.Equal(2, outcome.ToVersion);
        Assert.Equal("front intake", document["newName"]!.GetValue<string>());
        Assert.False(document.ContainsKey("oldName"));
    }

    [Fact]
    public void A_migration_is_persisted_so_it_only_runs_once()
    {
        var store = Build(2, new RenameMigration(1, "oldName", "newName"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            store.PathFor("default"),
            """{ "schemaVersion": 1, "oldName": "front intake" }""");

        store.Load("default");
        var (_, second) = store.Load("default");

        Assert.False(second.Changed);
    }

    [Fact]
    public void The_original_is_backed_up_before_a_migration_rewrites_it()
    {
        // The whole point: a configuration someone spent an evening tuning gets a copy that
        // predates any automatic rewriting.
        var store = Build(2, new RenameMigration(1, "oldName", "newName"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            store.PathFor("default"),
            """{ "schemaVersion": 1, "oldName": "front intake" }""");

        store.Load("default");

        var backups = Directory.GetFiles(store.BackupPath);
        Assert.Single(backups);

        var contents = File.ReadAllText(backups[0]);
        Assert.Contains("oldName", contents, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void A_current_configuration_is_not_backed_up()
    {
        var store = Build(currentVersion: 1);
        store.Save("default", store.CreateEmpty());

        store.Load("default");

        Assert.False(Directory.Exists(store.BackupPath));
    }

    [Fact]
    public void Malformed_json_is_reported_against_the_file_that_contains_it()
    {
        var store = Build();
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.PathFor("broken"), "{ not json");

        var error = Assert.Throws<ConfigMigrationException>(() => store.Load("broken"));
        Assert.Contains("broken", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_json_document_that_is_not_an_object_is_rejected()
    {
        var store = Build();
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.PathFor("array"), "[1, 2, 3]");

        Assert.Throws<ConfigMigrationException>(() => store.Load("array"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/nested")]
    [InlineData("sub\\nested")]
    public void A_name_containing_a_path_is_rejected(string name)
    {
        // Configuration names come from user input; they must not be able to reach outside the folder.
        Assert.Throws<ArgumentException>(() => Build().PathFor(name));
    }

    [Fact]
    public void Saving_over_an_existing_configuration_replaces_it_atomically()
    {
        var store = Build();
        var first = store.CreateEmpty();
        first["value"] = 1;
        store.Save("default", first);

        var second = store.CreateEmpty();
        second["value"] = 2;
        store.Save("default", second);

        var (loaded, _) = store.Load("default");
        Assert.Equal(2, loaded["value"]!.GetValue<int>());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
}
