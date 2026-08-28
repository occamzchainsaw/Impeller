using System.Text.Json.Nodes;

namespace Impeller.Core.Persistence;

/// <summary>What running migrations against a document did.</summary>
/// <param name="Applied">The migrations that ran, in order.</param>
/// <param name="FromVersion">The version the document was at.</param>
/// <param name="ToVersion">The version it is at now.</param>
public readonly record struct MigrationOutcome(
    IReadOnlyList<string> Applied,
    int FromVersion,
    int ToVersion)
{
    /// <summary>Whether anything was changed.</summary>
    public bool Changed => Applied.Count > 0;
}

/// <summary>
/// Brings a configuration document up to the current schema version.
/// </summary>
/// <remarks>
/// Migrations form a chain: each advances one version, and the runner applies as many as the
/// document is behind. A document from the future — written by a newer build — is refused rather
/// than guessed at, because silently loading a config whose fields we do not understand risks
/// discarding the parts we cannot see when it is next saved.
/// </remarks>
public sealed class MigrationRunner
{
    /// <summary>The property that carries the document's schema version.</summary>
    public const string VersionProperty = "schemaVersion";

    private readonly Dictionary<int, IConfigMigration> _byFromVersion;

    /// <param name="migrations">
    /// The available migrations. Two migrations sharing a <see cref="IConfigMigration.FromVersion"/>
    /// is a programming error, since the chain would be ambiguous.
    /// </param>
    /// <param name="currentVersion">The schema version this build writes.</param>
    public MigrationRunner(IEnumerable<IConfigMigration> migrations, int currentVersion)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentOutOfRangeException.ThrowIfNegative(currentVersion);

        _byFromVersion = [];
        CurrentVersion = currentVersion;

        foreach (var migration in migrations)
        {
            if (!_byFromVersion.TryAdd(migration.FromVersion, migration))
            {
                throw new ArgumentException(
                    $"Two migrations both claim to upgrade from version {migration.FromVersion}: " +
                    $"'{_byFromVersion[migration.FromVersion].Description}' and '{migration.Description}'.",
                    nameof(migrations));
            }
        }
    }

    /// <summary>The schema version this build writes.</summary>
    public int CurrentVersion { get; }

    /// <summary>
    /// Reads a document's version. A document with no version property is treated as version 0,
    /// which is how a config written before versioning existed is recognised.
    /// </summary>
    public static int ReadVersion(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document.TryGetPropertyValue(VersionProperty, out var node)
            && node is not null
            && node.GetValueKind() == System.Text.Json.JsonValueKind.Number
                ? node.GetValue<int>()
                : 0;
    }

    /// <summary>
    /// Upgrades a document to <see cref="CurrentVersion"/>, in place.
    /// </summary>
    /// <exception cref="ConfigMigrationException">
    /// The document is newer than this build understands, or the chain has a gap so the document
    /// cannot reach the current version.
    /// </exception>
    public MigrationOutcome Run(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var from = ReadVersion(document);

        if (from > CurrentVersion)
        {
            throw new ConfigMigrationException(
                $"This configuration was written by a newer version of Impeller " +
                $"(schema {from}; this build understands {CurrentVersion}). " +
                "Update Impeller, or load a different configuration.");
        }

        var applied = new List<string>();
        var version = from;

        while (version < CurrentVersion)
        {
            if (!_byFromVersion.TryGetValue(version, out var migration))
            {
                throw new ConfigMigrationException(
                    $"No migration exists from schema version {version}, so this configuration " +
                    $"cannot be brought up to version {CurrentVersion}.");
            }

            migration.Apply(document);
            applied.Add(migration.Description);
            version++;
            document[VersionProperty] = version;
        }

        // Stamp the version even when nothing ran, so a version-less document gains one.
        document[VersionProperty] = CurrentVersion;

        return new MigrationOutcome(applied, from, CurrentVersion);
    }
}

/// <summary>Thrown when a configuration document cannot be brought to the current schema version.</summary>
public sealed class ConfigMigrationException : Exception
{
    public ConfigMigrationException()
    {
    }

    public ConfigMigrationException(string message)
        : base(message)
    {
    }

    public ConfigMigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
