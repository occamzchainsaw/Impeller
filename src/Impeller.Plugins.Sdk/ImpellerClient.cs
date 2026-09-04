using System.IO.Pipes;
using Impeller.Plugins.Abstractions;
using StreamJsonRpc;

namespace Impeller.Plugins.Sdk;

/// <summary>
/// A plugin's end of the channel: connects to the engine, stays connected, and says plainly when
/// it cannot.
/// </summary>
/// <remarks>
/// <para>
/// Reconnection is most of the job. The engine is a Windows service with its own lifetime — it can
/// be stopped, updated or restarted while a plugin stays open — and none of that should require the
/// user to restart the plugin. The loop runs until <see cref="StopAsync"/>, so an application can
/// start this once and never think about connectivity again.
/// </para>
/// <para>
/// <strong>Claims are never restored for you.</strong> Not after a reconnect, not after a lease
/// lapsed, not after the failsafe cleared. A claim is a fact about a session and the session is
/// gone; whether taking the fan again is the right thing to do depends on why it was lost, which
/// is exactly what <see cref="ControlLost"/> tells you. Re-acquiring blindly on a timer is how a
/// plugin ends up fighting a failsafe.
/// </para>
/// <para>
/// Nothing here throws because the engine is unreachable. Every verb answers with the same "no"
/// it would give if the engine had refused, because a plugin cannot usefully tell the two apart
/// and should behave identically either way.
/// </para>
/// </remarks>
public sealed class ImpellerClient : IPluginClient, IAsyncDisposable
{
    private readonly PluginManifest _manifest;
    private readonly ImpellerClientOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _gate = new();

    private Task? _loop;
    private JsonRpc? _rpc;
    private IPluginHost? _engine;

    /// <summary>
    /// Builds a client for one plugin.
    /// </summary>
    /// <param name="manifest">Who this plugin is and what it would like to be allowed to do.</param>
    /// <param name="options">Timings and the optional hardware hook, or the defaults.</param>
    /// <exception cref="ArgumentException">
    /// The manifest id is not a well-formed plugin id. Thrown here, at construction, so that a
    /// plugin author finds out at their own desk rather than from a user's bug report — the engine
    /// checks again on arrival and has no reason to trust that this ever ran.
    /// </exception>
    public ImpellerClient(PluginManifest manifest, ImpellerClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!PluginId.Validate(manifest.Id, out var problem))
        {
            throw new ArgumentException(problem, nameof(manifest));
        }

        if (manifest.ProtocolVersion != PluginProtocol.CurrentVersion)
        {
            throw new ArgumentException(
                $"Set ProtocolVersion to PluginProtocol.CurrentVersion ({PluginProtocol.CurrentVersion}). "
                + "Naming a different number does not make this SDK speak it.",
                nameof(manifest));
        }

