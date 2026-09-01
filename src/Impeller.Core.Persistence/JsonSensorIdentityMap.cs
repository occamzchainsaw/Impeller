using System.Text.Json;
using System.Text.Json.Serialization;
using Impeller.Core.Abstractions;

namespace Impeller.Core.Persistence;

/// <summary>
/// A <see cref="ISensorIdentityMap"/> backed by a JSON file beside the configurations.
/// </summary>
/// <remarks>
/// <para>
/// Kept separate from the configuration files on purpose. The identity map is machine state, not
/// user state: copying a configuration to another PC should carry the curves, and should
/// <em>not</em> carry this machine's idea of which GUID meant which fan header.
/// </para>
/// <para>
/// Writes are append-driven and rare — only a first sighting mints anything — so saving on every
/// mint is cheap and means an unexpected termination cannot lose ids that sensors are already
/// using.
/// </para>
/// </remarks>
public sealed class JsonSensorIdentityMap : ISensorIdentityMap
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<HardwareFingerprint, SensorId> _entries = [];
    private readonly Lock _gate = new();
    private readonly string _path;

    /// <summary>Opens (or creates) an identity map at the given path.</summary>
    public JsonSensorIdentityMap(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        Load();
    }

    /// <summary>Where the map is stored.</summary>
    public string Path => _path;

    /// <inheritdoc />
    public IReadOnlyDictionary<HardwareFingerprint, SensorId> Entries
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<HardwareFingerprint, SensorId>(_entries);
            }
        }
    }

    /// <inheritdoc />
    public SensorId GetOrCreate(HardwareFingerprint fingerprint)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(fingerprint, out var existing))
            {
                return existing;
            }

            var minted = SensorId.New();
            _entries[fingerprint] = minted;
            Save();
            return minted;
        }
    }

    /// <inheritdoc />
    public bool TryGet(HardwareFingerprint fingerprint, out SensorId id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(fingerprint, out id);
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        List<Entry>? entries;

        try
        {
            entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), SerializerOptions);
        }
        catch (JsonException)
        {
            // A corrupt identity map is recoverable in a way a corrupt configuration is not: the
            // ids are regenerated and the user re-picks their sensors. Refusing to start would be
            // a worse outcome than losing the mapping, so start empty and overwrite on first mint.
            return;
        }

        if (entries is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (!SensorId.TryParse(entry.Id, out var id) || entry.HardwareKey is null)
            {
                continue;
            }

            _entries[new HardwareFingerprint(
                entry.ProviderId ?? string.Empty,
                entry.HardwareKey,
                entry.Channel,
                entry.Kind)] = id;
        }
    }

    private void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = _entries
            .Select(pair => new Entry
            {
                ProviderId = pair.Key.ProviderId,
                HardwareKey = pair.Key.HardwareKey,
                Channel = pair.Key.Channel,
                Kind = pair.Key.Kind,
                Id = pair.Value.ToString(),
            })
            .OrderBy(entry => entry.ProviderId, StringComparer.Ordinal)
            .ThenBy(entry => entry.HardwareKey, StringComparer.Ordinal)
            .ThenBy(entry => entry.Channel)
            .ToList();

        // Written via a temp file so a crash mid-write cannot leave a half-serialised map that
        // would fail to load and cost every id on the machine.
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(payload, SerializerOptions));
        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>The on-disk shape. Flat and explicit, so the file stays hand-readable.</summary>
    private sealed class Entry
    {
        public string? ProviderId { get; set; }

        public string? HardwareKey { get; set; }

        public int Channel { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter<SensorKind>))]
        public SensorKind Kind { get; set; }

        public string? Id { get; set; }
    }
}
