using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Ipc.Contracts;

/// <summary>Where the engine listens and the shell connects.</summary>
public static class ImpellerPipe
{
    /// <summary>
    /// The named pipe both ends use.
    /// </summary>
    /// <remarks>
    /// One constant shared by both projects rather than a string in each. A pipe name that only
    /// matches by coincidence fails as "the engine is not running", which is the least helpful
    /// possible symptom for the actual problem.
    /// </remarks>
    public const string Name = "Impeller.Engine";
}

/// <summary>What the engine is and how it is doing.</summary>
/// <param name="Version">The engine assembly's version, so a shell can notice a mismatch.</param>
/// <param name="TickCount">How many ticks have run since it started.</param>
/// <param name="LastTickCompleted">When the last tick finished.</param>
/// <param name="FailsafeEngaged">Whether every control is being held at its failsafe duty.</param>
/// <param name="ConfigurationRoot">Where this installation keeps its state, for bug reports.</param>
public sealed record EngineStatus(
    string Version,
    long TickCount,
    DateTimeOffset LastTickCompleted,
    bool FailsafeEngaged,
    string ConfigurationRoot);

/// <summary>A sensor as the shell needs to show it.</summary>
/// <param name="Id">Stable identity, and the only thing a configuration stores.</param>
/// <param name="Name">Provider-supplied name. Not unique, not identity.</param>
/// <param name="Kind">What it measures.</param>
/// <param name="ProviderId">Which backend produced it.</param>
/// <param name="HardwarePath">
/// A human-legible rendering of the fingerprint. Diagnostics only — never round-tripped, never
/// used to resolve anything.
/// </param>
/// <param name="Value">The latest reading, or null when it is not reporting.</param>
public sealed record SensorDescriptor(
    SensorId Id,
    string Name,
    SensorKind Kind,
    string ProviderId,
    string HardwarePath,
    float? Value);

/// <summary>A writable control, with whatever is currently driving it.</summary>
/// <param name="Id">Stable identity.</param>
/// <param name="Name">Provider-supplied name.</param>
/// <param name="ProviderId">Which backend produced it.</param>
/// <param name="HardwarePath">Diagnostic rendering of the fingerprint.</param>
/// <param name="CommandedDuty">The duty last written, or null if the engine has not written it.</param>
/// <param name="SupportsAutomaticMode">Whether it can be handed back to its own firmware.</param>
/// <param name="Owner">What sort of claimant is driving it.</param>
/// <param name="ClaimantId">Which specific claimant, where that means anything.</param>
public sealed record ControlDescriptor(
    SensorId Id,
    string Name,
    string ProviderId,
    string HardwarePath,
    Duty? CommandedDuty,
    bool SupportsAutomaticMode,
    ControlOwnerKind Owner,
    string? ClaimantId);

/// <summary>Everything the shell needs to draw itself from a standing start.</summary>
/// <param name="Status">Engine health.</param>
/// <param name="Sensors">Every sensor, controls included.</param>
/// <param name="Controls">Just the writable ones, with their ownership.</param>
/// <param name="ConfigurationName">Which configuration is in force.</param>
/// <param name="Configuration">Its contents.</param>
/// <param name="Validation">What checking it turned up when it was applied.</param>
/// <param name="AvailableConfigurations">Every configuration that could be loaded.</param>
public sealed record EngineSnapshot(
    EngineStatus Status,
    EquatableArray<SensorDescriptor> Sensors,
    EquatableArray<ControlDescriptor> Controls,
    string ConfigurationName,
    ImpellerConfiguration Configuration,
    ConfigurationValidation Validation,
    EquatableArray<string> AvailableConfigurations);

/// <summary>One sensor's current value.</summary>
/// <param name="Id">Which sensor.</param>
/// <param name="Value">Its reading, or null when it is not reporting.</param>
public readonly record struct SensorReading(SensorId Id, float? Value);

/// <summary>One control's current state.</summary>
/// <param name="Id">Which control.</param>
/// <param name="CommandedDuty">The duty standing at it.</param>
/// <param name="Owner">What sort of claimant is driving it.</param>
public readonly record struct ControlReading(SensorId Id, Duty? CommandedDuty, ControlOwnerKind Owner);

/// <summary>
/// What changed on one tick.
/// </summary>
/// <remarks>
/// Values only. Names, kinds and hardware paths do not change between ticks, so re-sending them at
/// the tick rate would be several hundred strings a second to say nothing new. The shell gets those
/// once in the snapshot and matches on id.
/// </remarks>
/// <param name="Tick">Which tick this is, so a client can notice it missed some.</param>
/// <param name="At">When the tick completed.</param>
/// <param name="Sensors">Every sensor's current value.</param>
/// <param name="Controls">Every control's current state.</param>
public sealed record TickSnapshot(
    long Tick,
    DateTimeOffset At,
    EquatableArray<SensorReading> Sensors,
    EquatableArray<ControlReading> Controls);

