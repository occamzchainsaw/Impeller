namespace Impeller.Core.Abstractions;

/// <summary>
/// Translates a <see cref="HardwareFingerprint"/> into the <see cref="SensorId"/> a configuration
/// refers to, minting one the first time a fingerprint is seen.
/// </summary>
/// <remarks>
/// <para>
/// This is the hinge the whole sensor-identity design turns on. Providers describe hardware; they
/// do not name it. The map owns the synthetic ids, and because those ids are minted once and then
/// persisted, a provider reordering its enumeration between releases cannot silently repoint a
/// saved curve at a different physical fan.
/// </para>
/// <para>
/// It is deliberately append-only. An entry for hardware that is currently absent is kept, so
/// unplugging a USB fan controller and plugging it back in returns the same ids and the
/// configuration still works.
/// </para>
/// </remarks>
public interface ISensorIdentityMap
{
    /// <summary>Every known fingerprint and the id it resolves to, including absent hardware.</summary>
    IReadOnlyDictionary<HardwareFingerprint, SensorId> Entries { get; }

    /// <summary>
    /// Resolves a fingerprint to its id, minting and recording a new one if this is the first sighting.
    /// </summary>
    SensorId GetOrCreate(HardwareFingerprint fingerprint);

    /// <summary>Looks up a fingerprint without minting anything.</summary>
    bool TryGet(HardwareFingerprint fingerprint, out SensorId id);
}

/// <summary>
/// An identity map that lives only as long as the process.
/// </summary>
/// <remarks>
/// Correct but forgetful: every restart mints fresh ids, so saved configurations would not resolve.
/// Useful for tests and for a provider being probed before anything is persisted. Production uses
/// the persisted implementation.
/// </remarks>
public sealed class InMemorySensorIdentityMap : ISensorIdentityMap
{
    private readonly Dictionary<HardwareFingerprint, SensorId> _entries = [];
    private readonly Lock _gate = new();

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
}
