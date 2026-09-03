using System.Text.Json.Serialization;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Plugins.Abstractions;

namespace Impeller.Plugins.Host;

/// <summary>
/// Which program an approval is bound to.
/// </summary>
/// <param name="ImagePath">The executable, as the operating system reports it.</param>
/// <param name="UserSid">The account it runs as.</param>
/// <remarks>
/// <para>
/// The plugin channel turns a one-time approval into a standing grant, and a standing grant that
/// keys off nothing but a manifest id is a grant any program can inherit by announcing the right
/// string. Binding it to the program and the account means inheriting it requires being that
/// program, run by that person.
/// </para>
/// <para>
/// Stated plainly, as the engine's own pipe does for its threat model: this is <em>not</em> a
/// boundary against a same-user adversary. Mapping a pipe client to a path has a reuse race,
/// anyone who can write to the plugin's install directory wins, and anyone who can inject into the
/// approved process wins. It is a boundary against <em>silent</em> inheritance, and the approval
/// prompt must not imply more than that.
/// </para>
/// <para>
/// Hashing the executable was considered and rejected: re-prompting on every plugin update trains
/// the user to click yes, which destroys the value of every prompt after it.
/// </para>
/// </remarks>
public sealed record PluginIdentity(string? ImagePath, string? UserSid)
{
    /// <summary>A connection whose program could not be identified.</summary>
    public static PluginIdentity Unknown { get; } = new(null, null);

    /// <summary>Whether anything is known about this connection at all.</summary>
    [JsonIgnore]
    public bool IsKnown => !string.IsNullOrEmpty(ImagePath) || !string.IsNullOrEmpty(UserSid);

    /// <summary>
    /// Whether an approval bound to <paramref name="approved"/> covers this connection.
    /// </summary>
    /// <remarks>
    /// An approval bound to nothing covers everything, which is the state of a record written
    /// before the engine could identify clients, or on a system where it cannot. An approval bound
    /// to something is not covered by a connection we know nothing about: that direction fails
    /// closed, and the cost of being wrong is a prompt rather than a lock-out.
    /// </remarks>
    public bool Covers(PluginIdentity approved)
    {
        ArgumentNullException.ThrowIfNull(approved);

        if (!approved.IsKnown)
        {
            return true;
        }

        return PathsMatch(approved.ImagePath, ImagePath)
            && string.Equals(approved.UserSid, UserSid, StringComparison.Ordinal);
    }

    // Windows paths are case-insensitive, and a plugin launched from a shortcut, a shell and a
    // scheduled task can legitimately arrive spelled three ways.
    private static bool PathsMatch(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What the engine remembers about one plugin between restarts.
/// </summary>
/// <param name="Id">The manifest id. The key everything is stored under.</param>
/// <param name="DisplayName">What it called itself last time, for the UI.</param>
/// <param name="Version">The version it last reported.</param>
/// <param name="State">Whether the user has approved it.</param>
/// <param name="Enabled">
/// Whether it is allowed to connect at all. Disabling is a separate verb from revoking a fan: a
/// disabled plugin is refused outright, where a plugin with no fans stays connected and can still
/// read.
/// </param>
/// <param name="Requested">
/// What the plugin last asked for. Kept beside what was granted so the Plugins page can show that
/// an updated plugin now wants something it does not have - otherwise a plugin that starts asking
/// to control fans is simply refused, and neither the user nor the author is told why.
/// </param>
/// <param name="Capabilities">The capabilities granted, which is never more than were requested.</param>
/// <param name="Controls">The specific fans granted. Never a wildcard.</param>
/// <param name="Approved">The program and account the approval is bound to.</param>
/// <param name="LastSeen">
/// The program and account that most recently connected. Compared against
/// <paramref name="Approved"/> so the UI can explain a re-prompt rather than just issuing one.
/// </param>
/// <param name="FirstSeenAt">When this plugin first turned up.</param>
/// <param name="LastSeenAt">When it last connected.</param>
/// <remarks>
/// <para>
/// Its collections are <see cref="EquatableArray{T}"/> rather than plain lists so that two records
/// with the same contents compare as equal. That is not cosmetic: the registry decides whether a
/// change is worth announcing by comparing the record before and against the one after, and with
/// reference equality every no-op edit - revoking a fan the plugin never had - would push a fresh
/// admission at the plugin claiming its permissions had changed.
/// </para>
/// <para>
/// Grants survive a drop back to <see cref="PluginAdmissionState.Pending"/> on purpose, so that
/// re-approving a plugin the user has already configured is one click rather than a rebuild of
/// their fan list. That is safe only because a pending record hands out nothing: the registry
/// returns empty grants for any state but <see cref="PluginAdmissionState.Approved"/>, and that is
/// the invariant to protect if this record ever grows another consumer.
/// </para>
/// </remarks>
public sealed record PluginRecord(
    string Id,
    string DisplayName,
    string Version,
    PluginAdmissionState State,
    bool Enabled,
    EquatableArray<PluginCapability> Requested,
    EquatableArray<PluginCapability> Capabilities,
    EquatableArray<SensorId> Controls,
    PluginIdentity Approved,
    PluginIdentity LastSeen,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt)
{
    /// <summary>A plugin seen for the first time: remembered, and granted nothing.</summary>
    public static PluginRecord New(PluginManifest manifest, PluginIdentity identity, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return new PluginRecord(
            manifest.Id,
            manifest.DisplayName,
            manifest.Version,
            PluginAdmissionState.Pending,
            Enabled: true,
            Requested: [.. manifest.Requests],
            Capabilities: [],
            Controls: [],
            Approved: PluginIdentity.Unknown,
            LastSeen: identity,
            FirstSeenAt: now,
            LastSeenAt: now);
    }

    /// <summary>Whether this plugin may currently claim that control.</summary>
    public bool MayControl(SensorId controlId) =>
        State == PluginAdmissionState.Approved
        && Enabled
        && Capabilities.Contains(PluginCapability.ControlFans)
        && Controls.Contains(controlId);

    /// <summary>Capabilities the plugin has asked for and has not been given.</summary>
    public IReadOnlyList<PluginCapability> Outstanding =>
        [.. Requested.Where(capability => !Capabilities.Contains(capability))];

    /// <summary>
    /// Whether the program now connecting is the one the approval was given to.
    /// </summary>
    /// <remarks>
    /// False on a record that has never been approved, which is not a problem: nothing consults
    /// this until there is an approval to honour.
    /// </remarks>
    public bool IdentityHolds => LastSeen.Covers(Approved);
}
