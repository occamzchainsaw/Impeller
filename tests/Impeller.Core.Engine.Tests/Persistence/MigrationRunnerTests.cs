using System.Text.Json.Nodes;
using Impeller.Core.Persistence;

namespace Impeller.Core.Engine.Tests.Persistence;

public class MigrationRunnerTests
{
    /// <summary>A migration that records that it ran and sets a marker property.</summary>
    private sealed class MarkerMigration(int fromVersion) : IConfigMigration
    {
        public int FromVersion { get; } = fromVersion;

        public string Description => $"marker {FromVersion}";

        public int Runs { get; private set; }

        public void Apply(JsonObject document)
        {
            Runs++;
            document[$"marker{FromVersion}"] = true;
        }
    }

    private static JsonObject Document(int? version = null)
    {
        var document = new JsonObject();
        if (version is { } v)
        {
            document[MigrationRunner.VersionProperty] = v;
        }

        return document;
    }

    [Fact]
    public void A_document_with_no_version_is_treated_as_version_zero()
    {
        Assert.Equal(0, MigrationRunner.ReadVersion(Document()));
    }

    [Fact]
    public void A_current_document_runs_nothing()
    {
        var migration = new MarkerMigration(0);
        var runner = new MigrationRunner([migration], currentVersion: 1);

        var outcome = runner.Run(Document(1));

        Assert.False(outcome.Changed);
        Assert.Equal(0, migration.Runs);
    }

    [Fact]
    public void A_document_one_version_behind_runs_one_migration()
    {
        var migration = new MarkerMigration(0);
        var runner = new MigrationRunner([migration], currentVersion: 1);
        var document = Document(0);

        var outcome = runner.Run(document);

        Assert.True(outcome.Changed);
        Assert.Equal(1, migration.Runs);
        Assert.Equal(1, MigrationRunner.ReadVersion(document));
        Assert.True(document["marker0"]!.GetValue<bool>());
    }

    [Fact]
    public void Migrations_chain_in_order_across_several_versions()
    {
        var first = new MarkerMigration(0);
        var second = new MarkerMigration(1);
        var third = new MarkerMigration(2);

        // Deliberately supplied out of order: the runner chains by version, not by position.
        var runner = new MigrationRunner([third, first, second], currentVersion: 3);
        var document = Document(0);

        var outcome = runner.Run(document);

        Assert.Equal(["marker 0", "marker 1", "marker 2"], outcome.Applied);
        Assert.Equal(3, MigrationRunner.ReadVersion(document));
    }

    [Fact]
    public void Only_the_outstanding_migrations_run()
    {
        var first = new MarkerMigration(0);
        var second = new MarkerMigration(1);
        var runner = new MigrationRunner([first, second], currentVersion: 2);

        runner.Run(Document(1));

        Assert.Equal(0, first.Runs);
        Assert.Equal(1, second.Runs);
    }

    [Fact]
    public void A_version_less_document_is_stamped_even_when_nothing_ran()
    {
        var runner = new MigrationRunner([], currentVersion: 0);
        var document = Document();

        runner.Run(document);

        Assert.Equal(0, MigrationRunner.ReadVersion(document));
        Assert.True(document.ContainsKey(MigrationRunner.VersionProperty));
    }

    [Fact]
    public void A_document_from_a_newer_build_is_refused_rather_than_guessed_at()
    {
        // Loading a config whose fields we cannot see risks discarding them on the next save.
        var runner = new MigrationRunner([], currentVersion: 2);

        var error = Assert.Throws<ConfigMigrationException>(() => runner.Run(Document(5)));
        Assert.Contains("newer version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gap_in_the_chain_is_reported_rather_than_skipped()
    {
        // Version 1 has no migration, so a version-0 document cannot reach version 3.
        var runner = new MigrationRunner([new MarkerMigration(0), new MarkerMigration(2)], currentVersion: 3);

        var error = Assert.Throws<ConfigMigrationException>(() => runner.Run(Document(0)));
        Assert.Contains("version 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_migrations_from_the_same_version_are_a_programming_error()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new MigrationRunner([new MarkerMigration(0), new MarkerMigration(0)], currentVersion: 1));

        Assert.Contains("version 0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_numeric_version_property_is_treated_as_version_zero()
    {
        // A hand-edited config should be recovered, not rejected outright.
        var document = new JsonObject { [MigrationRunner.VersionProperty] = "not a number" };

        Assert.Equal(0, MigrationRunner.ReadVersion(document));
    }
}
