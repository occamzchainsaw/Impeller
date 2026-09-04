using System.IO.Pipes;
using System.Runtime.Versioning;
using Impeller.Core.Engine;
using Impeller.Ipc.Contracts;
using Impeller.Platform.Windows;
using Impeller.Plugins.Abstractions;
using Impeller.Plugins.Host;

namespace Impeller.EngineService.Ipc;

/// <summary>
/// Listens on the plugin pipe and hands each connection to the plugin host.
/// </summary>
/// <remarks>
/// <para>
/// Registration order matters twice. It is registered <em>after</em> <see cref="EngineRpcHost"/>,
/// and hosted services stop in reverse order, so this one stops first: plugins are told the engine
/// is going and let go of their fans while the window is still connected and the failsafe has not
/// yet run.
/// </para>
/// <para>
/// The accept loop also waits on <see cref="EngineReadiness"/> before opening the pipe at all. A
/// plugin reconnecting on a one-second backoff would otherwise beat provider enumeration and be
/// told <c>UnknownControl</c> for every id it has ever known — which looks, from the plugin's side,
/// exactly like the user having removed their hardware.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class PluginRpcHost(
    PluginHost plugins,
    EngineNotifications notifications,
    EngineReadiness readiness,
    ILogger<PluginRpcHost> logger) : BackgroundService
{
    /// <summary>How long to wait before retrying a pipe that could not be created.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>And how long when the name belongs to someone else, which will not fix itself.</summary>
    private static readonly TimeSpan SquattedRetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        notifications.Ticked += OnTicked;

        try
        {
            await readiness.WaitAsync(stoppingToken).ConfigureAwait(false);

            Log.Listening(logger, PluginProtocol.PipeName);

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
            // Nothing here may take the engine down. Losing the plugin channel costs a user their
            // plugins; a BackgroundService that throws stops the host, which would cost them fan
            // control — and the fans are the reason any of this exists.
            Log.HostFailed(logger, ex);
        }
        finally
        {
            notifications.Ticked -= OnTicked;

            await plugins.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Log.Stopped(logger);
        }
    }

    /// <summary>Waits for one plugin, works out what it is, and hands it over.</summary>
    private async Task AcceptAsync(CancellationToken stoppingToken)
    {
        NamedPipeServerStream stream;

        try
        {
            stream = PluginPipe.Create();
        }
        catch (UnauthorizedAccessException ex)
        {
            // Something else owns this name. Distinguished from a transient failure on purpose:
            // retrying silently forever would let a squatter that got in before the engine harvest
            // every plugin handshake on the machine for the rest of the machine's uptime, and the
            // only symptom would be that plugins never connect.
            Log.PipeTaken(logger, PluginProtocol.PipeName, ex);
            await Task.Delay(SquattedRetryDelay, stoppingToken).ConfigureAwait(false);
            return;
        }
        catch (IOException ex)
        {
            // The ordinary cause is a stale instance still closing.
            Log.PipeUnavailable(logger, ex);
            await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
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

        // Read before a byte of the handshake, so the answer describes the process that actually
        // opened the connection rather than whatever it later claims to be.
        var client = PipeClientIdentity.Identify(stream);
        var identity = new PluginIdentity(client.ImagePath, client.UserSid);

        if (plugins.Accept(stream, identity) is null)
        {
            // Refused before it said anything: the host is stopping, or too many connections are
            // already sitting silent.
            await stream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        Log.Connected(logger, client.ImagePath ?? "an unknown program", plugins.SessionCount);
    }

    /// <summary>
    /// Drives the plugin channel's per-tick work from the engine's own tick.
    /// </summary>
    /// <remarks>
    /// Rather than a timer of its own, so leases, health checks and readings are all measured on
    /// the same clock the fans are. It returns immediately; nothing on this path waits for a
    /// plugin.
    /// </remarks>
    private void OnTicked(object? sender, TickSnapshot snapshot) =>
        plugins.PublishTick(snapshot.Tick, snapshot.At);

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 60,
            Level = LogLevel.Information,
            Message = "Listening for plugins on pipe {PipeName}.")]
        public static partial void Listening(ILogger logger, string pipeName);

        [LoggerMessage(EventId = 61, Level = LogLevel.Information, Message = "Stopped listening for plugins.")]
        public static partial void Stopped(ILogger logger);

        [LoggerMessage(
            EventId = 62,
            Level = LogLevel.Information,
            Message = "A plugin connection arrived from {ImagePath}; {Count} plugins admitted.")]
        public static partial void Connected(ILogger logger, string imagePath, int count);

        [LoggerMessage(
            EventId = 63,
            Level = LogLevel.Warning,
            Message = "Could not open the plugin pipe; retrying shortly.")]
        public static partial void PipeUnavailable(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 64,
            Level = LogLevel.Error,
            Message = "The name {PipeName} is owned by another process, so plugins cannot reach this "
                + "engine and may be talking to something else. Find and close whatever holds it.")]
        public static partial void PipeTaken(ILogger logger, string pipeName, Exception exception);

        [LoggerMessage(
            EventId = 65,
            Level = LogLevel.Error,
            Message = "The plugin channel has stopped. The engine continues to control fans.")]
        public static partial void HostFailed(ILogger logger, Exception exception);
    }
}
