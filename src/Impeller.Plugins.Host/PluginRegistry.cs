using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Plugins.Abstractions;

namespace Impeller.Plugins.Host;

/// <summary>
/// Decides what each plugin is allowed to do, and remembers the answer.
/// </summary>
/// <remarks>
/// <para>
/// The whole of the approval model lives here: who has been seen, what the user granted them, which
/// program the grant is bound to, and which of the three distinct verbs — revoke a fan, disable the
/// plugin, forget it entirely — has been applied. Nothing about pipes, connections or hardware.
/// That separation is what lets the policy be exercised as plain function calls.
/// </para>
/// <para>
/// A plugin seen for the first time is <see cref="PluginAdmissionState.Pending"/> and granted
/// nothing. It may connect, say hello, and wait. That is the only defensible default for a program
/// that has just turned up asking to control the cooling, and it is deliberately not an error: a
/// plugin that treats pending as a failure and exits leaves nothing in the list for the user to
/// approve.
/// </para>
/// <para>Thread-safe. Plugins connect on transport threads; the user changes grants on another.</para>
/// </remarks>
public sealed class PluginRegistry
{
    private readonly PluginStore _store;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PluginRecord> _records;

    /// <summary>Builds a registry over a store, loading whatever it holds.</summary>
    public PluginRegistry(PluginStore store, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _time = timeProvider;
        _records = store.Read().ToDictionary(record => record.Id, StringComparer.Ordinal);
    }

    /// <summary>
    /// Raised whenever a plugin's standing changes, so the host can tell the plugin and the shell.
    /// </summary>
    /// <remarks>
    /// A plugin that only reads its admission at the handshake will believe it still holds
    /// permissions it lost ten minutes ago, so this is not optional decoration either.
    /// </remarks>
    public event EventHandler<PluginRecord>? Changed;

