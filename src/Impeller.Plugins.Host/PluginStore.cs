using System.Text.Json;
using System.Text.Json.Serialization;
using Impeller.Core.Abstractions;
using Impeller.Plugins.Abstractions;

namespace Impeller.Plugins.Host;

/// <summary>
/// Reads and writes <c>plugins.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written the way <c>SelectedConfiguration</c> is: read-tolerant, write-swallowing, never fatal to
/// startup. The engine's job is to cool the machine, and no failure to read a preferences file is
/// worth not doing it.
/// </para>
/// <para>
/// One difference matters. An unreadable selection falls back to the default configuration, which
/// is merely inconvenient. An unreadable plugin record falls back to <em>every plugin pending and
/// granted nothing</em> — the user is prompted again, and until they answer, nothing outside the
/// engine drives a fan. That is the safe direction, and it is worth the prompts.
/// </para>
/// </remarks>
public sealed class PluginStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,

        // camelCase on the way out and case-insensitive on the way in. This file records which
        // programs may drive the fans in this machine, so someone will eventually open it, and a
        // hand-edited "id" that silently reads as nothing would cost them every grant in it.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,

        // By name, so a value inserted into an enum does not repoint every stored grant, and so
        // that someone reading the file can tell what it says.
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>The version this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Where the record is kept.</summary>
    public string Path => _path;

    /// <summary>The last failure, for a caller that wants to log it. Cleared by a success.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>
    /// Everything remembered, or nothing at all when it cannot be read.
    /// </summary>
    public IReadOnlyList<PluginRecord> Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                LastError = null;
                return [];
            }

            var document = JsonSerializer.Deserialize<StoredDocument>(File.ReadAllText(_path), Options);
            LastError = null;

            // A file from a version this build does not understand is ignored rather than guessed
            // at. Guessing here would mean handing a program permissions from a schema whose meaning
            // has changed.
            if (document is null || document.Version > CurrentVersion)
            {
                return [];
            }

            return [.. document.Plugins.Where(IsUsable).Select(Restore)];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
            or NotSupportedException or FormatException)
        {
            LastError = ex;
            return [];
        }
    }

    /// <summary>
    /// Writes the record. Returns false if it could not be written.
    /// </summary>
    /// <remarks>
    /// A failure costs the user's answer at the next restart — they are asked again — which is
    /// worth far less than the running engine that throwing would take down. Reported rather than
    /// hidden, so the host can log it and the Plugins page can say approvals are not sticking.
    /// </remarks>
    public bool Write(IEnumerable<PluginRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        try
        {
            var folder = System.IO.Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var document = new StoredDocument
            {
                Version = CurrentVersion,
                Plugins = [.. records.Select(Store)],
            };

            File.WriteAllText(_path, JsonSerializer.Serialize(document, Options));
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastError = ex;
            return false;
        }
    }

    /// <summary>
    /// Whether a stored entry is worth restoring at all.
    /// </summary>
    /// <remarks>
    /// A hand-edited or corrupted entry with no id, or one whose id would not be accepted at a
    /// handshake, can never match a connecting plugin. Dropping it keeps the file from accumulating
    /// entries that exist only to confuse the Plugins page.
    /// </remarks>
    private static bool IsUsable(StoredPlugin stored) => PluginId.IsValid(stored.Id);

    private static PluginRecord Restore(StoredPlugin stored) => new(
        stored.Id!,
        string.IsNullOrWhiteSpace(stored.DisplayName) ? stored.Id! : stored.DisplayName,
        stored.Version ?? string.Empty,
        stored.State,
        stored.Enabled,
        [.. stored.Requested],
        [.. stored.Capabilities],
        [.. stored.Controls.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => SensorId.TryParse(id, out var parsed) ? parsed : SensorId.None)
            .Where(id => !id.IsNone)],
        new PluginIdentity(stored.ApprovedImagePath, stored.ApprovedUserSid),
        new PluginIdentity(stored.LastImagePath, stored.LastUserSid),
        stored.FirstSeenAt,
        stored.LastSeenAt);

    private static StoredPlugin Store(PluginRecord record) => new()
    {
        Id = record.Id,
        DisplayName = record.DisplayName,
        Version = record.Version,
        State = record.State,
        Enabled = record.Enabled,
        Requested = [.. record.Requested],
        Capabilities = [.. record.Capabilities],
        Controls = [.. record.Controls.Select(id => id.ToString())],
        ApprovedImagePath = record.Approved.ImagePath,
        ApprovedUserSid = record.Approved.UserSid,
        LastImagePath = record.LastSeen.ImagePath,
        LastUserSid = record.LastSeen.UserSid,
        FirstSeenAt = record.FirstSeenAt,
        LastSeenAt = record.LastSeenAt,
    };

    /// <summary>The file's shape. Separate from <see cref="PluginRecord"/> so one can change without the other.</summary>
    private sealed class StoredDocument
    {
        public int Version { get; set; }

        public List<StoredPlugin> Plugins { get; set; } = [];
    }

    private sealed class StoredPlugin
    {
        public string? Id { get; set; }

        public string? DisplayName { get; set; }

        public string? Version { get; set; }

        public PluginAdmissionState State { get; set; }

        public bool Enabled { get; set; } = true;

        public List<PluginCapability> Requested { get; set; } = [];

        public List<PluginCapability> Capabilities { get; set; } = [];

        public List<string> Controls { get; set; } = [];

        public string? ApprovedImagePath { get; set; }

        public string? ApprovedUserSid { get; set; }

        public string? LastImagePath { get; set; }

        public string? LastUserSid { get; set; }

        public DateTimeOffset FirstSeenAt { get; set; }

        public DateTimeOffset LastSeenAt { get; set; }
    }
}
