using Impeller.Core.Abstractions;
using Impeller.Core.Engine;
using Microsoft.Extensions.Options;

namespace Impeller.EngineService;

/// <summary>
/// Drives the control loop on a fixed cadence for as long as the service runs.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin. Everything about how fans behave lives in <see cref="ControlLoop"/>, which
/// has no timer, no hosting dependency, and no hardware of its own — that is what makes the
/// behaviour testable. This class supplies the clock, refreshes providers on their own cadence,
/// and makes sure the engine fails safe on the way out.
/// </para>
/// <para>
/// A tick that throws is logged and the loop continues. Stopping because one tick failed would
/// leave every fan at its last duty with nothing maintaining it, which is precisely the state
/// the failsafe exists to prevent.
/// </para>
/// </remarks>
public sealed partial class EngineWorker(
    ControlLoop loop,
    AggregatingSensorRegistry registry,
    TimeProvider timeProvider,
    IOptions<EngineOptions> options,
    ILogger<EngineWorker> logger) : BackgroundService
{
    private readonly EngineOptions _options = options.Value;

    /// <summary>
    /// When the last tick completed. The watchdog reads this to decide whether the engine is
    /// still alive; a stale value is what trips the failsafe.
    /// </summary>
    public DateTimeOffset LastTickCompleted { get; private set; }

    /// <summary>How many ticks have run since the service started.</summary>
    public long TickCount { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Starting(logger, _options.TickInterval, registry.ProviderCount);

        using var timer = new PeriodicTimer(_options.TickInterval, timeProvider);

        var previous = timeProvider.GetTimestamp();
        LastTickCompleted = timeProvider.GetUtcNow();

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var now = timeProvider.GetUtcNow();
                var elapsed = timeProvider.GetElapsedTime(previous);
                previous = timeProvider.GetTimestamp();

                await RunTickAsync(now, elapsed, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        Log.Stopping(logger, TickCount);
    }

    /// <summary>
    /// Runs one tick: refresh whatever is due, then evaluate and write.
    /// Faults are logged rather than propagated, so one bad provider cannot stop control.
    /// </summary>
    private async Task RunTickAsync(DateTimeOffset now, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        try
        {
            var refreshFaults = await registry
                .RefreshDueAsync(now, cancellationToken)
                .ConfigureAwait(false);

            foreach (var (providerId, exception) in refreshFaults)
            {
                Log.ProviderRefreshFailed(logger, providerId, exception);
            }

            var result = loop.Tick(elapsed);

            foreach (var (controlId, exception) in result.Faults)
            {
                Log.ControlWriteFailed(logger, controlId, exception);
            }

            TickCount++;
            LastTickCompleted = now;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep ticking. A loop that stops on error strands every fan at its last duty.
            Log.TickFailed(logger, ex);
        }
    }

    /// <summary>
    /// Drives controls to their failsafe duty on the way out, so nothing is left at a duty the
    /// engine is no longer maintaining. Shares its code path with watchdog recovery deliberately.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        if (!_options.FailsafeOnShutdown)
        {
            return;
        }

        try
        {
            var result = loop.EngageFailsafe();
            Log.FailsafeApplied(logger, result.ControlsWritten);
        }
        catch (Exception ex)
        {
            Log.FailsafeFailed(logger, ex);
        }
    }

    /// <summary>
    /// Source-generated log messages. Avoids boxing the value-type arguments on a path that
    /// runs every second.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Information,
            Message = "Engine starting: tick interval {TickInterval}, {ProviderCount} provider(s).")]
        public static partial void Starting(ILogger logger, TimeSpan tickInterval, int providerCount);

        [LoggerMessage(
            EventId = 2,
            Level = LogLevel.Information,
            Message = "Engine stopping after {TickCount} tick(s).")]
        public static partial void Stopping(ILogger logger, long tickCount);

        [LoggerMessage(
            EventId = 3,
            Level = LogLevel.Warning,
            Message = "Provider {ProviderId} failed to refresh.")]
        public static partial void ProviderRefreshFailed(ILogger logger, string providerId, Exception exception);

        [LoggerMessage(
            EventId = 4,
            Level = LogLevel.Warning,
            Message = "Control {ControlId} could not be written.")]
        public static partial void ControlWriteFailed(ILogger logger, SensorId controlId, Exception exception);

        [LoggerMessage(
            EventId = 5,
            Level = LogLevel.Error,
            Message = "Tick failed; continuing.")]
        public static partial void TickFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 6,
            Level = LogLevel.Information,
            Message = "Engine stopped; {ControlCount} control(s) set to their failsafe duty.")]
        public static partial void FailsafeApplied(ILogger logger, int controlCount);

        [LoggerMessage(
            EventId = 7,
            Level = LogLevel.Error,
            Message = "Failed to apply the failsafe during shutdown.")]
        public static partial void FailsafeFailed(ILogger logger, Exception exception);
    }
}