        _manifest = manifest;
        _options = options ?? new ImpellerClientOptions();
    }

    /// <summary>Raised whenever the connection's standing changes, with a sentence to show a user.</summary>
    public event EventHandler<ImpellerConnectionState>? StateChanged;

    /// <summary>Raised when the engine says what this plugin may do — at the handshake, and after.</summary>
    public event EventHandler<PluginAdmission>? AdmissionChanged;

    /// <summary>Raised when a control this plugin held is no longer its, whatever took it.</summary>
    public event EventHandler<ControlLostEventArgs>? ControlLost;

    /// <summary>Raised each tick with the sensors subscribed to and the fans granted.</summary>
    public event EventHandler<PluginReadings>? Readings;

    /// <summary>Raised when the engine says it is shutting down.</summary>
    public event EventHandler? EngineStopping;

    /// <summary>How this plugin currently stands relative to the engine.</summary>
    public ImpellerConnectionState State { get; private set; } = ImpellerConnectionState.Disconnected;

    /// <summary>Something honest to show a user about the state above.</summary>
    public string StatusMessage { get; private set; } = "Not connected to Impeller.";

    /// <summary>The engine's latest word on what this plugin may do, or null before the handshake.</summary>
    public PluginAdmission? Admission { get; private set; }

    /// <summary>Whether the engine is reachable and this plugin has been admitted.</summary>
    public bool IsReady => State == ImpellerConnectionState.Ready;

    /// <summary>Whether a specific fan may be claimed right now.</summary>
    public bool MayControl(SensorRef control) => Admission?.MayControl(control) == true;

    /// <summary>Starts connecting, and keeps connecting for as long as the client lives.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _loop ??= Task.Run(() => RunAsync(_shutdown.Token));
        }
    }

    /// <summary>Stops trying, and closes the connection if there is one.</summary>
    public async Task StopAsync()
    {
        Task? loop;

        lock (_gate)
        {
            loop = _loop;
            _loop = null;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        Detach();
    }

    /// <summary>Everything the engine can see, filtered to what this plugin was granted.</summary>
    public Task<PluginSnapshot?> GetMachineAsync(CancellationToken cancellationToken = default) =>
        CallAsync<PluginSnapshot?>(
            async engine => await engine.GetSensorsAsync(cancellationToken).ConfigureAwait(false),
            null);

    /// <summary>
    /// Asks to be sent these sensors every tick, replacing any previous subscription.
    /// </summary>
    /// <remarks>
    /// An empty list is valid and means "stop sending me readings". Subscribing to everything is
    /// possible and almost always wrong: it puts a couple of hundred values a second on the wire to
    /// tell you about three you cared about.
    /// </remarks>
    public Task SubscribeAsync(
        IReadOnlyList<SensorRef> sensors,
        CancellationToken cancellationToken = default) =>
        CallAsync(engine => engine.SubscribeAsync(sensors, cancellationToken));

    /// <summary>
    /// Claims a fan, so this plugin becomes the thing driving it.
    /// </summary>
    /// <param name="control">Which fan. Must be one the user granted.</param>
    /// <param name="maxSilence">
    /// A lease: release the fan if no duty arrives for this long. Null — the default — holds it
    /// until something changes, which is right for a slider a person set and walked away from. A
    /// value suits a plugin computing a duty on a schedule: set it to a few times your update
    /// interval and the fan recovers to its curve if your own logic wedges, without the whole
    /// session being declared dead. Only you know which kind you are.
    /// </param>
    /// <param name="cancellationToken">Cancels the call, not the claim.</param>
    public Task<AcquireOutcome> AcquireAsync(
        SensorRef control,
        TimeSpan? maxSilence = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(
            engine => engine.AcquireAsync(control, maxSilence, cancellationToken),
            AcquireOutcome.No(
                PluginAcquireFailure.EngineUnavailable,
                "Impeller is not running, or this plugin is not connected to it."));

    /// <summary>
    /// Asks for a duty on a fan this plugin holds.
    /// </summary>
    /// <remarks>
    /// What you ask for and what is written are not the same number: the engine applies the fan's
    /// configured limits, its avoided speed bands and its slew limiter on the way through. Watch
    /// <see cref="ControlSample.CommandedDuty"/> in <see cref="Readings"/> for what actually
    /// happened, and show both if you show either.
    /// </remarks>
    public Task<bool> SetDutyAsync(
        SensorRef control,
        float percent,
        CancellationToken cancellationToken = default) =>
        CallAsync(engine => engine.SetDutyAsync(control, percent, cancellationToken), false);

    /// <summary>
    /// Gives a fan back. It returns to its curve on the next tick.
    /// </summary>
    /// <remarks>
    /// Worth calling, never required: dying does the same thing. That equivalence is the whole
    /// safety argument — there is no cleanup you can fail to do, because you were never the thing
    /// writing to the fan.
    /// </remarks>
    public Task<bool> ReleaseAsync(SensorRef control, CancellationToken cancellationToken = default) =>
        CallAsync(engine => engine.ReleaseAsync(control, cancellationToken), false);

    /// <summary>Writes a line into the engine's log, under this plugin's id.</summary>
    public Task LogAsync(
        PluginLogLevel level,
        string message,
        CancellationToken cancellationToken = default) =>
        CallAsync(engine => engine.LogAsync(level, message, cancellationToken));

    /// <summary>
    /// Offers this plugin's own sensors and controls to the engine.
    /// </summary>
    /// <remarks>
    /// The wire shape is settled; the engine side is not built, and every declaration comes back
    /// as <see cref="ProviderOutcome.NotImplementedInThisBuild"/>. That is a real answer, not a
    /// refusal — nothing is wrong with your declaration or your permissions.
    /// </remarks>
    public Task<HardwareAdmission> DeclareHardwareAsync(
        IReadOnlyList<HardwareDeclaration> hardware,
        CancellationToken cancellationToken = default) =>
        CallAsync(
            engine => engine.DeclareHardwareAsync(hardware, cancellationToken),
            HardwareAdmission.NotImplemented());

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    // ---- IPluginClient: what the engine calls on us ---------------------------------------

    Task IPluginClient.OnReadingsAsync(PluginReadings readings)
    {
        Raise(() => Readings?.Invoke(this, readings));
        return Task.CompletedTask;
    }

    Task IPluginClient.OnControlLostAsync(SensorRef control, ControlLostReason reason)
    {
        Raise(() => ControlLost?.Invoke(this, new ControlLostEventArgs(control, reason)));
        return Task.CompletedTask;
    }

    Task IPluginClient.OnAdmissionChangedAsync(PluginAdmission admission)
    {
        Admission = admission;
        SetState(StateFor(admission), admission.Message);
        Raise(() => AdmissionChanged?.Invoke(this, admission));
        return Task.CompletedTask;
    }

    Task IPluginClient.OnEngineStoppingAsync()
    {
        SetState(ImpellerConnectionState.Disconnected, "Impeller is shutting down.");
        Raise(() => EngineStopping?.Invoke(this, EventArgs.Empty));
        return Task.CompletedTask;
    }

    // Answered directly and cheaply. The engine pings because an answered ping proves this
    // application's message loop is turning, which a heartbeat we pushed would not - a wedged app
    // still has a live transport thread. Doing any work here would defeat the point.
    Task<bool> IPluginClient.PingAsync() => Task.FromResult(true);

    Task IPluginClient.OnWriteRequestedAsync(SensorRef control, float percent)
    {
        _options.Hardware?.Write(control, percent);
        return Task.CompletedTask;
    }

    // ---- the loop --------------------------------------------------------------------------

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = _options.MinimumRetryDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            bool wasConnected;

            try
            {
                wasConnected = await TryConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Whatever went wrong, keep trying. A plugin that silently stops reconnecting looks
                // exactly like an engine that is down, and its user cannot tell which they have.
                SetState(
                    ImpellerConnectionState.Disconnected,
                    $"Could not reach Impeller: {ex.Message}");

                wasConnected = false;
            }

            if (wasConnected)
            {
                // Dropped after being connected: back to the shortest delay, because a service
                // restart is the likeliest cause and it will be back in a second or two.
                delay = _options.MinimumRetryDelay;
            }

            // Always waited, never skipped. A connection that is accepted and then closed
            // immediately - an engine shutting down, a host that has stopped taking plugins - would
            // otherwise send this loop straight back round with no pause at all, opening
            // connections as fast as the machine allows.
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!wasConnected)
            {
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaximumRetryDelay.Ticks));
            }
        }
    }

    /// <summary>One attempt. Returns whether it got as far as being connected.</summary>
    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        SetState(ImpellerConnectionState.Connecting, "Connecting to Impeller…");

        Stream transport;

        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(_options.ConnectTimeout);

            transport = await OpenAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            SetState(
                ImpellerConnectionState.Disconnected,
                "Impeller is not running. Fans are being controlled by whatever was set last.");

            return false;
        }

        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            transport,
            transport,
            new SystemTextJsonFormatter { JsonSerializerOptions = PluginJson.Options }));

        rpc.AddLocalRpcTarget<IPluginClient>(this, null);
        var engine = rpc.Attach<IPluginHost>();
        rpc.StartListening();

        lock (_gate)
        {
            _rpc = rpc;
            _engine = engine;
        }

        try
        {
            // Bounded, because a connection that is accepted and then never answered leaves this
            // waiting forever with no way for a user to tell that from a slow machine. The engine
            // drops a silent connection after its own deadline; this is the other side of that.
            var admission = await engine
                .HelloAsync(_manifest, cancellationToken)
                .WaitAsync(_options.HandshakeTimeout, cancellationToken)
                .ConfigureAwait(false);

            Admission = admission;
            SetState(StateFor(admission), admission.Message);
            Raise(() => AdmissionChanged?.Invoke(this, admission));

            if (!admission.IsAdmitted)
            {
                // Refused. Waiting for the connection to close on its own rather than dropping it
                // here, so a caller reading StatusMessage sees the reason before it goes.
                await rpc.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await rpc.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetState(ImpellerConnectionState.Disconnected, $"Lost the connection to Impeller: {ex.Message}");
        }
        finally
        {
            Detach();
            await transport.DisposeAsync().ConfigureAwait(false);

            // The local failsafe. The engine cannot drive hardware this plugin contributed when the
            // pipe is down - a post to a dead process writes nothing - so the only thing that can
            // put it somewhere safe is this side, right here.
            _options.Hardware?.ApplyFailsafe();
        }

        SetState(ImpellerConnectionState.Disconnected, "Disconnected from Impeller.");
        return true;
    }

    private async Task<Stream> OpenAsync(CancellationToken cancellationToken)
    {
        if (_options.Transport is { } open)
        {
            return await open(cancellationToken).ConfigureAwait(false);
        }

        var pipe = new NamedPipeClientStream(
            ".",
            PluginProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return pipe;
    }

    private void Detach()
    {
        JsonRpc? rpc;

        lock (_gate)
        {
            rpc = _rpc;
            _rpc = null;
            _engine = null;
        }

        rpc?.Dispose();
    }

    private static ImpellerConnectionState StateFor(PluginAdmission admission) => admission.State switch
    {
        PluginAdmissionState.Approved => ImpellerConnectionState.Ready,
        PluginAdmissionState.Pending => ImpellerConnectionState.AwaitingApproval,
        _ => ImpellerConnectionState.Refused,
    };

    private void SetState(ImpellerConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        Raise(() => StateChanged?.Invoke(this, state));
    }

    /// <summary>
    /// Raises an event without letting a handler's fault reach the transport.
    /// </summary>
    /// <remarks>
    /// A throwing handler here would fault the JSON-RPC dispatch that called it, which the engine
    /// reads as an unhealthy plugin and answers by taking its fans away. A bug in a plugin's UI
    /// code should not cost it its fans.
    /// </remarks>
    private static void Raise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception)
        {
            // Deliberately swallowed. See above.
        }
    }

    private async Task<T> CallAsync<T>(Func<IPluginHost, Task<T>> call, T whenUnavailable)
    {
        var engine = _engine;

        if (engine is null)
        {
            return whenUnavailable;
        }

        try
        {
            return await call(engine).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The connection dropping mid-call is ordinary, and the loop is already on it.
            return whenUnavailable;
        }
    }

    private async Task CallAsync(Func<IPluginHost, Task> call)
    {
        var engine = _engine;

        if (engine is null)
        {
            return;
        }

        try
        {
            await call(engine).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // As above.
        }
    }
}
