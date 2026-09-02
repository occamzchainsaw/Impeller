using System.IO.Pipes;
using System.Runtime.Versioning;
using Impeller.Ipc.Contracts;
using StreamJsonRpc;

namespace Impeller.EngineService.Ipc;

/// <summary>
/// Listens on the engine's pipe and serves whoever connects.
/// </summary>
/// <remarks>
/// <para>
/// One accept loop, one JSON-RPC connection per client, and a push of every tick to whoever is
/// attached. Nothing polls: the shell is told what happened rather than asking every second, which
/// is what makes a one-second tick rate affordable to display.
/// </para>
/// <para>
/// Every failure here is contained. A client that disconnects mid-call, a malformed request, a
/// pipe that cannot be created — none of them may stop the engine, because the engine's job is to
/// keep fans under control whether or not anything is watching.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class EngineRpcHost(
    EngineRpcService service,
    EngineNotifications notifications,
    ILogger<EngineRpcHost> logger) : BackgroundService
{
    private readonly List<ClientConnection> _clients = [];
    private readonly Lock _gate = new();

    /// <summary>How many shells are currently attached.</summary>
    public int ConnectedClients
    {
        get
        {
            lock (_gate)
            {
                return _clients.Count;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        notifications.Ticked += OnTicked;
        notifications.ConfigurationChanged += OnConfigurationChanged;
        notifications.HardwareChanged += OnHardwareChanged;
        notifications.TuningProgressed += OnTuningProgress;

        Log.Listening(logger, ImpellerPipe.Name);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await AcceptAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Nothing here may take the engine down with it. A BackgroundService that throws stops
            // the host by default, which would mean an RPC fault costing the machine its fan
            // control -- exactly the coupling that running the engine as its own process exists to
            // remove. The shell losing its channel is a bad afternoon; fans left to whatever duty
            // was last written is a thermal problem.
            Log.HostFailed(logger, ex);
        }
        finally
        {
            notifications.Ticked -= OnTicked;
            notifications.ConfigurationChanged -= OnConfigurationChanged;
            notifications.HardwareChanged -= OnHardwareChanged;
            notifications.TuningProgressed -= OnTuningProgress;

            DisconnectAll();
            Log.Stopped(logger);
        }
    }

    /// <summary>
    /// Waits for one client and hands it its own connection.
    /// </summary>
    /// <remarks>
    /// The stream is handed to the connection, which owns it from then on — including disposing it
    /// when the client goes away. Failing to create a pipe at all is logged and retried after a
    /// pause rather than treated as fatal: the usual cause is a stale instance still closing.
    /// </remarks>
    private async Task AcceptAsync(CancellationToken stoppingToken)
    {
        NamedPipeServerStream stream;

        try
        {
            stream = EnginePipe.Create();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.PipeUnavailable(logger, ex);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await stream.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var connection = new ClientConnection(stream, service);

        lock (_gate)
        {
            _clients.Add(connection);
        }

        Log.ClientConnected(logger, ConnectedClients);

        // Remove it when the other end goes away, whenever that turns out to be.
        _ = connection.Completion.ContinueWith(
            _ =>
            {
                lock (_gate)
                {
                    _clients.Remove(connection);
                }

                connection.Dispose();
                Log.ClientDisconnected(logger, ConnectedClients);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnTicked(object? sender, TickSnapshot snapshot) =>
        Broadcast(events => events.OnTickAsync(snapshot));

    private void OnConfigurationChanged(object? sender, ConfigurationResult result) =>
        Broadcast(events => events.OnConfigurationChangedAsync(result));

    private void OnHardwareChanged(object? sender, EventArgs e) =>
        Broadcast(events => events.OnHardwareChangedAsync());

    private void OnTuningProgress(object? sender, Core.Abstractions.TuningProgress progress) =>
        Broadcast(events => events.OnTuningProgressAsync(progress));

    /// <summary>
    /// Sends to every attached client, and lets none of them hold up the engine.
    /// </summary>
    /// <remarks>
    /// Fire and forget, deliberately. This is called from the tick loop's thread, and awaiting a
    /// client that has stopped reading would stall fan control behind a hung UI — precisely the
    /// coupling that splitting the two processes was meant to remove.
    /// </remarks>
    private void Broadcast(Func<IEngineEvents, Task> send)
    {
        ClientConnection[] clients;

        lock (_gate)
        {
            if (_clients.Count == 0)
            {
                return;
            }

            clients = [.. _clients];
        }

        foreach (var client in clients)
        {
            try
            {
                _ = send(client.Events).ContinueWith(
                    task => Log.NotifyFailed(logger, task.Exception!),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                // A client that has already gone is not an engine problem.
                Log.NotifyFailed(logger, ex);
            }
        }
    }

    private void DisconnectAll()
    {
        ClientConnection[] clients;

        lock (_gate)
        {
            clients = [.. _clients];
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    /// <summary>One connected shell: its stream, its JSON-RPC session, and its callback proxy.</summary>
    private sealed class ClientConnection : IDisposable
    {
        private readonly NamedPipeServerStream _stream;
        private readonly JsonRpc _rpc;

        public ClientConnection(NamedPipeServerStream stream, EngineRpcService service)
        {
            _stream = stream;

            // The same serializer contract the configuration file uses, so the wire format and the
            // stored format cannot drift apart.
            var formatter = new SystemTextJsonFormatter
            {
                JsonSerializerOptions = Core.Abstractions.Configuration.ImpellerJson.CompactOptions,
            };

            _rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, formatter));
            _rpc.AddLocalRpcTarget<IEngineControl>(service, null);
            Events = _rpc.Attach<IEngineEvents>();
            _rpc.StartListening();
        }

        /// <summary>The far end's callback interface.</summary>
        public IEngineEvents Events { get; }

        /// <summary>Completes when the client goes away, however it goes.</summary>
        public Task Completion => _rpc.Completion;

        public void Dispose()
        {
            _rpc.Dispose();
            _stream.Dispose();
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 20,
            Level = LogLevel.Information,
            Message = "Listening for clients on pipe {PipeName}.")]
        public static partial void Listening(ILogger logger, string pipeName);

        [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Stopped listening for clients.")]
        public static partial void Stopped(ILogger logger);

        [LoggerMessage(
            EventId = 22,
            Level = LogLevel.Information,
            Message = "A client connected; {Count} now attached.")]
        public static partial void ClientConnected(ILogger logger, int count);

        [LoggerMessage(
            EventId = 23,
            Level = LogLevel.Information,
            Message = "A client disconnected; {Count} still attached.")]
        public static partial void ClientDisconnected(ILogger logger, int count);

        [LoggerMessage(
            EventId = 24,
            Level = LogLevel.Warning,
            Message = "Could not open the listening pipe; retrying shortly.")]
        public static partial void PipeUnavailable(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 26,
            Level = LogLevel.Error,
            Message = "The client channel has stopped. The engine continues to control fans.")]
        public static partial void HostFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 25,
            Level = LogLevel.Debug,
            Message = "Could not notify a client; it has probably gone away.")]
        public static partial void NotifyFailed(ILogger logger, Exception exception);
    }
}
