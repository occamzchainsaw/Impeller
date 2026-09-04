using System.Text.Json;
using Impeller.Core.Abstractions;

namespace Impeller.Core.Persistence;

/// <summary>
/// The user's names for this machine's sensors, in a JSON file beside the configurations.
/// </summary>
/// <remarks>
/// <para>
/// Beside them, never among them, for the reason Phase 1 established the hard way: anything ending
/// in <c>.json</c> inside the configurations folder is offered to the user as a configuration they
/// could load. It also has to stay out of a configuration so that switching profiles does not
/// rename every fan.
/// </para>
/// <para>
/// Longest-lived state in the product after the identity map, and the least replaceable — nobody
/// remembers which header they called "seat blower" once it is gone. So: written on every change,
/// read tolerantly, and never fatal. A file that cannot be read costs the user their labels, which
/// is annoying; one that throws on startup costs them fan control.
/// </para>
/// </remarks>
public sealed class JsonSensorNames : ISensorNames
{
    /// <summary>The longest name that will be stored, so a paste cannot bloat the file.</summary>
    public const int MaxLength = 64;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<SensorId, string> _names = [];
    private readonly Lock _gate = new();
    private readonly string _path;

    /// <summary>Opens, or creates, a name file at the given path.</summary>
    public JsonSensorNames(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        Load();
    }

    /// <summary>Whatever went wrong the last time this was read or written, or null.</summary>
    public Exception? LastError { get; private set; }

    /// <inheritdoc />
    public IReadOnlyDictionary<SensorId, string> All
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<SensorId, string>(_names);
            }
        }
    }

    /// <inheritdoc />
    public string? GetName(SensorId id)
    {
        lock (_gate)
        {
            return _names.GetValueOrDefault(id);
        }
    }

    /// <inheritdoc />
    public bool SetName(SensorId id, string? name)
    {
        if (id.IsNone)
        {
            return false;
        }

        var trimmed = name?.Trim();

        if (trimmed is { Length: > MaxLength })
        {
            trimmed = trimmed[..MaxLength];
        }

        lock (_gate)
        {
            var changed = string.IsNullOrEmpty(trimmed)
                ? _names.Remove(id)
                : !_names.TryGetValue(id, out var existing)
                    || !string.Equals(existing, trimmed, StringComparison.Ordinal);

            if (!changed)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(trimmed))
            {
                _names[id] = trimmed;
            }

            Save();
            return true;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Implemented rather than left to the interface's default, so callers holding the concrete
    /// type - which the engine does, because it also seeds - can reach it without a cast.
    /// </remarks>
    public string Resolve(SensorId id, string providerName) => GetName(id) ?? providerName;

    /// <summary>
    /// Adds names that are not already set, for seeding from an import.
    /// </summary>
    /// <remarks>
    /// Never overwrites. An import brings the names the user had in the app they are leaving; if
    /// they have since named something here, that is the more recent decision and it wins.
    /// </remarks>
    /// <returns>How many were added.</returns>
    public int Seed(IEnumerable<KeyValuePair<SensorId, string>> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var added = 0;

        lock (_gate)
        {
            foreach (var (id, name) in names)
            {
                var trimmed = name?.Trim();

                if (id.IsNone || string.IsNullOrEmpty(trimmed) || _names.ContainsKey(id))
                {
                    continue;
                }

                _names[id] = trimmed.Length > MaxLength ? trimmed[..MaxLength] : trimmed;
                added++;
            }

            if (added > 0)
            {
                Save();
            }
        }

        return added;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(_path),
                SerializerOptions);

            foreach (var (key, value) in stored ?? [])
            {
                // A hand-edited key that is not an id costs the user one label rather than all of
                // them, which is the same bargain plugins.json strikes with a bad grant.
                if (Guid.TryParse(key, out var guid) && !string.IsNullOrWhiteSpace(value))
                {
                    _names[new SensorId(guid)] = value.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LastError = ex;
            _names.Clear();
        }
    }

    /// <summary>Writes the whole map. Called while holding the gate.</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var stored = _names.ToDictionary(
                entry => entry.Key.Value.ToString(),
                entry => entry.Value);

            File.WriteAllText(_path, JsonSerializer.Serialize(stored, SerializerOptions));
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swallowed for the same reason the identity map swallows: a name that fails to save is
            // a label lost at the next restart, and throwing here would take the engine with it.
            LastError = ex;
        }
    }
}
