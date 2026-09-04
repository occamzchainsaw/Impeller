using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine;
using Impeller.Core.Engine.Configuration;
using Impeller.Core.Engine.Tuning;
using Impeller.Core.Persistence.Diagnostics;
using Impeller.Core.Persistence.Legacy;
using Impeller.Ipc.Contracts;
using Impeller.Plugins.Abstractions;
using Impeller.Plugins.Host;

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
    EngineWorkerState workerState,
    TuningCoordinator tuning,
    EngineNotifications notifications,
    ISensorIdentityMap identityMap,
    PluginRegistry plugins,
    PluginHost pluginHost) : IEngineControl
{
    /// <summary>Who the engine records as holding a manually overridden control.</summary>
    public const string ManualClaimant = ControlOwnershipRegistry.ManualClaimant;

    private CancellationTokenSource? _tuningCancellation;

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
            // Through the loop rather than straight to the ownership registry. The registry knows
            // who holds what and nothing about configuration, so it would happily grant a claim on
            // a control the tick loop skips - and the pin would then be accepted, saved, and never
            // written to the fan.
            var acquired = loop.TryAcquire(controlId, ControlOwnerKind.ManualOverride, ManualClaimant);

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
    /// Saved, never applied. Reading the notes is the point of importing rather than guessing, and
    /// notes shown after the fans have already changed behaviour are notes shown too late.
    /// </remarks>
    public Task<ImportSummary> ImportConfigurationAsync(
        string path,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        // The live registry is handed over as well as the identity map: motherboard references
        // resolve through the map alone, but a graphics card is named rather than fingerprinted and
        // can only be matched against what is actually here.
        var importer = new FanControlConfigImporter(identityMap, registry);

        ImportResult imported;

        try
        {
            imported = importer.ImportFile(path, name);
        }
        catch (LegacyImportException ex)
        {
            return Task.FromResult(new ImportSummary(
                false,
                name ?? Path.GetFileNameWithoutExtension(path),
                ex.Message,
                [],
                0,
                0,
                0,
                ConfigurationValidation.Clean));
        }

        var configuration = imported.Configuration with { Name = Unused(imported.Configuration.Name) };
        var validation = ConfigurationValidator.Validate(configuration, registry);

        // Errors mean it could not be applied, not that it is not worth keeping. A configuration
        // whose curves point at hardware this machine does not have is exactly what someone
        // importing from another machine expects to fix by hand, and deleting it would leave them
        // nothing to fix.
        coordinator.Save(configuration);

        return Task.FromResult(new ImportSummary(
            true,
            configuration.Name,
            null,
            imported.Notes,
            configuration.Curves.Count,
            configuration.Controls.Count,
            configuration.CustomSensors.Count,
            validation));
    }

    /// <inheritdoc />
    public Task<TuningReport> CalibrateAsync(
        EquatableArray<SensorId> controlIds,
        CancellationToken cancellationToken = default) =>
        RunTuningAsync(token => tuning.CalibrateAsync(controlIds, token), cancellationToken);

    /// <inheritdoc />
    public Task<TuningReport> PairFansAsync(
        EquatableArray<SensorId> controlIds,
        CancellationToken cancellationToken = default) =>
        RunTuningAsync(token => tuning.PairAsync(controlIds, token), cancellationToken);

    /// <inheritdoc />
    public Task<bool> CancelTuningAsync(CancellationToken cancellationToken = default)
    {
        var cancellation = Volatile.Read(ref _tuningCancellation);

        if (cancellation is null)
        {
            return Task.FromResult(false);
        }

        cancellation.Cancel();
        return Task.FromResult(true);
    }

    /// <summary>
    /// Runs one tuning procedure, forwarding its progress to every attached shell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Progress is broadcast rather than returned to the caller, because a run takes over every fan
    /// in the machine: a second window open on the same engine has to be able to say what is
    /// happening rather than looking like it stopped responding.
    /// </para>
    /// <para>
    /// The token source is kept so <see cref="CancelTuningAsync"/> can reach it. That matters for
    /// the case the caller's own token cannot cover — a shell that crashed mid-run leaves every fan
    /// held at a baseline duty, and the next shell to connect needs a way to end it.
    /// </para>
    /// </remarks>
    private async Task<TuningReport> RunTuningAsync(
        Func<CancellationToken, Task<TuningReport>> run,
        CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Interlocked.Exchange(ref _tuningCancellation, cancellation)?.Dispose();

        void OnProgress(object? sender, TuningProgress progress) =>
            notifications.RaiseTuningProgress(progress);

        tuning.Progressed += OnProgress;

        try
        {
            return await run(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            tuning.Progressed -= OnProgress;
            Interlocked.CompareExchange(ref _tuningCancellation, null, cancellation);
        }
    }

    /// <summary>
    /// A configuration name nothing is saved under yet.
    /// </summary>
    /// <remarks>
    /// Importing the same file twice is an ordinary thing to do — usually after fixing something on
    /// the source machine — and silently overwriting the first attempt would take away the thing
    /// being compared against.
    /// </remarks>
    private string Unused(string name)
    {
        if (!coordinator.Exists(name))
        {
            return name;
        }

        for (var suffix = 2; suffix < 100; suffix++)
        {
            var candidate = $"{name} ({suffix})";

            if (!coordinator.Exists(candidate))
            {
                return candidate;
            }
        }

        return $"{name} ({Guid.NewGuid():N})";
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
            Plugins = [.. plugins.All.Select(Summarise)],
        });

    /// <inheritdoc />
    public Task<EquatableArray<PluginSummary>> ListPluginsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<EquatableArray<PluginSummary>>([.. plugins.All.Select(Summarise)]);

    /// <inheritdoc />
    public Task<bool> ApprovePluginAsync(
        string pluginId,
        EquatableArray<PluginCapability> capabilities,
        EquatableArray<SensorId> controls,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(plugins.Approve(pluginId, capabilities, controls));

    /// <inheritdoc />
    public Task<bool> GrantPluginControlAsync(
        string pluginId,
        SensorId controlId,
        CancellationToken cancellationToken = default) =>
        // Refused rather than stored, because a grant on a fan the engine is not driving is a
        // permission that fails the first time it is used, and the user would have no idea why.
        Task.FromResult(loop.CanBeHeldByPlugin(controlId) && plugins.Grant(pluginId, controlId));

    /// <inheritdoc />
    public Task<bool> RevokePluginControlAsync(
        string pluginId,
        SensorId controlId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(plugins.Revoke(pluginId, controlId));

    /// <inheritdoc />
    public Task<bool> SetPluginEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(plugins.SetEnabled(pluginId, enabled));

    /// <inheritdoc />
    public Task<bool> ForgetPluginAsync(
        string pluginId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(plugins.Forget(pluginId));

    /// <summary>One stored plugin record, plus the two things about it that are not stored.</summary>
    private PluginSummary Summarise(PluginRecord record) => new(
        record.Id,
        record.DisplayName,
        record.Version,
        record.State,
        record.Enabled,
        pluginHost.Find(record.Id) is not null,
        [.. record.Requested],
        [.. record.Capabilities],
        [.. record.Controls],
        record.LastSeen.ImagePath,
        record.LastSeen.UserSid,
        !record.IdentityHolds,
        record.FirstSeenAt,
        record.LastSeenAt);

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