/// <summary>The outcome of trying to apply or load a configuration.</summary>
/// <param name="Applied">Whether it is now driving the engine.</param>
/// <param name="Name">Which configuration is in force after the attempt.</param>
/// <param name="Validation">Everything checking it turned up, warnings included.</param>
public sealed record ConfigurationResult(bool Applied, string Name, ConfigurationValidation Validation);

/// <summary>The outcome of trying to take a control.</summary>
/// <param name="Granted">Whether the claim succeeded.</param>
/// <param name="Failure">Why it did not. Meaningful only when <paramref name="Granted"/> is false.</param>
/// <param name="CurrentOwner">
/// What holds it instead, when the refusal was that someone else already does. Surfaced so a
/// conflict names the other claimant rather than failing anonymously.
/// </param>
/// <param name="CurrentClaimantId">Which specific claimant, where that means anything.</param>
public sealed record ControlAcquireOutcome(
    bool Granted,
    ControlAcquireFailure Failure,
    ControlOwnerKind? CurrentOwner,
    string? CurrentClaimantId);

/// <summary>
/// What the shell can ask the engine to do.
/// </summary>
/// <remarks>
/// Plain C# rather than a generated stub, which is the whole reason this channel is JSON-RPC
/// rather than the gRPC-and-protobuf the app being replaced uses: for one machine, two endpoints
/// and one language, a code generation step buys nothing and costs a build dependency.
/// </remarks>
public interface IEngineControl
{
    /// <summary>Everything needed to draw the UI from nothing.</summary>
    Task<EngineSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Engine health on its own, for a tray icon that does not need the rest.</summary>
    Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a configuration and, if it is sound, applies and saves it.
    /// </summary>
    /// <remarks>
    /// All or nothing. A configuration with errors never reaches the tick loop, so a bad edit
    /// leaves the fans on the last good one rather than half-switching into a broken state.
    /// </remarks>
    Task<ConfigurationResult> ApplyConfigurationAsync(
        ImpellerConfiguration configuration,
        CancellationToken cancellationToken = default);

    /// <summary>Checks a configuration without applying it, for an editor validating an edit.</summary>
    Task<ConfigurationValidation> ValidateConfigurationAsync(
        ImpellerConfiguration configuration,
        CancellationToken cancellationToken = default);

    /// <summary>Every configuration available to load.</summary>
    Task<EquatableArray<string>> ListConfigurationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads a stored configuration and applies it.</summary>
    Task<ConfigurationResult> LoadConfigurationAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a stored configuration. Returns false if it was not there.</summary>
    Task<bool> DeleteConfigurationAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a control and holds it at a duty until released.
    /// </summary>
    /// <remarks>
    /// Exclusive, and refused rather than queued if something else already holds it. A fan quietly
    /// changing which thing is driving it is a worse outcome than a rejected request the caller
    /// has to handle.
    /// </remarks>
    Task<ControlAcquireOutcome> SetManualDutyAsync(
        SensorId controlId,
        Duty duty,
        CancellationToken cancellationToken = default);

    /// <summary>Hands a manually held control back to its curve.</summary>
    Task<bool> ReleaseControlAsync(SensorId controlId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Spins one control up briefly so the user can work out which fan it is.
    /// </summary>
    /// <remarks>
    /// Returns as soon as the control has been taken; the engine hands it back on its own when the
    /// time is up, so a shell that crashes mid-identify does not leave a fan pinned.
    /// </remarks>
    Task<ControlAcquireOutcome> IdentifyControlAsync(
        SensorId controlId,
        Duty duty,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What the engine tells the shell without being asked.
/// </summary>
/// <remarks>
/// Implemented by the shell and attached as a callback target, so one duplex connection carries
/// both directions. There is no polling anywhere in this design.
/// </remarks>
public interface IEngineEvents
{
    /// <summary>A tick completed, with every current reading.</summary>
    Task OnTickAsync(TickSnapshot snapshot);

    /// <summary>The configuration in force changed — possibly from another shell, or the CLI.</summary>
    Task OnConfigurationChangedAsync(ConfigurationResult result);

    /// <summary>The set of available hardware changed, so the snapshot is stale.</summary>
    Task OnHardwareChangedAsync();
}
