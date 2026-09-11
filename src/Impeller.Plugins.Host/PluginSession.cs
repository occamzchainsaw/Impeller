using Impeller.Core.Abstractions;
using Impeller.Plugins.Abstractions;
using StreamJsonRpc;

namespace Impeller.Plugins.Host;

/// <summary>
/// One plugin's connection: what it said it was, what it is allowed to do, and what it holds.
/// </summary>
/// <remarks>
/// <para>
/// The session is the JSON-RPC target, so every call a plugin makes lands here and is checked here.
/// Two rules hold for all of them. Nothing but <see cref="HelloAsync"/> works before admission, and
/// nothing a plugin does on this connection can throw into the engine — the worst a session can do
/// to fan control is stop answering, which is what the health check is looking for.
/// </para>
/// <para>
/// Everything the engine sends the other way goes through one pump, so a plugin's notifications are
/// serialised and a flooding plugin queues behind itself rather than saturating the thread pool the
/// tick loop shares. Readings coalesce — the tick reads a duty once, so every reading between two
/// ticks except the last is redundant by construction — while everything that carries meaning
/// queues intact. Losing a fan is not a message worth dropping to make room for a temperature.
/// </para>
/// </remarks>
public sealed class PluginSession : IPluginHost, IAsyncDisposable
{
    private readonly PluginHost _host;
    private readonly Stream _stream;
    private readonly JsonRpc _rpc;
    private readonly IPluginClient _client;
    private readonly PluginHostOptions _options;
    private readonly TimeProvider _time;

    private readonly Lock _gate = new();
    private readonly Queue<Func<IPluginClient, Task>> _queue = [];
    private readonly Dictionary<SensorId, Lease> _claims = [];
    private readonly HashSet<SensorId> _subscribed = [];

    private PluginReadings? _readings;
    private bool _pumping;
    private bool _closed;
    private bool _pingInFlight;

