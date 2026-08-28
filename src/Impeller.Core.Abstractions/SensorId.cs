namespace Impeller.Core.Abstractions;

/// <summary>
/// A stable, opaque identifier for a sensor or control.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately meaningless. Configurations reference sensors <em>only</em> by
/// <see cref="SensorId"/>, never by hardware path, so that a provider reordering its
/// enumeration cannot silently repoint a saved curve at a different physical sensor.
/// </para>
/// <para>
/// Ids are minted once per distinct <see cref="HardwareFingerprint"/> and persisted in the
/// identity map. Re-enumerating the same hardware resolves the same id; genuinely new
/// hardware gets a new one.
/// </para>
/// </remarks>
public readonly record struct SensorId
{
    /// <summary>An id that refers to nothing. Used for "no sensor selected".</summary>
    public static SensorId None => default;

    /// <summary>The underlying value.</summary>
    public Guid Value { get; }

    /// <summary>Wraps an existing id value, typically when loading a saved configuration.</summary>
    public SensorId(Guid value) => Value = value;

    /// <summary>True when this id refers to nothing.</summary>
    public bool IsNone => Value == Guid.Empty;

    /// <summary>Mints a brand-new id. Called only by the identity map, on first sight of a fingerprint.</summary>
    public static SensorId New() => new(Guid.NewGuid());

    /// <summary>Parses an id from its round-trip string form.</summary>
    public static bool TryParse(string? text, out SensorId id)
    {
        if (Guid.TryParse(text, out var guid))
        {
            id = new SensorId(guid);
            return true;
        }

        id = None;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// What the engine uses to recognise a physical sensor across restarts and re-enumerations.
/// </summary>
/// <param name="ProviderId">
/// Which backend produced the sensor (for example <c>lhm</c>, or a plugin's manifest id).
/// </param>
/// <param name="HardwareKey">
/// A provider-scoped identifier for the containing device, as stable as the provider can make it —
/// ideally a serial number or device instance path rather than an enumeration index.
/// </param>
/// <param name="Channel">
/// The index of this sensor within that device. The one genuinely order-dependent component,
/// which is why it is never the sole basis for identity.
/// </param>
/// <param name="Kind">What the sensor measures, guarding against a temperature and a fan colliding on the same channel.</param>
/// <remarks>
/// Fingerprints are compared, not stored in configs. When a provider changes its enumeration
/// scheme it ships a remap so old fingerprints resolve to the ids they already had, rather than
/// the configuration needing hand-written repair.
/// </remarks>
public readonly record struct HardwareFingerprint(
    string ProviderId,
    string HardwareKey,
    int Channel,
    SensorKind Kind)
{
    /// <summary>
    /// A compact, human-legible rendering, for logs and the diagnostics view. Never persisted
    /// as identity and never parsed back.
    /// </summary>
    public override string ToString() =>
        $"{ProviderId}/{HardwareKey}/{Kind.ToString().ToLowerInvariant()}/{Channel}";
}
