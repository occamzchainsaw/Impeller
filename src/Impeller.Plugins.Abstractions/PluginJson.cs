using System.Text.Json;
using System.Text.Json.Serialization;

namespace Impeller.Plugins.Abstractions;

/// <summary>
/// The serializer contract for the plugin channel.
/// </summary>
/// <remarks>
/// <para>
/// Its own options rather than the engine's, for the same reason this assembly has its own
/// vocabulary: a plugin should not have to reference the configuration model to talk to the engine.
/// The settings are chosen to match, so a value that crosses both channels looks identical on
/// both.
/// </para>
/// <para>
/// Enums travel by name. A plugin built against version 1 that meets an engine which has inserted a
/// value into the middle of an enum must not read <c>Warning</c> where <c>Error</c> was written,
/// and by-name is the only encoding where renumbering is merely a compile-time concern.
/// </para>
/// <para>
/// Unknown members are ignored and absent ones take their defaults, which is what makes additive
/// change work in both directions: a newer plugin sends fields an older engine drops, and an older
/// plugin ignores fields a newer engine adds. Neither is a reason to refuse to control fans.
/// </para>
/// </remarks>
public static class PluginJson
{
    /// <summary>Options for the plugin channel.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
