using Impeller.Core.Abstractions;
using Impeller.Core.Engine;
using Impeller.Plugins.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Impeller.Plugins.Host;

/// <summary>
/// The engine side of the plugin channel: who is connected, what they may do, and what they hold.
/// </summary>
/// <remarks>
/// <para>
/// Built against <see cref="Stream"/> rather than a named pipe, so the whole lifecycle — handshake,
/// admission, claims, eviction, death — runs headlessly over an in-memory duplex stream against a
/// real <see cref="ControlLoop"/> and a real <see cref="ControlOwnershipRegistry"/>. The pipe is the
/// least interesting part of this and the most annoying to test.
/// </para>
/// <para>
/// <strong>At most one live session per manifest id.</strong> Everything downstream treats a
/// claimant id as an identity: two connections announcing the same one could release each other's
/// controls, set duties on them, and free them all by dying. A flat refusal would lock a user out
/// of their own plugin after it hung, so a newcomer whose id is taken causes the existing session
/// to be pinged — an answer refuses the newcomer, and silence evicts the incumbent.
/// </para>
/// <para>
/// The engine never calls into a plugin from the tick loop. <see cref="PublishTick"/> queues and
/// returns; nothing here waits on a plugin for anything except a ping, which is bounded and never
/// runs on the tick thread.
/// </para>
/// </remarks>
public sealed partial class PluginHost : IAsyncDisposable
{
    private readonly ISensorRegistry _registry;
    private readonly ISensorNames _names;
    private readonly PluginRegistry _plugins;
    private readonly PluginHostOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, PluginSession> _admitted = new(StringComparer.Ordinal);
    private readonly List<PluginSession> _unannounced = [];

    private bool _stopped;

    /// <summary>Builds a host over a running engine.</summary>
    /// <param name="registry">Where sensors and controls are read from.</param>
    /// <param name="loop">The tick loop, which decides what may be claimed and what is written.</param>
    /// <param name="ownership">Who holds what.</param>
    /// <param name="plugins">What the user has approved.</param>
    /// <param name="names">The user's own names for this machine's fans and sensors.</param>
    /// <param name="engineVersion">This engine's version, for admissions to carry.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="options">Timings, or the defaults.</param>
    /// <param name="logger">Where plugin log lines and host events go.</param>
    public PluginHost(
        ISensorRegistry registry,
        ControlLoop loop,
        ControlOwnershipRegistry ownership,
        PluginRegistry plugins,
        ISensorNames names,
        string engineVersion,
        TimeProvider timeProvider,
        PluginHostOptions? options = null,
        ILogger<PluginHost>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(plugins);

        _registry = registry;
        _names = names ?? throw new ArgumentNullException(nameof(names));
        _plugins = plugins;
        _options = options ?? new PluginHostOptions();
        _time = timeProvider;
        _logger = logger ?? NullLogger<PluginHost>.Instance;

        Loop = loop;
        Ownership = ownership;
        EngineVersion = engineVersion;

        Ownership.OwnershipChanged += OnOwnershipChanged;
        _plugins.Changed += OnRecordChanged;
    }

    /// <summary>
    /// Raised when a plugin connected or went away.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>PluginRegistry.Changed</c>, which fires when what a plugin is <em>allowed</em>
    /// to do changes. Being connected is not stored anywhere and does not survive a restart, so
    /// nothing else would ever announce it - and a Plugins page that showed a plugin as connected
    /// ten minutes after it exited would be worse than one that showed nothing.
    /// </remarks>
    public event EventHandler? SessionsChanged;

    /// <summary>The tick loop. Sessions ask it what they may claim and what to write.</summary>
    internal ControlLoop Loop { get; }

    /// <summary>Who holds what.</summary>
    internal ControlOwnershipRegistry Ownership { get; }

    /// <summary>This engine's version, as told to plugins.</summary>
    public string EngineVersion { get; }

    /// <summary>How many plugins are currently admitted.</summary>
    public int SessionCount
    {
        get
        {
            lock (_gate)
            {
                return _admitted.Count;
            }
        }
    }

