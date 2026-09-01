using System.Reflection;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine;
using Impeller.Core.Engine.Configuration;
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
    /// <summary>
    /// Who the engine records as holding a manually overridden control.
    /// </summary>
    /// <remarks>
    /// One claimant id for every shell rather than one each, so closing a window and opening
    /// another does not strand a fan under a claim nothing can release.
    /// </remarks>
    public const string ManualClaimant = "shell";

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
    public Task<ControlAcquireOutcome> SetManualDutyAsync(
        SensorId controlId,
        Duty duty,
        CancellationToken cancellationToken = default)
    {
        if (registry.GetControl(controlId) is null)
        {
            return Task.FromResult(Refused(ControlAcquireFailure.UnknownControl, null));
        }

        var owner = ownership.GetOwner(controlId);

        // Already ours: adjusting a slider should not need the claim taken again.
        if (owner.Kind != ControlOwnerKind.ManualOverride)
        {
            var acquired = ownership.TryAcquire(controlId, ControlOwnerKind.ManualOverride, ManualClaimant);

            if (!acquired.Succeeded)
            {
                return Task.FromResult(Refused(acquired.Failure, acquired.CurrentOwner));
            }
        }

        loop.TrySetRequestedDuty(controlId, duty, ManualClaimant);
        return Task.FromResult(new ControlAcquireOutcome(true, default, ControlOwnerKind.ManualOverride, ManualClaimant));
    }

    /// <inheritdoc />
    public Task<bool> ReleaseControlAsync(SensorId controlId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ownership.Release(controlId, ManualClaimant));

    /// <inheritdoc />
    public async Task<ControlAcquireOutcome> IdentifyControlAsync(
        SensorId controlId,
        Duty duty,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var outcome = await SetManualDutyAsync(controlId, duty, cancellationToken).ConfigureAwait(false);

        if (!outcome.Granted)
        {
            return outcome;
        }

        // Released by the engine on a timer rather than by the caller. A shell that crashes
        // halfway through identifying a fan must not leave it pinned at full speed forever.
        _ = ReleaseAfterAsync(controlId, duration);

        return outcome;
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