    /// <summary>Every plugin the engine has seen, in the order they were first seen.</summary>
    public IReadOnlyList<PluginRecord> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _records.Values.OrderBy(record => record.FirstSeenAt)];
            }
        }
    }

    /// <summary>What is remembered about one plugin, or null if it has never been seen.</summary>
    public PluginRecord? Find(string id)
    {
        lock (_gate)
        {
            return _records.GetValueOrDefault(id);
        }
    }

    /// <summary>
    /// Answers a handshake: records that this plugin turned up, and says what it may do.
    /// </summary>
    /// <param name="manifest">What the plugin said about itself.</param>
    /// <param name="identity">Which program and account the connection came from.</param>
    /// <param name="engineVersion">This engine's version, for the answer to carry.</param>
    /// <remarks>
    /// <para>
    /// The state machine, in one place. A malformed id or a protocol mismatch is refused before
    /// anything is written, so a stranger cannot fill the state file with entries. A disabled plugin
    /// is refused and stays refused until the user enables it. A plugin whose program or account has
    /// changed since it was approved drops back to pending and is prompted for again, naming both,
    /// because a standing grant that keys off nothing but a manifest id is one any program can
    /// inherit by announcing the right string.
    /// </para>
    /// <para>
    /// Grants handed back are always the stored ones intersected with what this manifest actually
    /// requests. A plugin that has stopped asking for a capability does not keep it just because it
    /// used to have it.
    /// </para>
    /// </remarks>
    public PluginAdmission Admit(PluginManifest manifest, PluginIdentity identity, string engineVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(identity);

        if (!PluginHandshake.TryAccept(manifest, engineVersion, out var refusal))
        {
            return refusal;
        }

        PluginRecord updated;

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var existing = _records.GetValueOrDefault(manifest.Id);

            var record = (existing ?? PluginRecord.New(manifest, identity, now)) with
            {
                DisplayName = manifest.DisplayName,
                Version = manifest.Version,
                Requested = [.. manifest.Requests],
                LastSeen = identity,
                LastSeenAt = now,
            };

            // Checked after LastSeen is updated, so the comparison is against this connection.
            if (record.State == PluginAdmissionState.Approved && !record.IdentityHolds)
            {
                record = record with { State = PluginAdmissionState.Pending };
            }

            updated = record;
            _records[record.Id] = record;
            Persist();
        }

        Raise(updated);

        return updated.Enabled
            ? Build(updated, engineVersion)
            : PluginAdmission.Refused(
                PluginRefusal.Disabled,
                $"'{updated.DisplayName}' has been disabled in Impeller. Enable it there to let it connect.",
                engineVersion);
    }

    /// <summary>
    /// Approves a plugin, granting capabilities and specific fans.
    /// </summary>
    /// <remarks>
    /// Binds the approval to the program that most recently connected, which is why approving
    /// something that has never connected is refused rather than granted against nothing: an
    /// approval with no program attached is one any program inherits.
    /// </remarks>
    /// <returns>False when there is no such plugin.</returns>
    public bool Approve(
        string id,
        IEnumerable<PluginCapability> capabilities,
        IEnumerable<SensorId> controls) =>
        Update(id, record => record with
        {
            State = PluginAdmissionState.Approved,

            // Never more than the plugin asked for. A capability granted but not requested would
            // sit in the file forever, and would silently take effect the day the plugin started
            // asking for it.
            Capabilities = [.. capabilities.Distinct().Where(record.Requested.Contains)],
            Controls = [.. controls.Distinct()],
            Approved = record.LastSeen,
        });

    /// <summary>Grants one more fan to an already-approved plugin.</summary>
    public bool Grant(string id, SensorId controlId) =>
        Update(id, record => record.Controls.Contains(controlId)
            ? record
            : record with { Controls = [.. record.Controls, controlId] });

    /// <summary>
    /// Takes one fan back. The plugin stays connected and keeps whatever else it has.
    /// </summary>
    /// <remarks>
    /// The narrowest of the three verbs, and the one to reach for first. Disabling a plugin because
    /// you want one fan back is how a user ends up with a plugin they have forgotten they turned
    /// off.
    /// </remarks>
    public bool Revoke(string id, SensorId controlId) =>
        Update(id, record => record with
        {
            Controls = [.. record.Controls.Where(control => control != controlId)],
        });

    /// <summary>
    /// Turns a plugin off, or back on.
    /// </summary>
    /// <remarks>
    /// A disabled plugin is refused at the handshake and its connection closed; its grants are kept,
    /// so enabling it again does not mean configuring it again.
    /// </remarks>
    public bool SetEnabled(string id, bool enabled) =>
        Update(id, record => record with { Enabled = enabled });

    /// <summary>
    /// Drops a plugin from the record entirely, so the next connection prompts as if it were new.
    /// </summary>
    /// <remarks>
    /// The third verb, and the destructive one. Distinct from disabling: a forgotten plugin can
    /// come straight back by connecting, where a disabled one cannot until the user says so.
    /// </remarks>
    public bool Forget(string id)
    {
        PluginRecord? removed;

        lock (_gate)
        {
            if (!_records.Remove(id, out removed))
            {
                return false;
            }

            Persist();
        }

        Raise(removed with { State = PluginAdmissionState.Pending, Capabilities = [], Controls = [] });
        return true;
    }

    /// <summary>
    /// What this plugin may currently do, as an admission ready to be pushed at it.
    /// </summary>
    /// <returns>A refusal when the plugin is unknown, which is the honest answer to asking about one.</returns>
    public PluginAdmission Describe(string id, string engineVersion)
    {
        var record = Find(id);

        return record is null
            ? PluginAdmission.Refused(
                PluginRefusal.Disabled, "This plugin is not known to Impeller.", engineVersion)
            : Build(record, engineVersion);
    }

    /// <summary>Whether a plugin is allowed to claim that control right now.</summary>
    public bool MayControl(string id, SensorId controlId) => Find(id)?.MayControl(controlId) == true;

    /// <summary>Turns a record into the admission a plugin is told about.</summary>
    private static PluginAdmission Build(PluginRecord record, string engineVersion)
    {
        if (!record.Enabled)
        {
            return PluginAdmission.Refused(
                PluginRefusal.Disabled,
                $"'{record.DisplayName}' has been disabled in Impeller.",
                engineVersion);
        }

        // A pending record hands out nothing, whatever it happens to be storing. Grants are kept
        // across a drop back to pending so re-approving is one click, and this line is the only
        // thing standing between that convenience and a permission nobody granted.
        if (record.State != PluginAdmissionState.Approved)
        {
            return new PluginAdmission(
                PluginAdmissionState.Pending,
                [],
                [],
                PluginRefusal.None,
                record.Approved.IsKnown
                    ? $"'{record.DisplayName}' is now running from a different program or account, "
                        + "so its permissions need confirming again in Impeller."
                    : $"'{record.DisplayName}' is waiting to be approved in Impeller.",
                engineVersion,
                PluginProtocol.CurrentVersion);
        }

        var granted = record.Capabilities.Where(record.Requested.Contains).ToArray();

        var controls = granted.Contains(PluginCapability.ControlFans)
            ? record.Controls.Select(PluginTranslation.ToRef).ToArray()
            : [];

        return new PluginAdmission(
            PluginAdmissionState.Approved,
            granted,
            controls,
            PluginRefusal.None,
            controls.Length switch
            {
                0 when granted.Length == 0 => "Approved, with nothing granted yet.",
                0 => "Approved.",
                1 => "Approved, with one fan granted.",
                _ => $"Approved, with {controls.Length} fans granted.",
            },
            engineVersion,
            PluginProtocol.CurrentVersion);
    }

    private bool Update(string id, Func<PluginRecord, PluginRecord> change)
    {
        PluginRecord updated;

        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
            {
                return false;
            }

            updated = change(record);

            if (updated == record)
            {
                return true;
            }

            _records[id] = updated;
            Persist();
        }

        Raise(updated);
        return true;
    }

    /// <summary>Writes the whole record out. Called while holding the gate.</summary>
    private void Persist() => _store.Write(_records.Values.OrderBy(record => record.FirstSeenAt));

    // Raised outside the lock: a handler that calls back in - the host pushing an admission and
    // then reading grants to decide what to release - would otherwise deadlock or reenter.
    private void Raise(PluginRecord record) => Changed?.Invoke(this, record);
}