    internal PluginSession(
        PluginHost host,
        Stream stream,
        PluginIdentity identity,
        PluginHostOptions options,
        TimeProvider timeProvider)
    {
        _host = host;
        _stream = stream;
        _options = options;
        _time = timeProvider;

        Identity = identity;
        OpenedAt = timeProvider.GetUtcNow();
        LastPingAt = OpenedAt;

        _rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            stream,
            stream,
            new SystemTextJsonFormatter { JsonSerializerOptions = PluginJson.Options }));

        _rpc.AddLocalRpcTarget<IPluginHost>(this, null);
        _client = _rpc.Attach<IPluginClient>();
        _rpc.StartListening();

        _ = EnforceHandshakeDeadlineAsync();
    }

    /// <summary>Which program and account this connection came from.</summary>
    public PluginIdentity Identity { get; }

    /// <summary>When the connection was accepted.</summary>
    public DateTimeOffset OpenedAt { get; }

    /// <summary>What the plugin said about itself, once it has said anything.</summary>
    public PluginManifest? Manifest { get; private set; }

    /// <summary>What it is currently allowed to do, once admitted.</summary>
    public PluginAdmission? Admission { get; private set; }

    /// <summary>Its manifest id, or null before the handshake.</summary>
    public string? PluginId => Manifest?.Id;

    /// <summary>Whether the handshake happened and was not a refusal.</summary>
    public bool IsAdmitted => Admission?.IsAdmitted == true;

    /// <summary>How many pings in a row have gone unanswered.</summary>
    public int MissedPings { get; private set; }

    /// <summary>When the last ping was sent.</summary>
    public DateTimeOffset LastPingAt { get; private set; }

    /// <summary>Whether a notification had to be dropped to keep the queue bounded.</summary>
    public bool HasOverflowed { get; private set; }

    /// <summary>Completes when the connection goes away, however it goes.</summary>
    public Task Completion => _rpc.Completion;

    /// <summary>The controls this session currently believes it holds.</summary>
    public IReadOnlyCollection<SensorId> Claims
    {
        get
        {
            lock (_gate)
            {
                return [.. _claims.Keys];
            }
        }
    }

    /// <summary>The sensors it asked to be sent.</summary>
    public IReadOnlyCollection<SensorId> Subscriptions
    {
        get
        {
            lock (_gate)
            {
                return [.. _subscribed];
            }
        }
    }

    // ---- IPluginHost ----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<PluginAdmission> HelloAsync(
        PluginManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        // Idempotent rather than an error. A plugin that retries its handshake after a timeout is
        // being careful, and answering a second hello with a fault would punish it for that.
        if (Manifest is not null && Admission is { } already)
        {
            return already;
        }

        Manifest = manifest;

        // The admission is recorded by the host before the session is published, so that a change
        // the user makes in the same instant cannot be overwritten by this method assigning a
        // staler answer on its way out.
        return await _host.AdmitAsync(this, manifest).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<PluginSnapshot> GetSensorsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(IsAdmitted
            ? _host.DescribeMachine(this)
            : new PluginSnapshot(_host.EngineVersion, false, [], []));

    /// <inheritdoc />
    public Task SubscribeAsync(
        IReadOnlyList<SensorRef> sensors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sensors);

        lock (_gate)
        {
            _subscribed.Clear();

            // A plugin without the read grant subscribes to nothing, silently. Refusing loudly
            // would tell a pending plugin to give up, and pending is a state it is expected to sit
            // in while the user decides.
            if (Admission?.Has(PluginCapability.ReadSensors) == true)
            {
                foreach (var sensor in sensors)
                {
                    _subscribed.Add(PluginTranslation.ToSensorId(sensor));
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AcquireOutcome> AcquireAsync(
        SensorRef control,
        TimeSpan? maxSilence = null,
        CancellationToken cancellationToken = default)
    {
        if (Admission is not { } admission || !IsAdmitted || PluginId is not { } pluginId)
        {
            return Task.FromResult(AcquireOutcome.No(
                PluginAcquireFailure.NotAdmitted,
                "This connection has not been admitted. Call HelloAsync first."));
        }

        // Checked before the engine is asked, so an ungranted control cannot be distinguished from
        // an unknown one by timing, and so the answer names the user's decision rather than the
        // engine's plumbing.
        if (!admission.MayControl(control))
        {
            return Task.FromResult(AcquireOutcome.No(
                PluginAcquireFailure.NotPermitted,
                $"'{Manifest?.DisplayName ?? pluginId}' has not been granted this fan in Impeller."));
        }

        var controlId = PluginTranslation.ToSensorId(control);
        var result = _host.Loop.TryAcquire(controlId, ControlOwnerKind.Plugin, pluginId);

        if (!result.Succeeded)
        {
            return Task.FromResult(Refusal(result));
        }

        lock (_gate)
        {
            _claims[controlId] = new Lease(maxSilence, _time.GetUtcNow());
        }

        return Task.FromResult(AcquireOutcome.Ok(pluginId));
    }

    /// <inheritdoc />
    public Task<bool> SetDutyAsync(
        SensorRef control,
        float percent,
        CancellationToken cancellationToken = default)
    {
        if (!IsAdmitted || PluginId is not { } pluginId)
        {
            return Task.FromResult(false);
        }

        var controlId = PluginTranslation.ToSensorId(control);

        // Duty clamps rather than throwing, so a plugin sending NaN or 400 gets saturation instead
        // of a fault crossing the connection.
        if (!_host.Loop.TrySetRequestedDuty(controlId, new Duty(percent), pluginId))
        {
            return Task.FromResult(false);
        }

        lock (_gate)
        {
            // The lease is what the plugin asked for, so only a write it actually owned renews it.
            if (_claims.TryGetValue(controlId, out var lease))
            {
                _claims[controlId] = lease with { LastHeard = _time.GetUtcNow() };
            }
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> ReleaseAsync(SensorRef control, CancellationToken cancellationToken = default)
    {
        if (!IsAdmitted || PluginId is not { } pluginId)
        {
            return Task.FromResult(false);
        }

        // Claimant-checked inside the registry, so this cannot free a fan another plugin holds.
        // The claim record is dropped by the ownership event rather than here, so that every route
        // out of a claim - released, revoked, failsafe, death - runs through one path.
        return Task.FromResult(_host.Ownership.Release(PluginTranslation.ToSensorId(control), pluginId));
    }

    /// <inheritdoc />
    public Task LogAsync(
        PluginLogLevel level,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (IsAdmitted && PluginId is { } pluginId)
        {
            _host.WriteLog(pluginId, level, message);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<HardwareAdmission> DeclareHardwareAsync(
        IReadOnlyList<HardwareDeclaration> hardware,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        if (!IsAdmitted || PluginId is not { } pluginId || Manifest is not { } manifest)
        {
            return Task.FromResult(new HardwareAdmission(
                ProviderOutcome.NotRequested,
                null,
                new Dictionary<string, SensorRef>(),
                new Dictionary<string, SensorRef>(),
                "This connection has not been admitted. Call HelloAsync first."));
        }

        if (!manifest.Requests.Contains(PluginCapability.ProvideHardware))
        {
            return Task.FromResult(new HardwareAdmission(
                ProviderOutcome.NotRequested,
                null,
                new Dictionary<string, SensorRef>(),
                new Dictionary<string, SensorRef>(),
                "This plugin's manifest did not request ProvideHardware."));
        }

        if (Admission?.Has(PluginCapability.ProvideHardware) != true)
        {
            return Task.FromResult(new HardwareAdmission(
                ProviderOutcome.NotPermitted,
                null,
                new Dictionary<string, SensorRef>(),
                new Dictionary<string, SensorRef>(),
                $"'{manifest.DisplayName}' has not been granted permission to provide hardware in Impeller."));
        }

        return Task.FromResult(_host.Hardware.Admit(this, pluginId, hardware));
    }

    /// <inheritdoc />
    public Task PushReadingsAsync(
        IReadOnlyList<ProvidedReading> readings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readings);

        if (IsAdmitted && PluginId is { } pluginId && Admission?.Has(PluginCapability.ProvideHardware) == true)
        {
            _host.Hardware.ApplyReadings(pluginId, readings);
        }

        return Task.CompletedTask;
    }

    // ---- the engine's side ----------------------------------------------------------------

    /// <summary>Replaces what this plugin is allowed to do, after the user changed something.</summary>
    internal void UpdateAdmission(PluginAdmission admission) => Admission = admission;

    /// <summary>Queues a notification, dropping the oldest if the plugin is not keeping up.</summary>
    internal void Post(Func<IPluginClient, Task> send)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (_queue.Count >= _options.MaxQueuedNotifications)
            {
                _queue.Dequeue();
                HasOverflowed = true;
            }

            _queue.Enqueue(send);

            if (_pumping)
            {
                return;
            }

            _pumping = true;
        }

        _ = Task.Run(PumpAsync);
    }

    /// <summary>Offers this tick's readings, replacing any the plugin has not read yet.</summary>
    internal void PostReadings(PluginReadings readings)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _readings = readings;

            if (_pumping)
            {
                return;
            }

            _pumping = true;
        }

        _ = Task.Run(PumpAsync);
    }

    /// <summary>Forgets a claim, because the control changed hands.</summary>
    internal void Forget(SensorId controlId)
    {
        lock (_gate)
        {
            _claims.Remove(controlId);
        }
    }

    /// <summary>The controls whose leases have run out, so the host can release them.</summary>
    internal IReadOnlyList<SensorId> ExpiredLeases(DateTimeOffset now)
    {
        lock (_gate)
        {
            List<SensorId>? expired = null;

            foreach (var (controlId, lease) in _claims)
            {
                // A null lease is the default and means "hold it until something changes", which is
                // right for a slider a person set and walked away from.
                if (lease.MaxSilence is { } window && now - lease.LastHeard > window)
                {
                    (expired ??= []).Add(controlId);
                }
            }

            return expired ?? (IReadOnlyList<SensorId>)[];
        }
    }

    /// <summary>Whether it is time to ask this plugin whether it is still there.</summary>
    internal bool IsPingDue(DateTimeOffset now) =>
        IsAdmitted && !_pingInFlight && now - LastPingAt >= _options.PingInterval;

    /// <summary>
    /// Asks, without waiting for the answer.
    /// </summary>
    /// <remarks>
    /// Sent directly rather than through the pump, on purpose. The pump can be stuck behind a
    /// plugin that has stopped reading its pipe, and detecting exactly that is the ping's only job —
    /// a health check queued behind the unhealthiness it is meant to find would never fire.
    /// </remarks>
    internal void BeginPing(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_closed || _pingInFlight)
            {
                return;
            }

            _pingInFlight = true;
            LastPingAt = now;
        }

        _ = PingOnceAsync();
    }

    /// <summary>Asks, and waits a short while for the answer. Used when a replacement turns up.</summary>
    internal async Task<bool> IsAnsweringAsync(TimeSpan timeout)
    {
        if (_rpc.Completion.IsCompleted)
        {
            return false;
        }

        try
        {
            return await _client.PingAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timed out, faulted, or the connection is already gone. All of them mean the same
            // thing here: this session is not answering and must not keep the id.
            return false;
        }
    }

    /// <summary>Tells the plugin the engine is going, and does not wait long.</summary>
    internal Task NotifyStoppingAsync()
    {
        try
        {
            return _client.OnEngineStoppingAsync();
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _queue.Clear();
            _readings = null;
        }

        _rpc.Dispose();

        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A stream that is already gone is the ordinary case here.
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            Func<IPluginClient, Task>? next;
            PluginReadings? readings = null;

            lock (_gate)
            {
                if (_closed)
                {
                    _pumping = false;
                    return;
                }

                if (_queue.Count > 0)
                {
                    next = _queue.Dequeue();
                }
                else if (_readings is { } pending)
                {
                    // Readings go last, so a control-lost notification never waits behind a
                    // temperature the plugin will be sent again in a second anyway.
                    next = null;
                    readings = pending;
                    _readings = null;
                }
                else
                {
                    _pumping = false;
                    return;
                }
            }

            try
            {
                if (next is not null)
                {
                    await next(_client).ConfigureAwait(false);
                }
                else
                {
                    await _client.OnReadingsAsync(readings!).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // A plugin that throws from a handler, or has gone away mid-notification, is not
                // an engine problem. The connection's own completion is what reports it leaving.
            }
        }
    }

    private async Task PingOnceAsync()
    {
        var answered = false;

        try
        {
            answered = await _client.PingAsync().WaitAsync(_options.PingTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            answered = false;
        }

        lock (_gate)
        {
            _pingInFlight = false;
            MissedPings = answered ? 0 : MissedPings + 1;
        }
    }

    /// <summary>
    /// Drops a connection that never introduced itself.
    /// </summary>
    /// <remarks>
    /// Without this, any local process can open connections and simply never speak, and the pipe's
    /// instance limit is reached by silence. The only symptom would be that plugins stop being able
    /// to connect, which reads as an engine bug rather than as the denial of service it is.
    /// </remarks>
    private async Task EnforceHandshakeDeadlineAsync()
    {
        try
        {
            await Task.Delay(_options.HandshakeDeadline, _time, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        if (Manifest is null)
        {
            await _host.DropAsync(this).ConfigureAwait(false);
        }
    }

    private AcquireOutcome Refusal(ControlAcquireResult result)
    {
        var failure = PluginTranslation.ToFailure(result.Failure);
        var holder = result.CurrentOwner is { } owner
            ? PluginTranslation.ToHolder(owner.Kind)
            : ControlHolder.Curve;

        return AcquireOutcome.No(
            failure,
            failure switch
            {
                PluginAcquireFailure.UnknownControl =>
                    "Impeller does not see a control with that reference.",

                // The user's own hold is tested first, and that order is the whole of it. A
                // manual claim carries a claimant id like any other - the literal string "shell" -
                // so an arm that only asks whether there is an id answers every manual hold with
                // "'shell' is holding this fan", which names an implementation detail at the one
                // moment the user needs to recognise themselves. It also says what to do, because
                // this is the one refusal the person reading it can lift.
                PluginAcquireFailure.AlreadyOwned when holder == ControlHolder.User =>
                    "You are driving this fan by hand in Impeller. Hand it back to its curve there "
                        + "to let this app take it.",

                // Names the holder rather than saying "something else". A user told that iracing
                // has their fan knows what to close; one told that something does, does not. The
                // display name, not the id: nobody chose to read reverse-DNS.
                PluginAcquireFailure.AlreadyOwned when result.CurrentOwner?.ClaimantId is { } who =>
                    $"'{_host.NameOf(who)}' is holding this fan.",

                PluginAcquireFailure.AlreadyOwned => "Something else is holding this fan.",

                PluginAcquireFailure.NotDriven =>
                    "This fan is not on Impeller's dashboard. Add it there and grant it again.",
                PluginAcquireFailure.EngineUnavailable =>
                    "Impeller is in a failsafe state and is granting nothing.",
                _ => "Impeller refused the claim.",
            },
            holder,
            result.CurrentOwner?.ClaimantId);
    }

    /// <summary>One claim's lease: what the plugin asked for, and when it last said anything.</summary>
    private readonly record struct Lease(TimeSpan? MaxSilence, DateTimeOffset LastHeard);
}
