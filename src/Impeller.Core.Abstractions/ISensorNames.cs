namespace Impeller.Core.Abstractions;

/// <summary>
/// The names a user has given this machine's sensors and fans.
/// </summary>
/// <remarks>
/// <para>
/// "System Fan #4" says nothing about which fan it is. A person knows it as the one pointed at
/// their face, and being able to say so is the difference between a page you have to decode and one
/// you can read.
/// </para>
/// <para>
/// <strong>Machine state, not configuration.</strong> A name describes the physical machine — this
/// header runs the fan aimed at the seat — and that does not change when you switch from a quiet
/// profile to a gaming one. Storing names in a configuration would lose them on every switch and
/// carry one machine's names to another whenever a configuration was copied.
/// </para>
/// <para>
/// Over <see cref="SensorId"/> rather than over bindings, because a temperature called "Radiator
/// out" is worth exactly as much as a named fan. Nothing keys off a name: identity is the id, the
/// same way a plugin's identity is its manifest id rather than its display name, and for the same
/// reason — renaming must never break a binding.
/// </para>
/// </remarks>
public interface ISensorNames
{
    /// <summary>The name the user gave this sensor, or null if they have not named it.</summary>
    string? GetName(SensorId id);

    /// <summary>
    /// Names a sensor, or clears the name when given null or blank.
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// Clearing restores the provider's own name rather than leaving a blank, which is why an empty
    /// string is a removal and not a name. A fan labelled with nothing at all is worse than one
    /// labelled "System Fan #4".
    /// </remarks>
    bool SetName(SensorId id, string? name);

    /// <summary>Every name given, for diagnostics and for seeding from an import.</summary>
    IReadOnlyDictionary<SensorId, string> All { get; }

    /// <summary>The name to show: the user's, or the provider's when there is none.</summary>
    string Resolve(SensorId id, string providerName) => GetName(id) ?? providerName;
}