    /// <summary>The admitted sessions, as a snapshot.</summary>
    public IReadOnlyList<PluginSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _admitted.Values];
            }
        }
    }

    /// <summary>The session a plugin id currently belongs to, if any.</summary>
    public PluginSession? Find(string pluginId)
    {
        lock (_gate)
        {
            return _admitted.GetValueOrDefault(pluginId);
        }
    }

    /// <summary>
    /// What to call a claimant in a sentence someone is going to read.
    /// </summary>
    /// <remarks>
    /// A plugin id is reverse-DNS and deliberately never changes, which makes it the right key and
    /// the wrong label. "com.occamzchainsaw.rigfan is holding this fan" names something the user
    /// has never seen; the display name is what they were shown when they approved it. The live
    /// session is asked first because that manifest is the current one, the stored record second
    /// because a plugin can hold nothing while disconnected but can still be worth naming, and the
    /// id last because a name is better than nothing and nothing is worse than a wrong name.
    /// </remarks>
    /// <param name="claimantId">The claimant, as the ownership registry records it.</param>
    public string NameOf(string claimantId) =>
        Find(claimantId)?.Manifest?.DisplayName
            ?? _plugins.Find(claimantId)?.DisplayName
            ?? claimantId;

    /// <summary>
    /// Takes on a new connection. It has <see cref="PluginHostOptions.HandshakeDeadline"/> to say
    /// hello.
    /// </summary>
    /// <param name="stream">The duplex stream, which the session owns from here on.</param>
    /// <param name="identity">Which program and account it came from.</param>
    /// <returns>The session, or null when the host is stopping or too many are already silent.</returns>
    public PluginSession? Accept(Stream stream, PluginIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(stream);

        lock (_gate)
        {
            if (_stopped)
            {
                return null;
            }

            // The newcomer is refused rather than an existing silent connection being dropped for
            // it. Evicting the oldest would let anyone flush out the connections that are merely
            // slow to start, which is the denial of service this cap exists to prevent, arriving
            // by a different door.
            if (_unannounced.Count >= _options.MaxUnannouncedConnections)
            {
                Log.TooManySilent(_logger, _unannounced.Count);
                return null;
            }
        }

        var session = new PluginSession(this, stream, identity, _options, _time);

        lock (_gate)
        {
            _unannounced.Add(session);
        }

        Watch(session);
        return session;
    }

    /// <summary>
    /// Answers a handshake: settles the one-session-per-id question, then asks the registry.
    /// </summary>
    internal async Task<PluginAdmission> AdmitAsync(PluginSession session, PluginManifest manifest)
    {
        // Shape and protocol first, before anything is written down, so a stranger announcing
        // nonsense cannot put an entry in the state file.
        if (!PluginHandshake.TryAccept(manifest, EngineVersion, out var malformed))
        {
            _ = DropLaterAsync(session);
            return malformed;
        }

        if (await FindLiveRivalAsync(manifest.Id).ConfigureAwait(false) is { } rival)
        {
            _ = DropLaterAsync(session);

            return PluginAdmission.Refused(
                PluginRefusal.AlreadyConnected,
                $"Another copy of '{rival}' is already connected to Impeller and is responding. "
                + "Close it before starting this one.",
                EngineVersion);
        }

        var admission = _plugins.Admit(manifest, session.Identity, EngineVersion);

        session.UpdateAdmission(admission);

        lock (_gate)
        {
            _unannounced.Remove(session);

            if (!admission.IsAdmitted)
            {
                _ = DropLaterAsync(session);
                return admission;
            }

            _admitted[manifest.Id] = session;
        }

        Log.Admitted(_logger, manifest.Id, admission.State, admission.Controls.Count);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        return admission;
    }

    /// <summary>
    /// Everything on the machine, filtered to what this plugin may see.
    /// </summary>
    /// <remarks>
    /// Controls are listed whether or not they were granted, because a plugin's settings window has
    /// to be able to offer the user a fan to pick before the user has granted it. Listing is not
    /// permission: <see cref="ControlInfo.Granted"/> says which are claimable, and claiming one that
    /// is not returns <see cref="PluginAcquireFailure.NotPermitted"/>.
    /// </remarks>
    internal PluginSnapshot DescribeMachine(PluginSession session)
    {
        var admission = session.Admission;
        var mayRead = admission?.Has(PluginCapability.ReadSensors) == true;

        var sensors = mayRead
            ? _registry.Sensors
                .Select(sensor => PluginTranslation.Describe(sensor, Named(sensor.Id, sensor.Name)))
                .ToArray()
            : [];

        var controls = _registry.Controls.Select(control => Describe(control, admission)).ToArray();

        return new PluginSnapshot(EngineVersion, Loop.IsFailsafeEngaged, sensors, controls);
    }

    /// <summary>
    /// One tick's work on the plugin channel: expire leases, check health, push readings.
    /// </summary>
    /// <remarks>
    /// Called from the tick loop and returns immediately. Everything it does is a queue write, a
    /// dictionary read, or the start of a task nobody waits on.
    /// </remarks>
    /// <param name="tick">The engine's tick number, so a plugin can notice gaps.</param>
    /// <param name="taken">When the tick ran.</param>
    public void PublishTick(long tick, DateTimeOffset taken)
    {
        foreach (var session in Sessions)
        {
            if (!session.IsAdmitted || session.PluginId is not { } pluginId)
            {
                continue;
            }

            // The plugin's own safety net, fired by the engine because the plugin cannot be trusted
            // to still be running its own timers - that being the failure the lease exists for.
            foreach (var controlId in session.ExpiredLeases(taken))
            {
                Ownership.ForceRelease(controlId, OwnershipChangeReason.LeaseExpired);
                Log.LeaseExpired(_logger, pluginId, controlId);
            }

            if (session.MissedPings >= _options.UnhealthyAfterMissedPings)
            {
                Log.Unhealthy(_logger, pluginId, session.MissedPings);
                _ = DropAsync(session);
                continue;
            }

            if (session.IsPingDue(taken))
            {
                session.BeginPing(taken);
            }

            session.PostReadings(Compose(session, tick, taken));
        }
    }

    /// <summary>Closes one session and frees whatever it held.</summary>
    internal async Task DropAsync(PluginSession session)
    {
        bool known;

        lock (_gate)
        {
            _unannounced.Remove(session);

            known = session.PluginId is { } id
                && _admitted.TryGetValue(id, out var current)
                && ReferenceEquals(current, session)
                && _admitted.Remove(id);
        }

        // Before disposal, so the fans are back on their curves whether or not anything else here
        // succeeds. A killed process and a graceful exit have to be the same event for the fans,
        // and this is the line that makes them so.
        if (known && session.PluginId is { } pluginId)
        {
            var freed = Ownership.ForceReleaseAllFrom(pluginId, OwnershipChangeReason.Unhealthy);

            if (freed > 0)
            {
                Log.Released(_logger, pluginId, freed);
            }
        }

        await session.DisposeAsync().ConfigureAwait(false);

        if (known)
        {
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Tells every plugin the engine is going, waits briefly for all of them together, and goes.
    /// </summary>
    /// <remarks>
    /// The wait is bounded across the whole set rather than per plugin, because the failure being
    /// guarded against is a service that will not stop — which is a machine that will not reboot.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        PluginSession[] sessions;

        lock (_gate)
        {
            _stopped = true;
            sessions = [.. _admitted.Values, .. _unannounced];
            _admitted.Clear();
            _unannounced.Clear();
        }

        if (sessions.Length > 0)
        {
            var notified = Task.WhenAll(sessions.Select(session => session.NotifyStoppingAsync()));

            try
            {
                await notified.WaitAsync(_options.StopGrace, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Whether they heard it changes nothing about what happens next.
            }
        }

        foreach (var session in sessions)
        {
            if (session.PluginId is { } pluginId)
            {
                Ownership.ForceReleaseAllFrom(pluginId, OwnershipChangeReason.Revoked);
            }

            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Writes a plugin's own log line into the engine's log, under its id.</summary>
    internal void WriteLog(string pluginId, PluginLogLevel level, string message)
    {
        // Truncated because it is untrusted input landing in a file the user will read, and
        // clamped to a level so a plugin cannot log at Critical and imply the engine said it.
        var text = message.Length > 2000 ? message[..2000] : message;

        Log.FromPlugin(
            _logger,
            level switch
            {
                PluginLogLevel.Debug => LogLevel.Debug,
                PluginLogLevel.Information => LogLevel.Information,
                PluginLogLevel.Warning => LogLevel.Warning,
                PluginLogLevel.Error => LogLevel.Error,
                _ => LogLevel.Information,
            },
            pluginId,
            text);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Ownership.OwnershipChanged -= OnOwnershipChanged;
        _plugins.Changed -= OnRecordChanged;

        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Decides whether an id is genuinely taken, evicting the incumbent when it is not answering.
    /// </summary>
    /// <returns>The display name of the live rival, or null when the id is free to take.</returns>
    private async Task<string?> FindLiveRivalAsync(string pluginId)
    {
        PluginSession? existing;

        lock (_gate)
        {
            existing = _admitted.GetValueOrDefault(pluginId);
        }

        if (existing is null)
        {
            return null;
        }

        // Already gone but not yet reaped: the completion continuation may not have run. Reaping
        // here rather than waiting for it is what stops a restarted plugin being told its own
        // corpse is holding the id.
        if (existing.Completion.IsCompleted)
        {
            await DropAsync(existing).ConfigureAwait(false);
            return null;
        }

        if (await existing.IsAnsweringAsync(_options.EvictionPingTimeout).ConfigureAwait(false))
        {
            return existing.Manifest?.DisplayName ?? pluginId;
        }

        // Hung, not dead. The replacement is almost always the user restarting the plugin after
        // exactly this, so the newcomer wins and the incumbent's fans go back to their curves.
        Log.Evicted(_logger, pluginId);
        await DropAsync(existing).ConfigureAwait(false);
        return null;
    }

    /// <summary>Builds one tick's readings for one plugin: what it subscribed to, and what it may hold.</summary>
    private PluginReadings Compose(PluginSession session, long tick, DateTimeOffset taken)
    {
        var sensors = session.Subscriptions
            .Select(id => new SensorSample(PluginTranslation.ToRef(id), _registry.GetValue(id)))
            .ToArray();

        // Every granted control, subscribed or not. Losing track of a fan you are driving is not
        // something a plugin should have to remember to ask for.
        var granted = session.Admission?.Controls ?? [];

        var controls = granted
            .Select(reference => Sample(PluginTranslation.ToSensorId(reference)))
            .ToArray();

        return new PluginReadings(tick, taken, sensors, controls);
    }

    /// <summary>What to call something: the user's name for it, or the provider's.</summary>
    /// <remarks>
    /// Plugins get the user's names for free. A window that says "Seat blower" while the app
    /// driving that fan says "System Fan #4" is two programs disagreeing about the same object in
    /// front of the person who named it.
    /// </remarks>
    private string Named(SensorId id, string providerName) => _names.Resolve(id, providerName);

    private ControlSample Sample(SensorId controlId)
    {
        var owner = Ownership.GetOwner(controlId);

        return new ControlSample(
            PluginTranslation.ToRef(controlId),
            Loop.GetRequestedDuty(controlId)?.Percent,
            Loop.GetCommandedDuty(controlId)?.Percent,
            PluginTranslation.ToHolder(owner.Kind),
            owner.ClaimantId);
    }

    private ControlInfo Describe(IControl control, PluginAdmission? admission)
    {
        var owner = Ownership.GetOwner(control.Id);
        var reference = PluginTranslation.ToRef(control.Id);

        return new ControlInfo(
            reference,
            Named(control.Id, control.Name),
            control.HardwareName,
            control.Fingerprint.ProviderId,
            control.Fingerprint.ToString(),
            PluginTranslation.ToRef(Loop.GetPairedFanSensor(control.Id)),
            Loop.GetRequestedDuty(control.Id)?.Percent,
            Loop.GetCommandedDuty(control.Id)?.Percent,
            PluginTranslation.ToHolder(owner.Kind),
            owner.ClaimantId,
            admission?.MayControl(reference) == true,
            Loop.CanBeHeldByPlugin(control.Id));
    }

    /// <summary>
    /// Tells a plugin when a control it held changes hands, whatever took it.
    /// </summary>
    /// <remarks>
    /// Every route out of a claim passes through here — released, revoked, taken by the user, lease
    /// expired, configuration changed, failsafe — which is why the session's claim record is
    /// cleared here rather than at each of those call sites. Without this notification a plugin
    /// whose fan the failsafe took goes on showing the duty it last asked for while the fan runs at
    /// full speed, and has no way to tell the difference.
    /// </remarks>
    private void OnOwnershipChanged(object? sender, ControlOwnershipChange change)
    {
        if (change.Previous.Kind != ControlOwnerKind.Plugin
            || change.Previous.ClaimantId is not { } pluginId)
        {
            return;
        }

        // A plugin re-taking its own control has not lost anything.
        if (change.Current.Kind == ControlOwnerKind.Plugin
            && string.Equals(change.Current.ClaimantId, pluginId, StringComparison.Ordinal))
        {
            return;
        }

        if (Find(pluginId) is not { } session)
        {
            return;
        }

        session.Forget(change.ControlId);
        session.Post(client => client.OnControlLostAsync(
            PluginTranslation.ToRef(change.ControlId),
            PluginTranslation.ToLostReason(change.Reason)));
    }

    /// <summary>
    /// Applies a change the user made on the Plugins page to the live session.
    /// </summary>
    /// <remarks>
    /// The admission a plugin got at its handshake goes stale the moment someone opens that page,
    /// so it is pushed again, and anything it is no longer allowed to hold is taken back rather
    /// than left standing until it next asks.
    /// </remarks>
    private void OnRecordChanged(object? sender, PluginRecord record)
    {
        if (Find(record.Id) is not { } session)
        {
            return;
        }

        var admission = _plugins.Describe(record.Id, EngineVersion);

        session.UpdateAdmission(admission);
        session.Post(client => client.OnAdmissionChangedAsync(admission));

        foreach (var controlId in session.Claims)
        {
            if (!admission.MayControl(PluginTranslation.ToRef(controlId)))
            {
                Ownership.ForceRelease(
                    controlId,
                    record.Enabled ? OwnershipChangeReason.Revoked : OwnershipChangeReason.PluginDisabled);
            }
        }

        // A disabled plugin is refused at the handshake, so leaving its connection open would mean
        // a plugin that is disabled everywhere except where it currently matters.
        if (!record.Enabled)
        {
            _ = DropAsync(session);
        }
    }

    /// <summary>Reaps the session when the far end goes away, however it goes.</summary>
    private void Watch(PluginSession session) =>
        _ = session.Completion.ContinueWith(
            _ => DropAsync(session),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Closes a refused connection without making the refusal itself fail to arrive.</summary>
    /// <remarks>
    /// The refusal is the return value of the call this runs inside, and it has not been written to
    /// the wire yet. Disposing here - even after a yield - cancels the response, and the plugin
    /// author sees a lost connection rather than the sentence naming what is wrong with their
    /// manifest. Every carefully worded refusal in <see cref="PluginHandshake"/> depends on this
    /// waiting.
    /// </remarks>
    private async Task DropLaterAsync(PluginSession session)
    {
        try
        {
            await Task.Delay(_options.RefusedLinger, _time, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fall through and close it anyway.
        }

        await DropAsync(session).ConfigureAwait(false);
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 40,
            Level = LogLevel.Information,
            Message = "Plugin {PluginId} admitted as {State} with {ControlCount} fans granted.")]
        public static partial void Admitted(
            ILogger logger,
            string pluginId,
            PluginAdmissionState state,
            int controlCount);

        [LoggerMessage(
            EventId = 41,
            Level = LogLevel.Warning,
            Message = "Plugin {PluginId} stopped answering after {Missed} pings; its fans are back on their curves.")]
        public static partial void Unhealthy(ILogger logger, string pluginId, int missed);

        [LoggerMessage(
            EventId = 42,
            Level = LogLevel.Information,
            Message = "Released {Count} fans held by plugin {PluginId}.")]
        public static partial void Released(ILogger logger, string pluginId, int count);

        [LoggerMessage(
            EventId = 43,
            Level = LogLevel.Information,
            Message = "Plugin {PluginId} was not answering, so a new connection took its place.")]
        public static partial void Evicted(ILogger logger, string pluginId);

        [LoggerMessage(
            EventId = 44,
            Level = LogLevel.Information,
            Message = "Plugin {PluginId} went quiet on {ControlId}, so its lease lapsed and the fan is back on its curve.")]
        public static partial void LeaseExpired(ILogger logger, string pluginId, SensorId controlId);

        [LoggerMessage(
            EventId = 45,
            Level = LogLevel.Warning,
            Message = "Refused a plugin connection: {Count} others have connected without saying hello.")]
        public static partial void TooManySilent(ILogger logger, int count);

        [LoggerMessage(EventId = 46, Message = "[{PluginId}] {Text}")]
        public static partial void FromPlugin(ILogger logger, LogLevel level, string pluginId, string text);
    }
}
