using Impeller.Core.Abstractions;
using Impeller.Core.Engine;
using Impeller.Core.Engine.Configuration;
using Impeller.Core.Persistence;
using Impeller.EngineService.Ipc;
using Impeller.Ipc.Contracts;
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
    IEnumerable<ISensorProvider> providers,
    ControlOwnershipRegistry ownership,
    ConfigurationCoordinator configuration,
    EngineNotifications notifications,
    EngineWorkerState state,
    EngineReadiness readiness,
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
        // Relayed rather than raised directly by the engine: the tick loop and the coordinator
        // should not know that an RPC channel exists at all.
        configuration.Changed += OnConfigurationChanged;
        registry.ProviderTopologyChanged += OnHardwareChanged;

        await RegisterProvidersAsync(stoppingToken).ConfigureAwait(false);

        // Providers first, then the configuration: a first run builds its control list from the
        // hardware actually found, so there has to be hardware to find by the time it runs.
        LoadConfiguration();

        Log.Starting(logger, _options.TickInterval, registry.ProviderCount);

        StartWatchdog(stoppingToken);

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

        configuration.Changed -= OnConfigurationChanged;
        registry.ProviderTopologyChanged -= OnHardwareChanged;

        Log.Stopping(logger, TickCount);
    }

    private void OnConfigurationChanged(object? sender, ConfigurationChanged e) =>
        notifications.RaiseConfigurationChanged(
            new ConfigurationResult(true, e.Name, e.Validation));

    private void OnHardwareChanged(object? sender, ISensorProvider provider) =>
        notifications.RaiseHardwareChanged();

    /// <summary>
    /// Hands this tick's readings to anything watching.
    /// </summary>
    /// <remarks>
    /// Skipped entirely when nothing is attached. Building several hundred readings a second for
    /// an audience of nobody is the sort of cost that is invisible until it is measured.
    /// </remarks>
    private void PublishTick(TickResult result, DateTimeOffset now)
    {
        if (!notifications.HasTickListeners)
        {
            return;
        }

        var sensors = registry.Sensors
            .Select(sensor => new SensorReading(sensor.Id, sensor.Value))
            .ToArray();

        var controls = registry.Controls
            .Select(control =>
            {
                var owner = ownership.GetOwner(control.Id);

                return new ControlReading(
                    control.Id,
                    result.CommandedDuties.TryGetValue(control.Id, out var duty) ? duty : control.CommandedDuty,
                    owner.Kind,
                    owner.ClaimantId);
            })
            .ToArray();

        // Already computed to resolve the controls above, and thrown away until now.
        var curves = result.CurveOutputs
            .Select(output => new CurveReading(output.Key, output.Value))
            .ToArray();

        notifications.RaiseTick(new TickSnapshot(TickCount, now, sensors, controls, curves));
    }

    /// <summary>
    /// Loads the configuration that should be driving the fans.
    /// </summary>
    /// <remarks>
    /// A configuration that cannot be read is logged and skipped rather than treated as fatal. The
    /// engine then runs with nothing bound, which writes to no hardware at all — worse than
    /// working, and considerably better than a service that refuses to start and leaves every fan
    /// wherever the last thing to touch it left it.
    /// </remarks>
    private void LoadConfiguration()
    {
        try
        {
            var validation = configuration.Start(_options.ConfigurationName);

            if (validation.HasErrors)
            {
                foreach (var issue in validation.Errors)
                {
                    Log.ConfigurationError(logger, issue.Code, issue.Message);
                }

                return;
            }

            foreach (var issue in validation.Warnings)
            {
                Log.ConfigurationWarning(logger, issue.Code, issue.Message);
            }

            var enabled = configuration.Current.Controls.Count(binding => binding.Enabled);

            Log.ConfigurationLoaded(
                logger,
                configuration.CurrentName,
                configuration.Current.Curves.Count,
                enabled);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ConfigMigrationException)
        {
            Log.ConfigurationFailed(logger, _options.ConfigurationName, ex);
        }
    }

    /// <summary>
    /// Starts the health check on a thread of its own.
    /// </summary>
    /// <remarks>
    /// A thread rather than a timer, deliberately. The failure being watched for is a tick loop
    /// that has stopped making progress, and one plausible cause of that is a starved or blocked
    /// thread pool — which is exactly where a timer callback would be queued behind the problem.
    /// </remarks>
    private void StartWatchdog(CancellationToken stoppingToken)
    {
        if (_options.TickTimeout <= TimeSpan.Zero)
        {
            return;
        }

        var watchdog = new TickWatchdog(_options.TickTimeout);

        // Check several times per timeout, so a trip is noticed promptly rather than up to a whole
        // timeout after the fact.
        var interval = _options.TickTimeout / 3;

        new Thread(() => WatchdogLoop(watchdog, interval, stoppingToken))
        {
            IsBackground = true,
            Name = "Impeller watchdog",
        }.Start();
    }

    private void WatchdogLoop(TickWatchdog watchdog, TimeSpan interval, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (stoppingToken.WaitHandle.WaitOne(interval))
                {
                    return;
                }

                switch (watchdog.Evaluate(timeProvider.GetUtcNow(), LastTickCompleted))
                {
                    case WatchdogVerdict.JustTripped:
                        Log.WatchdogTripped(logger, _options.TickTimeout);

                        // Engaged unconditionally, then logged. Fans must be driven to safety
                        // whether or not anyone is listening to the log.
                        var failsafe = loop.EngageFailsafe();
                        Log.FailsafeApplied(logger, failsafe.ControlsWritten);
                        break;

                    case WatchdogVerdict.Recovered:
                        Log.WatchdogRecovered(logger);
                        loop.ClearFailsafe();
                        break;

                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                // The watchdog failing must be visible, and must not stop it watching.
                Log.WatchdogFailed(logger, ex);
            }
        }
    }

    /// <summary>
    /// Brings every registered provider up before the first tick.
    /// </summary>
    /// <remarks>
    /// A provider that fails to initialise is kept rather than dropped, so the settings UI can
    /// show it as unavailable and offer a retry. Starting the loop with no working provider is
    /// still the right thing to do: manual overrides and plugin-owned controls do not depend on
    /// hardware discovery, and a machine with a broken sensor backend should not be a machine
    /// with no engine.
    /// </remarks>
    private async Task RegisterProvidersAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in providers)
        {
            var result = await registry.AddAsync(provider, cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                Log.ProviderFailed(logger, provider.DisplayName, result.Error!);
                continue;
            }

            Log.ProviderReady(logger, provider.DisplayName, result.SensorCount, result.ControlCount);

            if (result.FailedGroups.Count > 0)
            {
                Log.ProviderGroupsMissing(
                    logger,
                    provider.DisplayName,
                    string.Join(", ", result.FailedGroups));
            }
        }
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

            // Shared so the RPC layer can read liveness without a reference to a hosted service.
            state.TickCount = TickCount;
            state.LastTickCompleted = now;

            // Only now is the engine answering questions correctly: providers enumerated, sensor
            // identities resolved, bindings live. Anything gated on this - the plugin channel -
            // would otherwise open onto an engine that refuses every control it is asked about.
            readiness.MarkReady();

            PublishTick(result, now);
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

        [LoggerMessage(
            EventId = 8,
            Level = LogLevel.Information,
            Message = "{Provider}: {SensorCount} sensor(s), {ControlCount} control(s).")]
        public static partial void ProviderReady(
            ILogger logger,
            string provider,
            int sensorCount,
            int controlCount);

        [LoggerMessage(
            EventId = 9,
            Level = LogLevel.Error,
            Message = "{Provider} failed to initialise and will be unavailable.")]
        public static partial void ProviderFailed(ILogger logger, string provider, Exception exception);

        [LoggerMessage(
            EventId = 11,
            Level = LogLevel.Information,
            Message = "Configuration '{Name}' loaded: {CurveCount} curve(s), {EnabledCount} control(s) enabled.")]
        public static partial void ConfigurationLoaded(
            ILogger logger,
            string name,
            int curveCount,
            int enabledCount);

        [LoggerMessage(
            EventId = 12,
            Level = LogLevel.Warning,
            Message = "Configuration issue [{Code}]: {Detail}")]
        public static partial void ConfigurationWarning(ILogger logger, string code, string detail);

        [LoggerMessage(
            EventId = 13,
            Level = LogLevel.Error,
            Message = "Configuration rejected [{Code}]: {Detail}")]
        public static partial void ConfigurationError(ILogger logger, string code, string detail);

        [LoggerMessage(
            EventId = 14,
            Level = LogLevel.Error,
            Message = "Configuration '{Name}' could not be loaded; running with nothing bound.")]
        public static partial void ConfigurationFailed(ILogger logger, string name, Exception exception);

        [LoggerMessage(
            EventId = 15,
            Level = LogLevel.Error,
            Message = "No tick has completed within {Timeout}; engaging the failsafe.")]
        public static partial void WatchdogTripped(ILogger logger, TimeSpan timeout);

        [LoggerMessage(
            EventId = 16,
            Level = LogLevel.Information,
            Message = "Ticks have resumed; returning controls to their curves.")]
        public static partial void WatchdogRecovered(ILogger logger);

        [LoggerMessage(
            EventId = 17,
            Level = LogLevel.Error,
            Message = "The watchdog check itself failed; still watching.")]
        public static partial void WatchdogFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 10,
            Level = LogLevel.Warning,
            Message = "{Provider} found no hardware for: {Groups}. This usually means the process "
                + "lacks the privileges its kernel driver needs, or a vendor driver is missing.")]
        public static partial void ProviderGroupsMissing(ILogger logger, string provider, string groups);
    }
}
