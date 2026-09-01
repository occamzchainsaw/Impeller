using System.Text.Json;
using System.Text.Json.Serialization;

namespace Impeller.Core.Abstractions.Configuration;

/// <summary>
/// The single serializer configuration used for configuration files and for the engine-to-shell
/// channel.
/// </summary>
/// <remarks>
/// Deliberately one instance rather than one per caller. A wire format that has drifted from the
/// file format is the kind of bug that only shows up once a shell of one version meets an engine
/// of another, and by then it is a support problem rather than a compile error.
/// </remarks>
public static class ImpellerJson
{
    /// <summary>Options for reading and writing configuration documents.</summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    /// <summary>The same contract without the whitespace, for the channel between processes.</summary>
    public static JsonSerializerOptions CompactOptions { get; } = Create(indented: false);

    /// <summary>Serializes a configuration to its stored form.</summary>
    public static string Serialize(ImpellerConfiguration configuration) =>
        JsonSerializer.Serialize(configuration, Options);

    /// <summary>
    /// Reads a configuration from its stored form.
    /// </summary>
    /// <exception cref="JsonException">The document is not a valid configuration.</exception>
    public static ImpellerConfiguration Deserialize(string json) =>
        JsonSerializer.Deserialize<ImpellerConfiguration>(json, Options)
        ?? throw new JsonException("The configuration document was null.");

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        WriteIndented = indented,

        // Enums by name. A configuration is read by people, and a migration that renumbers an enum
        // must not silently repoint every value that referenced it.
        Converters = { new JsonStringEnumConverter() },

        // A configuration written by an older build is missing whatever was added since; a
        // configuration written by a newer one carries fields this build has never heard of.
        // Neither is a reason to refuse to control fans, so unknown members are ignored and
        // absent ones take their defaults.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
