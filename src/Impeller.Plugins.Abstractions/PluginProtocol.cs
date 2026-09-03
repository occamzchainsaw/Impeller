using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Impeller.Plugins.Abstractions;

/// <summary>
/// Where plugins connect, and which versions of this contract the engine speaks.
/// </summary>
/// <remarks>
/// <para>
/// A pipe of its own rather than the shell's. The shell is a person driving the engine for as long
/// as a window is open; a plugin is a program asking for a standing permission it will hold across
/// restarts. They deserve different admission rules, and a separate pipe means the engine can cut
/// every plugin off — during a failsafe, or a shutdown — without disturbing the window someone is
/// looking at.
/// </para>
/// </remarks>
public static class PluginProtocol
{
    /// <summary>The named pipe the engine listens on for plugins.</summary>
    public const string PipeName = "Impeller.Plugins";

    /// <summary>The protocol version this build speaks.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// The oldest protocol version this build will admit.
    /// </summary>
    /// <remarks>
    /// Additive change is the whole point of having a number here: a field added to a record does
    /// not move the minimum, because an older plugin simply never reads it. The minimum moves only
    /// when something an admitted plugin relies on stops being true, and moving it is a decision to
    /// break every plugin below it — never a side effect of a refactor.
    /// </remarks>
    public const int MinimumVersion = 1;

    /// <summary>
    /// How long a connection has to say hello before the engine drops it.
    /// </summary>
    /// <remarks>
    /// Without a deadline, any local process can open connections and never speak, and the pipe's
    /// instance limit is reached by silence. The only symptom would be that plugins stop being able
    /// to connect, which reads as an engine bug rather than as the denial of service it is.
    /// </remarks>
    public static TimeSpan HandshakeDeadline => TimeSpan.FromSeconds(5);

    /// <summary>Whether this build can talk to a plugin claiming that protocol version.</summary>
    public static bool IsSupported(int protocolVersion) =>
        protocolVersion >= MinimumVersion && protocolVersion <= CurrentVersion;
}

/// <summary>
/// Validation for plugin identifiers.
/// </summary>
/// <remarks>
/// <para>
/// A manifest id is the key to everything a plugin is granted, so its shape is worth being strict
/// about. Reverse DNS with at least three labels is required because it is the only namespace a
/// stranger can pick from without colliding with another stranger, and because it structurally
/// excludes the short words the engine already uses for its own claimants.
/// </para>
/// <para>
/// Enforced twice on purpose: in the SDK, so a plugin author finds out at their own desk rather
/// than from a user's bug report, and again at the handshake, because the engine has no reason to
/// trust that a connection was built with the SDK at all.
/// </para>
/// </remarks>
public static partial class PluginId
{
    /// <summary>
    /// Ids the engine keeps for itself.
    /// </summary>
    /// <remarks>
    /// The regex already excludes every one of these, since none has three labels. They are listed
    /// anyway because <c>ControlOwnershipRegistry.ManualClaimant</c> is the literal string
    /// <c>"shell"</c>, and a plugin admitted under that id could release the user's own manual pins.
    /// This is the belt to the pattern's braces: it survives someone loosening the pattern later
    /// without remembering why it was tight.
    /// </remarks>
    public static IReadOnlySet<string> Reserved { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "lhm",
            "custom",
            "shell",
            "engine",
            "impeller",
        };

    /// <summary>The longest id the engine will store, so a manifest cannot bloat the state file.</summary>
    public const int MaxLength = 128;

    /// <summary>
    /// Checks an id, and says what is wrong with it when something is.
    /// </summary>
    /// <param name="id">The candidate id.</param>
    /// <param name="problem">
    /// A sentence naming the problem, written to be shown to a plugin author unchanged.
    /// </param>
    public static bool Validate(string? id, out string problem)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            problem = "A plugin id is required.";
            return false;
        }

        if (id.Length > MaxLength)
        {
            problem = $"A plugin id may be at most {MaxLength} characters.";
            return false;
        }

        if (Reserved.Contains(id))
        {
            problem = $"'{id}' is reserved by Impeller and cannot be used as a plugin id.";
            return false;
        }

        if (!Pattern().IsMatch(id))
        {
            problem =
                $"'{id}' is not a valid plugin id. Use reverse DNS with at least three lowercase " +
                "labels, for example 'com.example.myplugin'.";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>Whether an id is valid, for callers that do not need the reason.</summary>
    public static bool IsValid(string? id) => Validate(id, out _);

    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9-]+){2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>
/// The parts of the handshake both ends agree on before any policy is involved.
/// </summary>
/// <remarks>
/// A pure function over a manifest, shared deliberately. The SDK runs it before connecting, so an
/// author finds a malformed id or a version mismatch at their own desk; the engine runs it again on
/// arrival, because it has no reason to believe the connection was built with the SDK. Having one
/// implementation means the two answers cannot disagree, and means the message an author sees while
/// developing is the same sentence a user's log will carry later.
/// </remarks>
public static class PluginHandshake
{
    /// <summary>
    /// Checks a manifest against this build's protocol.
    /// </summary>
    /// <param name="manifest">What the plugin said about itself.</param>
    /// <param name="engineVersion">The engine's version, for the refusal to carry.</param>
    /// <param name="refusal">The refusal to send back, when there is one.</param>
    /// <returns>True when the manifest is acceptable and admission becomes a question of policy.</returns>
    public static bool TryAccept(
        PluginManifest? manifest,
        string engineVersion,
        [NotNullWhen(false)] out PluginAdmission? refusal)
    {
        if (manifest is null)
        {
            refusal = PluginAdmission.Refused(
                PluginRefusal.InvalidId, "No manifest was sent.", engineVersion);
            return false;
        }

        if (!PluginId.Validate(manifest.Id, out var problem))
        {
            refusal = PluginAdmission.Refused(PluginRefusal.InvalidId, problem, engineVersion);
            return false;
        }

        if (manifest.ProtocolVersion < PluginProtocol.MinimumVersion)
        {
            refusal = PluginAdmission.Refused(
                PluginRefusal.ProtocolTooOld,
                $"This plugin speaks protocol version {manifest.ProtocolVersion}, and this engine "
                + $"requires at least {PluginProtocol.MinimumVersion}. Rebuild the plugin against a "
                + "newer Impeller SDK.",
                engineVersion);
            return false;
        }

        if (manifest.ProtocolVersion > PluginProtocol.CurrentVersion)
        {
            // Refused rather than tolerated. Letting a newer plugin proceed against an older engine
            // means a fan driven by two different sets of assumptions about what the calls mean.
            refusal = PluginAdmission.Refused(
                PluginRefusal.ProtocolTooNew,
                $"This plugin speaks protocol version {manifest.ProtocolVersion}, and this engine "
                + $"speaks {PluginProtocol.CurrentVersion}. Update Impeller.",
                engineVersion);
            return false;
        }

        refusal = null;
        return true;
    }
}
