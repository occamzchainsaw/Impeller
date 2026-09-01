using System.IO.Pipes;
using CommunityToolkit.Mvvm.ComponentModel;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;
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

    /// <summary>Raised when a fresh snapshot has been fetched, after every successful connect.</summary>
    public event EventHandler<EngineSnapshot>? SnapshotReceived;

    /// <summary>Raised on every tick the engine reports.</summary>
    public event EventHandler<TickSnapshot>? Ticked;

    /// <summary>Raised when the configuration in force changes, from any source.</summary>
    public event EventHandler<ConfigurationResult>? ConfigurationChanged;

    /// <summary>Raised when the available hardware changes and the snapshot is stale.</summary>
    public event EventHandler? HardwareChanged;

    /// <summary>Starts connecting, and keeps reconnecting until disposed.</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_shutdown.Token));

    /// <inheritdoc />
    public Task OnTickAsync(TickSnapshot snapshot)
    {
        Ticked?.Invoke(this, snapshot);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnConfigurationChangedAsync(ConfigurationResult result)
    {
        ConfigurationChanged?.Invoke(this, result);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnHardwareChangedAsync()
    {
        HardwareChanged?.Invoke(this, EventArgs.Empty);
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
            if (await TryConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                // Connected and then dropped: try again promptly, since a service restart is the
                // most likely cause and it will be back in a second or two.
                delay = MinimumRetryDelay;
                continue;
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
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
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            ReportUnreachable();
            return false;
        }

        try
        {
            await ServeAsync(stream, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or RemoteRpcException)
        {
            SetState(EngineConnectionState.Disconnected, "Lost the connection to the engine service.");
            return true;
        }
        finally
        {
            Engine = null;
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(NamedPipeClientStream stream, CancellationToken cancellationToken)
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

            Engine = engine;

            var snapshot = await engine.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            Snapshot = snapshot;

            SetState(
                EngineConnectionState.Connected,
                $"Connected. {snapshot.Sensors.Count} sensors, {snapshot.Controls.Count} controls, "
                + $"configuration '{snapshot.ConfigurationName}'.");

            SnapshotReceived?.Invoke(this, snapshot);

            await rpc.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
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
    /// Reports why the engine could not be reached, distinguishing the case where a different
    /// answer is needed from the cases where the answer is just "wait".
    /// </summary>
    private void ReportUnreachable()
    {
        if (IsEngineInstalled is { } installed && !installed())
        {
            SetState(
                EngineConnectionState.NotInstalled,
                "The Impeller engine service is not installed. Fans are not being controlled.");
            return;
        }

        SetState(
            EngineConnectionState.Disconnected,
            "The engine service is not running. Fans are being left to the firmware.");
    }

    private void SetState(EngineConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
    }
}
