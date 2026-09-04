using Impeller.Core.Abstractions;

namespace Impeller.Core.Persistence.Legacy;

/// <summary>What a vendor-backend identifier was pointing at.</summary>
public enum VendorRole
{
    /// <summary>Not recognised.</summary>
    Unknown = 0,

    /// <summary>A writable fan output.</summary>
    Control,

    /// <summary>A measured fan speed.</summary>
    FanSpeed,

    /// <summary>A temperature.</summary>
    Temperature,
}

/// <summary>A reference to something on a graphics card, as a legacy configuration spells it.</summary>
/// <param name="Vendor">The backend that minted it, for the report.</param>
/// <param name="Device">The card's name, which is the only durable part of the identifier.</param>
/// <param name="Role">What on that card it refers to.</param>
/// <param name="Detail">Which one, where the vendor distinguishes several — a temperature sensor's name.</param>
public readonly record struct VendorReference(string Vendor, string Device, VendorRole Role, string? Detail);

/// <summary>
/// Resolves graphics-card references from a legacy configuration onto sensors that are actually here.
/// </summary>
/// <remarks>
/// <para>
/// This exists instead of a vendor backend, and the reason is a measurement rather than a
/// preference. Running as LocalSystem, LibreHardwareMonitor reports the card's temperatures, its fan
/// speed <em>and</em> a writable fan control — everything a legacy ADLX configuration refers to. A
/// second backend over AMD's own SDK would add a native dependency and a second identity scheme to
/// produce sensors we already have.
/// </para>
/// <para>
/// What it cannot do is derive the new identifier from the old one: the vendor identifier names the
/// card, and the hardware path numbers it by enumeration order. So the card's own name is the hinge,
/// matched against what is on the machine now. That is a weaker key than a fingerprint, which is
/// exactly why it is used only here, once, to repair a reference during an import — and never to
/// establish identity.
/// </para>
/// </remarks>
public static class VendorIdentifier
{
    /// <summary>The prefix AMD's backend writes.</summary>
    public const string Adlx = "ADLX";

    /// <summary>
    /// The prefix NVIDIA's backend writes.
    /// </summary>
    /// <remarks>
    /// <c>NVApiWrapper</c>, not <c>NvAPI</c>. The wrapper library's name ended up in the stored
    /// identifier, and a configuration from an NVIDIA machine spells it this way.
    /// </remarks>
    public const string NvApi = "NVApiWrapper";

    /// <summary>Parses a vendor identifier, if that is what this is.</summary>
    /// <remarks>
    /// <para>The two shapes, taken from the code that writes them:</para>
    /// <para><c>ADLX/{card}/{uniqueId}/control</c>, <c>/fan</c>, or <c>/temp/{name}</c>.</para>
    /// <para><c>NVApiWrapper/{index}-{card}/control/{n}</c>, <c>/fan/{n}</c>, or <c>/sensor/{n}</c>.</para>
    /// </remarks>
    public static bool TryParse(string? identifier, out VendorReference reference)
    {
        reference = default;

        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        var parts = identifier.Split('/');

        if (parts.Length >= 4 && parts[0].Equals(Adlx, StringComparison.OrdinalIgnoreCase))
        {
            var role = parts[3].ToLowerInvariant() switch
            {
                "control" => VendorRole.Control,
                "fan" => VendorRole.FanSpeed,
                "temp" => VendorRole.Temperature,
                _ => VendorRole.Unknown,
            };

            reference = new VendorReference(
                Adlx,
                parts[1],
                role,
                role == VendorRole.Temperature && parts.Length > 4 ? parts[4] : null);

            return role != VendorRole.Unknown;
        }

        if (parts.Length >= 3 && parts[0].Equals(NvApi, StringComparison.OrdinalIgnoreCase))
        {
            var role = parts[2].ToLowerInvariant() switch
            {
                "control" => VendorRole.Control,
                "fan" => VendorRole.FanSpeed,
                "sensor" => VendorRole.Temperature,
                _ => VendorRole.Unknown,
            };

            // The card is prefixed with its enumeration index, which is the part that does not
            // survive a driver update. Only the name after it is worth matching on.
            var device = parts[1];
            var dash = device.IndexOf('-', StringComparison.Ordinal);
            if (dash >= 0)
            {
                device = device[(dash + 1)..];
            }

            reference = new VendorReference(NvApi, device, role, null);
            return role != VendorRole.Unknown;
        }

        return false;
    }

