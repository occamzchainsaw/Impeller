using System.IO.Pipes;
using CommunityToolkit.Mvvm.ComponentModel;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Impeller.App.ViewModels.Engine;

/// <summary>How the shell currently stands relative to the engine.</summary>
public enum EngineConnectionState
{
    /// <summary>Not connected, and not currently trying.</summary>
    Disconnected = 0,

    /// <summary>Trying to reach the engine.</summary>
    Connecting,

    /// <summary>Attached, receiving ticks.</summary>
    Connected,

    /// <summary>The engine is not installed on this machine at all.</summary>
    NotInstalled,

    /// <summary>
    /// The engine answered, and the two halves do not speak the same protocol.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Disconnected"/> because the next step is completely different:
    /// waiting fixes a service that is restarting and will never fix this one. Somebody has to
    /// update the half that is behind.
    /// </remarks>
    Incompatible,
}

/// <summary>
/// The shell's end of the channel: connects to the engine, stays connected, and gives up nothing
/// when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// Reconnection is the whole job here. The engine is a service with its own lifetime — it can be
/// restarted, updated, or stopped while the window stays open, and none of that should require the
/// user to restart the shell. Equally, closing the shell is not stopping the engine, and the UI has
/// to be able to say so plainly rather than implying the fans stopped with it.
/// </para>
/// <para>
/// Three failures are worth telling apart, because the useful next step differs for each: the
/// engine is not installed, it is installed but not running, or it is running and we lost the
/// connection. Only the first needs a different answer from "wait".
/// </para>
/// </remarks>
public sealed partial class EngineConnection : ObservableObject, IEngineEvents, IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// What this shell tells the engine it is.
    /// </summary>
    /// <remarks>
    /// This assembly's version rather than the executable's, because every project in the solution
    /// is stamped from one place and this layer has no executable of its own to ask.
    /// </remarks>
    private static readonly string ShellVersion =
        typeof(EngineConnection).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private readonly CancellationTokenSource _shutdown = new();

    private Task? _loop;
    private JsonRpc? _rpc;

    /// <summary>How the connection currently stands.</summary>
    [ObservableProperty]
    public partial EngineConnectionState State { get; private set; } = EngineConnectionState.Disconnected;

    /// <summary>Something honest to show the user about the state above.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = "Not connected to the engine service.";

    /// <summary>The engine, when there is one. Null whenever <see cref="State"/> is not connected.</summary>
    public IEngineControl? Engine { get; private set; }

    /// <summary>The most recent full snapshot, or null before the first connection.</summary>
    public EngineSnapshot? Snapshot { get; private set; }

    /// <summary>
    /// Answers whether the engine is installed as a service, so a first run can say so.
    /// </summary>
    /// <remarks>
    /// Supplied by the shell rather than done here: querying the service control manager is
    /// Windows-specific, and this layer is deliberately free of anything that would stop it being
    /// reused by a different front end.
    /// </remarks>
    public Func<bool>? IsEngineInstalled { get; set; }

    /// <summary>
    /// Opens the transport to the engine. Null means the named pipe, which is the only answer
    /// outside a test.
    /// </summary>
    /// <remarks>
    /// A seam rather than a design: this class builds its own <c>NamedPipeClientStream</c>, which
    /// makes the handshake and the retry backoff — the two things here most worth pinning down —
    /// impossible to test without an engine running. Supplied the same way <see cref="Dispatcher"/>
    /// and <see cref="IsEngineInstalled"/> are.
    /// </remarks>
    public Func<CancellationToken, Task<Stream>>? Connect { get; set; }

    /// <summary>
    /// Where connection attempts are recorded.
    /// </summary>
    /// <remarks>
    /// The shell's log exists mostly for this. "It says the engine is not running" is the report
    /// that arrives most often and the one the engine's own log cannot answer, because from its
    /// side nothing happened at all.
    /// </remarks>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// Where every event and property change is raised.
    /// </summary>
    /// <remarks>
    /// Ticks arrive on a transport thread, and everything downstream of them is bound to a UI that
    /// only tolerates being touched from its own. Marshalling once, here, is what keeps every view
    /// model free of the question -- and the alternative, each of them remembering to marshal, is
    /// the kind of rule that holds until the one place it does not.
    /// </remarks>
    public IUiDispatcher Dispatcher { get; set; } = ImmediateDispatcher.Instance;

    /// <summary>Raised when a fresh snapshot has been fetched, after every successful connect.</summary>
    public event EventHandler<EngineSnapshot>? SnapshotReceived;

    /// <summary>Raised on every tick the engine reports.</summary>
    public event EventHandler<TickSnapshot>? Ticked;

    /// <summary>Raised when the configuration in force changes, from any source.</summary>
    public event EventHandler<ConfigurationResult>? ConfigurationChanged;

    /// <summary>Raised when the available hardware changes and the snapshot is stale.</summary>
    public event EventHandler? HardwareChanged;

    /// <summary>Raised on each sample of a tuning run, wherever it was started from.</summary>
    public event EventHandler<TuningProgress>? TuningProgressed;

    /// <summary>A plugin appeared, went away, or had its permissions changed.</summary>
    public event EventHandler? PluginsChanged;

    /// <summary>Starts connecting, and keeps reconnecting until disposed.</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_shutdown.Token));

    /// <inheritdoc />
    public Task OnTickAsync(TickSnapshot snapshot)
    {
        Dispatcher.Post(() => Ticked?.Invoke(this, snapshot));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Anything that could change the configuration could have changed the controls, their
    /// ownership, and the list of saved configurations along with it. So the whole snapshot is
    /// re-read rather than patched from the little this event carries — one round trip, in one
    /// place, instead of every page working out for itself what a configuration change implies.
    /// </remarks>
    public Task OnConfigurationChangedAsync(ConfigurationResult result)
    {
        Dispatcher.Post(() => ConfigurationChanged?.Invoke(this, result));
        _ = RefreshAsync();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnHardwareChangedAsync()
    {
        Dispatcher.Post(() => HardwareChanged?.Invoke(this, EventArgs.Empty));
        _ = RefreshAsync();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Re-reads the snapshot and tells everyone watching.
    /// </summary>
    /// <remarks>
    /// A failure here is swallowed on purpose. The thing that makes it fail is the connection
    /// having gone, which the reconnect loop is already handling and will report properly; a
    /// second, worse account of the same fact helps nobody.
    /// </remarks>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Engine is not { } engine)
        {
            return;
        }

        try
        {
            var snapshot = await engine.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            Snapshot = snapshot;
            Dispatcher.Post(() => SnapshotReceived?.Invoke(this, snapshot));
        }
        catch (Exception ex)
            when (ex is IOException or ObjectDisposedException or RemoteRpcException or OperationCanceledException)
        {
            Log.RefreshFailed(Logger, ex);
        }
    }

    /// <inheritdoc />
    public Task OnTuningProgressAsync(TuningProgress progress)
    {
        Dispatcher.Post(() => TuningProgressed?.Invoke(this, progress));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnPluginsChangedAsync()
    {
        Dispatcher.Post(() => PluginsChanged?.Invoke(this, EventArgs.Empty));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_loop is { } loop)
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

        _rpc?.Dispose();
        _shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = MinimumRetryDelay;

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
                // Whatever went wrong, the loop keeps trying. A shell that silently stops
                // reconnecting looks identical to an engine that is down, and the user would have
                // no way to tell which they were looking at.
                Log.AttemptFailed(Logger, ex);
                SetState(EngineConnectionState.Disconnected, $"Could not reach the engine: {ex.Message}");
                wasConnected = false;
            }

            if (wasConnected)
            {
                // Connected and then dropped: back to the shortest delay, since a service restart
                // is the likeliest cause and it will be back in a second or two.
                delay = MinimumRetryDelay;
            }

            // Always waited, never skipped. A connection accepted and then closed at once - which
            // is exactly what an engine shutting down does - would otherwise send this loop
            // straight back round with no pause, opening connections as fast as the machine
            // allows. The plugin SDK had the identical defect and it crashed its own test host.
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (wasConnected)
            {
                continue;
            }

            // Back off toward a quiet poll. An engine that is not installed should not cost a
            // connection attempt every second for the life of the window.
            delay = delay < MaximumRetryDelay
                ? TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds))
                : MaximumRetryDelay;
        }
    }

    /// <summary>
    /// One connection attempt, held until the far end goes away.
    /// </summary>
    /// <returns>True if a connection was established and has since ended; false if it never was.</returns>
    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        SetState(EngineConnectionState.Connecting, "Looking for the engine service…");

        Stream stream;

        try
        {
            stream = await OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            ReportUnreachable();
            return false;
        }

        try
        {
            return await ServeAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or RemoteRpcException)
        {
            Log.ConnectionLost(Logger, ex);
            SetState(EngineConnectionState.Disconnected, "Lost the connection to the engine service.");
            return true;
        }
        finally
        {
            Engine = null;
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Opens the pipe, or whatever <see cref="Connect"/> was given instead.</summary>
    private async Task<Stream> OpenAsync(CancellationToken cancellationToken)
    {
        if (Connect is { } connect)
        {
            return await connect(cancellationToken).ConfigureAwait(false);
        }

        var stream = new NamedPipeClientStream(
            ".",
            ImpellerPipe.Name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(ConnectTimeout);

            await stream.ConnectAsync(attempt.Token).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return stream;
    }

    /// <summary>
    /// Runs one connection from handshake to close.
    /// </summary>
    /// <returns>
    /// True when the connection was served and has since ended; false when it was refused. The
    /// distinction drives the retry backoff, and getting it wrong means retrying a version mismatch
    /// once a second for the life of the window.
    /// </returns>
    private async Task<bool> ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        // The same serializer contract the engine and the configuration file use.
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = ImpellerJson.CompactOptions,
        };

        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, formatter));
        _rpc = rpc;

        try
        {
            rpc.AddLocalRpcTarget<IEngineEvents>(this, null);
            var engine = rpc.Attach<IEngineControl>();
            rpc.StartListening();

            // Before Engine is published, deliberately. A refused connection must never hand the
            // rest of the app a live proxy it will happily make calls on.
            if (!await AgreeAsync(engine, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            Engine = engine;

            var snapshot = await engine.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            Snapshot = snapshot;

            Log.Connected(
                Logger,
                snapshot.Status.Version,
                snapshot.Sensors.Count,
                snapshot.Controls.Count,
                snapshot.ConfigurationName);

            SetState(
                EngineConnectionState.Connected,
                $"Connected. {snapshot.Sensors.Count} sensors, {snapshot.Controls.Count} controls, "
                + $"configuration '{snapshot.ConfigurationName}'.");

            Dispatcher.Post(() => SnapshotReceived?.Invoke(this, snapshot));

            await rpc.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            rpc.Dispose();

            if (ReferenceEquals(_rpc, rpc))
            {
                _rpc = null;
            }
        }
    }

    /// <summary>
    /// Settles whether these two halves of Impeller speak the same protocol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine only answers; the decision is made here. That is not laziness about where to put
    /// the check — it is the only place it can work. The mismatch that matters most is a shell
    /// older than the service, and an older shell does not call this at all, so there is nothing
    /// for the engine to refuse. What it can do is answer honestly, which it does.
    /// </para>
    /// <para>
    /// A missing method is itself an answer, and the most likely one in practice: an engine built
    /// before this verb existed is, by definition, older than the window talking to it.
    /// </para>
    /// </remarks>
    private async Task<bool> AgreeAsync(IEngineControl engine, CancellationToken cancellationToken)
    {
        EngineHandshake handshake;

        try
        {
            handshake = await engine
                .HelloAsync(new ShellHello(ShellVersion, EngineProtocol.CurrentVersion), cancellationToken)
                .WaitAsync(EngineProtocol.HandshakeTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RemoteMethodNotFoundException)
        {
            ReportIncompatible(
                "The engine service is older than this copy of Impeller. Run the installer again to "
                + "update both halves.");
            return false;
        }

        if (handshake.Accepted)
        {
            return true;
        }

        ReportIncompatible(handshake.Message);
        return false;
    }

    private void ReportIncompatible(string message)
    {
        Log.Incompatible(Logger, message);
        SetState(EngineConnectionState.Incompatible, message);
    }

    /// <summary>
    /// Reports why the engine could not be reached, distinguishing the case where a different
    /// answer is needed from the cases where the answer is just "wait".
    /// </summary>
    private void ReportUnreachable()
    {
        if (!IsInstalled())
        {
            Log.NotInstalled(Logger);
            SetState(
                EngineConnectionState.NotInstalled,
                "The Impeller engine service is not installed. Fans are not being controlled.");
            return;
        }

        Log.NotRunning(Logger);

        SetState(
            EngineConnectionState.Disconnected,
            "The engine service is not running. Fans are being left to the firmware.");
    }

    /// <summary>
    /// Asks the supplied probe whether the engine is installed, and assumes it is when the probe
    /// cannot say.
    /// </summary>
    /// <remarks>
    /// The probe comes from outside this class and reaches the service control manager, so it can
    /// throw. It must not be able to stop the reconnect loop over a cosmetic question: the worst
    /// outcome of guessing wrong here is a slightly less helpful message, and the worst outcome of
    /// letting it escape is a shell that never reconnects.
    /// </remarks>
    private bool IsInstalled()
    {
        if (IsEngineInstalled is not { } probe)
        {
            return true;
        }

        try
        {
            return probe();
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Moves to a state and the sentence that goes with it.
    /// </summary>
    /// <remarks>
    /// The message is assigned first, and the order is load-bearing. Anything watching for a state
    /// to change reads the message in the same handler, and assigning the state first hands it the
    /// <em>previous</em> sentence — which is how a version mismatch first announced itself as
    /// "Looking for the engine service…".
    /// </remarks>
    private void SetState(EngineConnectionState state, string message) => Dispatcher.Post(() =>
    {
        StatusMessage = message;
        State = state;
    });

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 40,
            Level = LogLevel.Information,
            Message = "Connected to engine {Version}: {SensorCount} sensor(s), {ControlCount} control(s), configuration '{Configuration}'.")]
        public static partial void Connected(
            ILogger logger,
            string version,
            int sensorCount,
            int controlCount,
            string configuration);

        [LoggerMessage(
            EventId = 45,
            Level = LogLevel.Debug,
            Message = "Could not re-read the snapshot; the connection has probably gone.")]
        public static partial void RefreshFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 41, Level = LogLevel.Warning, Message = "Lost the connection to the engine.")]
        public static partial void ConnectionLost(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 42, Level = LogLevel.Error, Message = "The connection attempt failed. Retrying.")]
        public static partial void AttemptFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 43,
            Level = LogLevel.Warning,
            Message = "The engine service is not installed on this machine.")]
        public static partial void NotInstalled(ILogger logger);

        // Debug, not warning: the reconnect loop reaches this on every attempt while the engine is
        // down, and a restart taking a few seconds should not read as a wall of failures.
        [LoggerMessage(
            EventId = 44,
            Level = LogLevel.Debug,
            Message = "The engine service is installed but did not answer.")]
        public static partial void NotRunning(ILogger logger);

        // Error, and deliberately not throttled the way NotRunning is. This does not clear itself
        // by waiting, and the sentence is the whole point: it names which half to update.
        [LoggerMessage(EventId = 46, Level = LogLevel.Error, Message = "Refused by the engine. {Reason}")]
        public static partial void Incompatible(ILogger logger, string reason);
    }
}
