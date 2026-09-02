using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine;
using Impeller.Core.Engine.Configuration;
using Impeller.Core.Persistence.Diagnostics;
using Impeller.Ipc.Contracts;

namespace Impeller.EngineService.Ipc;

/// <summary>
/// The engine's side of the channel: everything the shell is allowed to ask for.
/// </summary>
/// <remarks>
/// <para>
/// A thin translation layer on purpose. Every decision it appears to make is made somewhere the
/// tick loop can reach without a pipe — validation in the validator, arbitration in the ownership
/// registry, application in the coordinator — so a shell asking over RPC and the engine acting on
/// its own take exactly the same path and fail exactly the same way.
/// </para>
/// <para>
/// One instance serves every connection. Nothing here holds per-client state, which is what makes
/// a second shell a non-event rather than a second source of truth.
/// </para>
/// </remarks>
public sealed class EngineRpcService(
    ConfigurationCoordinator coordinator,
    AggregatingSensorRegistry registry,
    ControlOwnershipRegistry ownership,
    ControlLoop loop,
    EngineStatePaths paths,
    TimeProvider timeProvider,
    EngineWorkerState workerState) : IEngineControl
{
    /// <summary>Who the engine records as holding a manually overridden control.</summary>
    public const string ManualClaimant = ControlOwnershipRegistry.ManualClaimant;

    /// <inheritdoc />
    public Task<EngineSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new EngineSnapshot(
            BuildStatus(),
            [.. registry.Sensors.Select(Describe)],
            [.. registry.Controls.Select(DescribeControl)],
            coordinator.CurrentName,
            coordinator.Current,
            coordinator.LastValidation,
            [.. coordinator.List().Select(entry => entry.Name)]));

    /// <inheritdoc />
    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(BuildStatus());

    /// <inheritdoc />
    public Task<ConfigurationResult> ApplyConfigurationAsync(
        ImpellerConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var validation = coordinator.Apply(configuration);

        return Task.FromResult(new ConfigurationResult(
            !validation.HasErrors,
            coordinator.CurrentName,
            validation));
    }

    /// <inheritdoc />
    public Task<ConfigurationValidation> ValidateConfigurationAsync(
        ImpellerConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ConfigurationValidator.Validate(configuration, registry));

    /// <inheritdoc />
    public Task<EquatableArray<string>> ListConfigurationsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<EquatableArray<string>>([.. coordinator.List().Select(entry => entry.Name)]);

    /// <inheritdoc />
    public Task<ConfigurationResult> LoadConfigurationAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var validation = coordinator.Load(name);

        return Task.FromResult(new ConfigurationResult(
            !validation.HasErrors,
            coordinator.CurrentName,
            validation));
    }

    /// <inheritdoc />
    public Task<bool> DeleteConfigurationAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(coordinator.Delete(name));

    /// <inheritdoc />
    /// <remarks>
    /// The pin is written to the configuration as well as taken, so it is still in force after a
    /// restart. Pinning a fan is an instruction, not a property of the session that issued it.
    /// </remarks>
    public Task<ControlAcquireOutcome> SetManualDutyAsync(
        SensorId controlId,
        Duty duty,
        CancellationToken cancellationToken = default)
    {
        var outcome = Pin(controlId, duty);

        if (outcome.Granted)
        {
            coordinator.RecordManualDuty(controlId, duty);
        }

        return Task.FromResult(outcome);
    }

    /// <inheritdoc />
    public Task<bool> ReleaseControlAsync(SensorId controlId, CancellationToken cancellationToken = default)
    {
        var released = ownership.Release(controlId, ManualClaimant);

        if (released)
        {
            coordinator.RecordManualDuty(controlId, null);
        }

        return Task.FromResult(released);
    }

    /// <summary>
    /// Takes the manual claim and sets the duty, without recording anything.
    /// </summary>
    /// <remarks>
    /// Shared by pinning and identifying, which differ in exactly one way: a pin is remembered and
    /// an identify is not. Keeping the claim logic here and the saving at the call site is what
    /// stops a fan briefly spun up to locate it from being written into the configuration as the
    /// user's intent.
    /// </remarks>
    private ControlAcquireOutcome Pin(SensorId controlId, Duty duty)
    {
        if (registry.GetControl(controlId) is null)
        {
            return Refused(ControlAcquireFailure.UnknownControl, null);
        }

        var owner = ownership.GetOwner(controlId);

        // Already ours: adjusting a slider should not need the claim taken again.
        if (owner.Kind != ControlOwnerKind.ManualOverride)
        {
            var acquired = ownership.TryAcquire(controlId, ControlOwnerKind.ManualOverride, ManualClaimant);

            if (!acquired.Succeeded)
            {
                return Refused(acquired.Failure, acquired.CurrentOwner);
            }
        }

        loop.TrySetRequestedDuty(controlId, duty, ManualClaimant);
        return new ControlAcquireOutcome(true, default, ControlOwnerKind.ManualOverride, ManualClaimant);
    }

    /// <inheritdoc />
    public Task<ControlAcquireOutcome> IdentifyControlAsync(
        SensorId controlId,
        Duty duty,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        // Not recorded: finding which fan is which is not a statement about how it should run.
        var outcome = Pin(controlId, duty);

        if (!outcome.Granted)
        {
            return Task.FromResult(outcome);
        }

        // Released by the engine on a timer rather than by the caller. A shell that crashes
        // halfway through identifying a fan must not leave it pinned at full speed forever.
        _ = ReleaseAfterAsync(controlId, duration);

        return Task.FromResult(outcome);
    }

    private async Task ReleaseAfterAsync(SensorId controlId, TimeSpan duration)
    {
        try
        {
            await Task.Delay(duration, timeProvider).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down. The failsafe path takes the control from here.
            return;
        }

        ownership.Release(controlId, ManualClaimant);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Assembled here rather than by the shell because most of it is only reachable from inside the
    /// engine process — the provider results, the configuration actually in force, the log file the
    /// engine holds open. A shell rendering its own version would be reporting on what it was told,
    /// not on what is running.
    /// </remarks>
    public Task<DiagnosticReport> GetDiagnosticReportAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DiagnosticReport
        {
            Taken = timeProvider.GetUtcNow(),
            Status = BuildStatus(),
            OperatingSystem = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RunningAsService = !Environment.UserInteractive,
            Identity = CurrentIdentity(),
            LogRoot = paths.LogRoot,
            Providers = [.. registry.Providers.Select(DescribeProvider)],
            Sensors = [.. registry.Sensors.Select(Describe)],
            Controls = [.. registry.Controls.Select(DescribeControl)],
            ConfigurationName = coordinator.CurrentName,
            Configuration = coordinator.Current,
            Validation = coordinator.LastValidation,
            RecentLog = [.. LogFiles.Tail(paths.LogRoot, "engine-*.log")],
        });

    private static ProviderDiagnostics DescribeProvider(
        (ISensorProvider Provider, ProviderInitializationResult? Result) entry) => new(
        entry.Provider.ProviderId,
        entry.Provider.DisplayName,
        entry.Result?.Succeeded ?? false,
        entry.Result?.SensorCount ?? 0,
        entry.Result?.ControlCount ?? 0,
        [.. entry.Result?.FailedGroups ?? []],

        // The whole exception, not just its message: the inner one is usually the interesting half
        // when a driver refuses to load.
        entry.Result?.Error?.ToString());

    /// <summary>
    /// Who the engine is running as, which is the first question when hardware is missing.
    /// </summary>
    /// <remarks>
    /// A backend that enumerates nothing under a user account and everything under LocalSystem is a
    /// permissions problem wearing a hardware problem's clothes, and this is the line in the report
    /// that tells them apart.
    /// </remarks>
    private static string CurrentIdentity()
    {
        try
        {
            return WindowsIdentity.GetCurrent().Name;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            return "unknown";
        }
    }

    private EngineStatus BuildStatus() => new(
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
        workerState.TickCount,
        workerState.LastTickCompleted,
        loop.IsFailsafeEngaged,
        paths.ConfigurationRoot);

    private static SensorDescriptor Describe(ISensor sensor) => new(
        sensor.Id,
        sensor.Name,
        sensor.Kind,
        sensor.Fingerprint.ProviderId,
        sensor.Fingerprint.ToString(),
        sensor.Value);

    private ControlDescriptor DescribeControl(IControl control)
    {
        var owner = ownership.GetOwner(control.Id);

        return new ControlDescriptor(
            control.Id,
            control.Name,
            control.Fingerprint.ProviderId,
            control.Fingerprint.ToString(),
            loop.GetCommandedDuty(control.Id) ?? control.CommandedDuty,
            control.SupportsAutomaticMode,
            owner.Kind,
            owner.ClaimantId);
    }

    private static ControlAcquireOutcome Refused(ControlAcquireFailure failure, ControlOwner? current) =>
        new(false, failure, current?.Kind, current?.ClaimantId);
}

/// <summary>
/// The engine worker's liveness counters, readable without a reference to the worker itself.
/// </summary>
/// <remarks>
/// A hosted service is owned by the host and awkward to inject into anything else. Two numbers
/// behind a small shared object avoids building a dependency cycle to read them.
/// </remarks>
public sealed class EngineWorkerState
{
    /// <summary>How many ticks have run since the service started.</summary>
    public long TickCount { get; set; }

    /// <summary>When the last tick completed. A stale value is what trips the watchdog.</summary>
    public DateTimeOffset LastTickCompleted { get; set; }
}