    /// <summary>
    /// Finds the sensor a vendor reference means, among the sensors that are present.
    /// </summary>
    /// <param name="reference">What the legacy configuration asked for.</param>
    /// <param name="registry">What the machine actually has.</param>
    /// <param name="ambiguous">
    /// Set when more than one sensor fitted. Two identical cards cannot be told apart by name, and
    /// the caller should say so rather than let the user assume the right one was picked.
    /// </param>
    public static ISensor? Resolve(VendorReference reference, ISensorRegistry registry, out bool ambiguous)
    {
        ArgumentNullException.ThrowIfNull(registry);

        ambiguous = false;

        if (reference.Role == VendorRole.Unknown || string.IsNullOrWhiteSpace(reference.Device))
        {
            return null;
        }

        var device = Normalize(reference.Device);

        var candidates = (reference.Role == VendorRole.Control
                ? registry.Controls.Cast<ISensor>()
                : registry.Sensors.Where(sensor => sensor.Kind == ExpectedKind(reference.Role)))
            .Where(sensor => Qualified(sensor).Contains(device, StringComparison.Ordinal))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        if (reference.Role == VendorRole.Temperature && reference.Detail is { } detail)
        {
            // Several temperatures on one card, and the two backends do not agree on what to call
            // any of them, so the match is by meaning rather than by string. A name with no
            // equivalent -- an intake sensor, say -- resolves to nothing: handing back a different
            // temperature off the same card would read plausibly, drive a curve, and be wrong.
            if (TemperatureAlias(detail) is not { } wanted)
            {
                return null;
            }

            candidates = candidates
                .Where(sensor => Qualified(sensor).Contains(wanted, StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }
        }

        ambiguous = candidates.Count > 1;
        return candidates[0];
    }

    private static SensorKind ExpectedKind(VendorRole role) => role switch
    {
        VendorRole.FanSpeed => SensorKind.FanSpeed,
        VendorRole.Temperature => SensorKind.Temperature,
        _ => SensorKind.Control,
    };

    /// <summary>
    /// What a vendor's temperature name is called by the backend that replaces it.
    /// </summary>
    /// <remarks>
    /// Intake has no entry deliberately. AMD's backend exposes it and LibreHardwareMonitor does not,
    /// so a curve reading it has no equivalent here and is reported rather than repointed at a
    /// different temperature that would read plausibly and be wrong.
    /// </remarks>
    private static string? TemperatureAlias(string detail) => detail.ToLowerInvariant() switch
    {
        "gpu" => "gpucore",
        "hotspot" => "gpuhotspot",
        "memory" => "gpumemory",
        _ => null,
    };

    /// <summary>
    /// Reduces a name to the part worth comparing.
    /// </summary>
    /// <remarks>
    /// Spaces, dashes and case all vary between what a vendor calls a card and what
    /// LibreHardwareMonitor calls it — "AMD Radeon RX 7800 XT" against
    /// "AMD Radeon RX 7800 XT - GPU Core". Dropping everything but letters and digits leaves the
    /// model number, which is the part that actually identifies the card.
    /// </remarks>
    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>
    /// A sensor's hardware and its own name, normalised together.
    /// </summary>
    /// <remarks>
    /// A legacy reference names a device - "nvidiagpu", "nct6687d" - and then a sensor on it, so
    /// the match has to see both. This used to work by accident, because the provider glued the
    /// hardware onto the front of every sensor name; now that the two are separate the join has to
    /// be made deliberately, here, rather than being reintroduced in the provider where it would
    /// reach every surface in both applications.
    /// </remarks>
    private static string Qualified(ISensor sensor) =>
        Normalize(sensor.HardwareName) + Normalize(sensor.Name);
}
