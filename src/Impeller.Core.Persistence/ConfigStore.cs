using System.Text.Json;
using System.Text.Json.Nodes;

namespace Impeller.Core.Persistence;

/// <summary>A configuration file available to load.</summary>
/// <param name="Name">The name shown to the user, which is the file name without its extension.</param>
/// <param name="Path">The full path on disk.</param>
/// <param name="LastModified">When it was last written.</param>
public readonly record struct ConfigEntry(string Name, string Path, DateTimeOffset LastModified);

/// <summary>
/// Reads and writes named configuration files, migrating them on the way in.
/// </summary>
/// <remarks>
/// <para>
/// Several named configurations coexist in one folder and exactly one is current, which is worth
/// keeping from the app being replaced: a quiet profile and a benchmarking profile are a
/// reasonable thing to want, and retrofitting that later is painful.
/// </para>
/// <para>
/// Every document that a migration touches is backed up first, under <c>Backups/</c>, stamped
/// with the version it was at. Migrations are tested, but a configuration someone spent an
/// evening tuning deserves a copy that predates any automatic rewriting of it.
/// </para>
/// </remarks>
public sealed class ConfigStore
{
    private const string Extension = ".json";
    private const string BackupFolderName = "Backups";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Configs are read and hand-edited by people; a stable property order makes them
        // diffable and reviewable.
        DictionaryKeyPolicy = null,
    };

    private readonly MigrationRunner _migrations;

    /// <param name="rootPath">The folder holding the configuration files.</param>
    /// <param name="migrations">The migration chain applied on load.</param>
    public ConfigStore(string rootPath, MigrationRunner migrations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(migrations);

        RootPath = rootPath;
        _migrations = migrations;
    }

    /// <summary>The folder holding the configuration files.</summary>
    public string RootPath { get; }

    /// <summary>The folder pre-migration copies are written to.</summary>
    public string BackupPath => Path.Combine(RootPath, BackupFolderName);

    /// <summary>
    /// Every configuration available, newest first. Returns empty rather than throwing when the
    /// folder does not exist yet, which is the normal first-run state.
    /// </summary>
    public IReadOnlyList<ConfigEntry> List()
    {
        if (!Directory.Exists(RootPath))
        {
            return [];
        }

        return [.. new DirectoryInfo(RootPath)
            .EnumerateFiles("*" + Extension, SearchOption.TopDirectoryOnly)
            .Select(file => new ConfigEntry(
                Path.GetFileNameWithoutExtension(file.Name),
                file.FullName,
                file.LastWriteTimeUtc))
            .OrderByDescending(entry => entry.LastModified)];
    }

    /// <summary>Whether a configuration of this name exists.</summary>
    public bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>
    /// Loads a configuration, running any outstanding migrations against it.
    /// </summary>
    /// <param name="name">The configuration name, without extension.</param>
    /// <returns>The migrated document and what migrating it did.</returns>
    /// <exception cref="FileNotFoundException">No configuration of that name exists.</exception>
    /// <exception cref="ConfigMigrationException">The document could not be brought up to date.</exception>
    public (JsonObject Document, MigrationOutcome Outcome) Load(string name)
    {
        var path = PathFor(name);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No configuration named '{name}' exists.", path);
        }

        var text = File.ReadAllText(path);
        var document = Parse(text, path);
        var versionBefore = MigrationRunner.ReadVersion(document);

        // Copy before rewriting, not after: the point is to preserve what the user had.
        if (versionBefore < _migrations.CurrentVersion)
        {
            WriteBackup(name, versionBefore, text);
        }

        var outcome = _migrations.Run(document);

        if (outcome.Changed)
        {
            Save(name, document);
        }

        return (document, outcome);
    }

    /// <summary>
    /// Writes a configuration, stamping it with the current schema version.
    /// </summary>
    /// <remarks>
    /// Written to a temporary file and then moved into place, so an interrupted write — a crash,
    /// a power cut — cannot leave a half-written configuration where a valid one used to be.
    /// </remarks>
    public void Save(string name, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var path = PathFor(name);
        Directory.CreateDirectory(RootPath);

        document[MigrationRunner.VersionProperty] = _migrations.CurrentVersion;

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, document.ToJsonString(WriteOptions));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Deletes a configuration. Returns false if it was not there to begin with.</summary>
    public bool Delete(string name)
    {
        var path = PathFor(name);

        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Creates an empty configuration document at the current schema version, for a first run
    /// or a "new configuration" action.
    /// </summary>
    public JsonObject CreateEmpty() =>
        new() { [MigrationRunner.VersionProperty] = _migrations.CurrentVersion };

    /// <summary>The full path a named configuration lives at.</summary>
    public string PathFor(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Guard against a name escaping the configuration folder.
        var fileName = Path.GetFileName(name);
        if (!string.Equals(fileName, name, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A configuration name cannot contain a path.", nameof(name));
        }

        return Path.Combine(RootPath, name + Extension);
    }

    private static JsonObject Parse(string text, string path)
    {
        JsonNode? node;

        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ConfigMigrationException(
                $"'{path}' is not valid JSON and could not be read: {ex.Message}", ex);
        }

        return node as JsonObject
            ?? throw new ConfigMigrationException($"'{path}' does not contain a JSON object.");
    }

    private void WriteBackup(string name, int version, string originalText)
    {
        Directory.CreateDirectory(BackupPath);

        var backupName = $"{name}.v{version}.{DateTime.UtcNow:yyyyMMdd-HHmmss}{Extension}";
        File.WriteAllText(Path.Combine(BackupPath, backupName), originalText);
    }
}
