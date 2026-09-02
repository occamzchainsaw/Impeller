using Impeller.Core.Abstractions;

namespace Impeller.Core.Persistence.Legacy;

/// <summary>
/// Turns an identifier string out of a legacy configuration back into something the identity map
/// can recognise.
/// </summary>
/// <remarks>
/// <para>
/// The app being imported from stores raw LibreHardwareMonitor paths — <c>/amdcpu/0/temperature/2</c>
/// — where Impeller stores a synthetic id. Bridging the two is the whole job of this class, and it
/// is pure string work: no hardware is touched and the LibreHardwareMonitor assembly is not
/// referenced. That matters because this lives in the persistence layer, which must not acquire a
/// dependency on one particular hardware backend just to read a file.
/// </para>
/// <para>
/// Vendor-specific identifiers (<c>ADLX/…</c>, <c>NVApiWrapper/…</c>) are not paths and get nothing
/// from this class beyond being recognised well enough to name in a report. They are repaired by
/// <see cref="VendorIdentifier"/>, which matches the card by name against what is present.
/// </para>
/// </remarks>
public static class LegacyIdentifier
{
    /// <summary>The provider id the LibreHardwareMonitor backend registers itself under.</summary>
    public const string LhmProviderId = "lhm";

    /// <summary>
    /// Candidate fingerprints for a stored path, best guess first.
    /// </summary>
    /// <returns>
    /// Zero candidates when the string is not a LibreHardwareMonitor path at all — a vendor
    /// identifier, a custom sensor reference, or something malformed.
    /// </returns>
    /// <remarks>
    /// More than one candidate can come back, and the reason is a rewrite the original performs on
    /// the way out. See <see cref="RestoreStrippedLpcIndex"/>; the honest response is to offer both
    /// spellings and let the identity map say which one it has actually seen.
    /// </remarks>
    public static IReadOnlyList<HardwareFingerprint> Candidates(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier[0] != '/')
        {
            return [];
        }

        var candidates = new List<HardwareFingerprint>(2);

        if (RestoreStrippedLpcIndex(identifier) is { } restored && TryParse(restored, out var first))
        {
            candidates.Add(first);
        }

        if (TryParse(identifier, out var literal) && !candidates.Contains(literal))
        {
            candidates.Add(literal);
        }

        return candidates;
    }

    /// <summary>
    /// Splits a LibreHardwareMonitor path into the hardware, channel and kind that make up a
    /// fingerprint.
    /// </summary>
    /// <remarks>
    /// The shape is <c>/&lt;hardware…&gt;/&lt;type&gt;/&lt;index&gt;</c>. Everything up to the last
    /// two segments is the hardware's own identifier, which is exactly what the backend uses as its
    /// hardware key — so the two agree without either having to know about the other.
    /// </remarks>
    public static bool TryParse(string? identifier, out HardwareFingerprint fingerprint)
    {
        fingerprint = default;

        if (string.IsNullOrWhiteSpace(identifier) || identifier[0] != '/')
        {
            return false;
        }

        var segments = identifier.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
        {
            return false;
        }

        if (!int.TryParse(segments[^1], out var channel))
        {
            return false;
        }

        var kind = ToSensorKind(segments[^2]);
        if (kind == SensorKind.Unknown)
        {
            return false;
        }

        var hardwareKey = "/" + string.Join('/', segments[..^2]);
        fingerprint = new HardwareFingerprint(LhmProviderId, hardwareKey, channel, kind);
        return true;
    }

    /// <summary>
    /// Puts back the index the original strips out of Super I/O paths.
    /// </summary>
    /// <returns>The repaired path, or <see langword="null"/> when nothing was stripped.</returns>
    /// <remarks>
    /// <para>
    /// This is the single most consequential detail in the whole import. On the way out, a path
    /// beginning <c>/lpc</c> whose chip index is exactly zero has that index deleted, so
    /// <c>/lpc/nct6687d/0/control/0</c> is stored as <c>/lpc/nct6687d/control/0</c>. Impeller's
    /// hardware key is the unmodified identifier, so a literal reading of the stored path matches
    /// nothing — and since a single-chip motherboard is the common case, that would silently fail
    /// to resolve every motherboard fan header on most machines.
    /// </para>
    /// <para>
    /// The rewrite is invertible because it only ever removes a zero: if the segment after the chip
    /// name does not parse as a number, it is a sensor type and an index was removed. A second chip
    /// at <c>/lpc/nct6687d/1/…</c> keeps its index and is left alone.
    /// </para>
    /// </remarks>
    public static string? RestoreStrippedLpcIndex(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        if (!identifier.StartsWith("/lpc", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Kept as a raw split, empty leading entry and all, so the index positions line up with the
        // original's own arithmetic rather than with a tidier version of it.
        var parts = identifier.Split('/');
        if (parts.Length <= 3 || int.TryParse(parts[3], out _))
        {
            return null;
        }

        return string.Join('/', parts[..3].Append("0").Concat(parts[3..]));
    }

    /// <summary>
    /// Maps the type segment of a path onto what the sensor measures.
    /// </summary>
    /// <remarks>
    /// These names come from LibreHardwareMonitor's own sensor types, lowercased into the path. The
    /// mapping deliberately mirrors the one in the backend rather than calling it: this reads a
    /// string out of a file, that reads an enum off live hardware, and the file has to stay readable
    /// whether or not the backend is installed.
    /// </remarks>
    public static SensorKind ToSensorKind(string segment) => segment.ToLowerInvariant() switch
    {
        "temperature" => SensorKind.Temperature,
        "fan" => SensorKind.FanSpeed,
        "control" => SensorKind.Control,
        "load" => SensorKind.Load,
        "power" => SensorKind.Power,
        "voltage" => SensorKind.Voltage,
        "current" => SensorKind.Current,
        "clock" => SensorKind.Clock,
        "flow" => SensorKind.Flow,
        "level" => SensorKind.Level,
        "data" or "smalldata" => SensorKind.Data,
        "factor" => SensorKind.Factor,
        _ => SensorKind.Unknown,
    };

    /// <summary>
    /// Names the backend an unresolvable identifier is waiting on, so the report can say what would
    /// fix it rather than only that it failed.
    /// </summary>
    public static string? PendingBackend(string? identifier) => identifier switch
    {
        null => null,
        _ when identifier.StartsWith("ADLX/", StringComparison.OrdinalIgnoreCase) => "AMD graphics",

        // NVApiWrapper, not NvAPI: the wrapper library's name is what ends up in the stored
        // identifier, and a configuration off an NVIDIA machine spells it that way.
        _ when identifier.StartsWith("NVApiWrapper/", StringComparison.OrdinalIgnoreCase) => "NVIDIA graphics",
        _ => null,
    };
}
