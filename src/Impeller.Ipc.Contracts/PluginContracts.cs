using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Plugins.Abstractions;

namespace Impeller.Ipc.Contracts;

/// <summary>
/// One plugin, as the Impeller window shows it.
/// </summary>
/// <param name="Id">The manifest id everything is stored under. Never shown as a label.</param>
/// <param name="DisplayName">What to call it. Free to change; nothing keys off it.</param>
/// <param name="Version">The plugin's own version, as last seen.</param>
/// <param name="State">Whether the user has approved it.</param>
/// <param name="Enabled">Whether it is allowed to connect at all.</param>
/// <param name="Connected">Whether it is connected right now.</param>
/// <param name="Requested">What its manifest asks for.</param>
/// <param name="Granted">What the user actually granted, which is never more than was asked.</param>
/// <param name="Controls">The specific fans it may claim.</param>
/// <param name="ImagePath">The program it last connected from, for the approval prompt to name.</param>
/// <param name="UserSid">The account it last connected as.</param>
/// <param name="IdentityChanged">
/// Whether it is running from a different program or account than the one that was approved.
/// </param>
/// <param name="FirstSeenAt">When it first turned up.</param>
/// <param name="LastSeenAt">When it last did.</param>
/// <remarks>
/// <para>
/// Flattened for display rather than handing the shell the stored record, because the two answer
/// different questions. The record says what was agreed; this says what is true now — including
/// <paramref name="Connected"/> and <paramref name="IdentityChanged"/>, neither of which is stored
/// anywhere because neither survives a restart.
/// </para>
/// <para>
/// <paramref name="Requested"/> and <paramref name="Granted"/> are separate on purpose. A page that
/// showed only what was granted could not offer the user the thing the plugin is asking for, and
/// one that showed only what was requested would imply permissions nobody gave.
/// </para>
/// </remarks>
public sealed record PluginSummary(
    string Id,
    string DisplayName,
    string Version,
    PluginAdmissionState State,
    bool Enabled,
    bool Connected,
    EquatableArray<PluginCapability> Requested,
    EquatableArray<PluginCapability> Granted,
    EquatableArray<SensorId> Controls,
    string? ImagePath,
    string? UserSid,
    bool IdentityChanged,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt)
{
    /// <summary>Whether the user has anything left to decide about this plugin.</summary>
    public bool NeedsAnswer => State != PluginAdmissionState.Approved || IdentityChanged;

    /// <summary>Whether it asked for something it has not been granted.</summary>
    public bool HasUngrantedRequests => Requested.Any(capability => !Granted.Contains(capability));
}
