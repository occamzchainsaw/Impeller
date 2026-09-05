namespace Impeller.Core.Abstractions;

/// <summary>
/// The provider ids that mean something outside the provider itself.
/// </summary>
/// <remarks>
/// Most provider ids are known only to the backend that mints them and to the fingerprints that
/// carry them. These are the exceptions — facts the shell needs as well as the engine — and they
/// live here so there is one spelling of each rather than a string literal at both ends.
/// </remarks>
public static class ProviderIds
{
    /// <summary>
    /// The engine's own computed sensors: mixes, averages, and anything else derived from readings
    /// rather than measured.
    /// </summary>
    /// <remarks>
    /// The shell needs this because a derived sensor is not on any hardware. Its fingerprint's
    /// hardware component is the sensor's own id, which is right for identity and wrong for
    /// grouping: keyed structurally like everything else, every computed sensor lands in a group of
    /// its own, headed by a GUID.
    /// </remarks>
    public const string Derived = "custom";
}
