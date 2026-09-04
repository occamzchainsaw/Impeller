using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Plugins.Abstractions;

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
/// <param name="Name">Provider-supplied name, on its own. Not unique, not identity.</param>
/// <param name="DisplayName">
/// What to show: the name the user gave it, or <paramref name="Name"/> when they have not given
/// one. Resolved by the engine so every surface agrees, including plugins.
/// </param>
/// <param name="HardwareName">
/// What it belongs to, for example <c>Nuvoton NCT6687D</c>. Kept separate from the name so a
/// surface that already says which hardware it is showing does not print it twice.
/// </param>
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
    string DisplayName,
    string HardwareName,
    SensorKind Kind,
    string ProviderId,
    string HardwarePath,
    float? Value);

/// <summary>A writable control, with whatever is currently driving it.</summary>
/// <param name="Id">Stable identity.</param>
/// <param name="Name">Provider-supplied name, on its own.</param>
/// <param name="DisplayName">The user's name for it, or the provider's when they have not given one.</param>
/// <param name="HardwareName">What it belongs to, kept separate from the name.</param>
/// <param name="ProviderId">Which backend produced it.</param>
/// <param name="HardwarePath">Diagnostic rendering of the fingerprint.</param>
/// <param name="CommandedDuty">The duty last written, or null if the engine has not written it.</param>
/// <param name="SupportsAutomaticMode">Whether it can be handed back to its own firmware.</param>
/// <param name="Owner">What sort of claimant is driving it.</param>
/// <param name="ClaimantId">Which specific claimant, where that means anything.</param>
/// <param name="Claimable">
/// Whether a plugin may be granted this fan: the engine drives it, and it has a curve to fall back
/// to when the plugin lets go or dies.
/// </param>
/// <remarks>
/// <paramref name="Claimable"/> is the engine's own answer, not an approximation of it. The rule
/// lives in the tick loop, and a shell re-deriving it from a commanded duty gets it wrong for
/// exactly the fan someone is most likely to try: one switched on and pinned by hand, with no
/// curve behind it.
/// </remarks>
public sealed record ControlDescriptor(
    SensorId Id,
    string Name,
    string DisplayName,
    string HardwareName,
    string ProviderId,
    string HardwarePath,
    Duty? CommandedDuty,
    bool SupportsAutomaticMode,
    ControlOwnerKind Owner,
    string? ClaimantId,
    bool Claimable);

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
/// <param name="ClaimantId">
/// Which specific claimant, where that means anything - a plugin's manifest id, or the shell's own
/// name for a manual pin. Null for a curve-driven control, which is most of them most of the time.
/// </param>
/// <remarks>
/// The claimant travels on every tick rather than only in a snapshot, because ownership changes
/// between snapshots and a card that could only say "a plugin" without saying which would send the
/// user hunting. It is a nullable string that is null in the ordinary case, which is the cheapest
/// thing that could carry it.
/// </remarks>
public readonly record struct ControlReading(
    SensorId Id,
    Duty? CommandedDuty,
    ControlOwnerKind Owner,
    string? ClaimantId);

/// <summary>One curve's output this tick.</summary>
/// <param name="Id">Which curve.</param>
/// <param name="Output">
/// What it is asking for, or null when it could not produce a value — its sensor is not reporting,
/// or a curve it depends on could not answer either.
/// </param>
/// <remarks>
/// The engine has computed this every tick since Phase 0 and threw it away after resolving the
/// controls. It is carried now because a curve editor that cannot show what the curve is doing
/// right now is a form, and one that can is something you can watch respond — which is the only
/// way to tell a badly shaped curve from a well shaped one without waiting for the machine to get
/// hot.
/// </remarks>
public readonly record struct CurveReading(CurveId Id, Duty? Output);

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
/// <param name="Curves">
/// Every curve's output. Defaulted rather than required, so a client built against the older shape
/// keeps deserialising — the additive-versioning habit the plugin protocol was designed around.
/// </param>
public sealed record TickSnapshot(
    long Tick,
    DateTimeOffset At,
    EquatableArray<SensorReading> Sensors,
    EquatableArray<ControlReading> Controls,
    EquatableArray<CurveReading> Curves = default);

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

/// <summary>What an import of a legacy configuration did.</summary>
/// <param name="Succeeded">Whether a configuration was produced and saved.</param>
/// <param name="Name">What it was saved as.</param>
/// <param name="Failure">Why nothing was produced. Null when it worked.</param>
/// <param name="Notes">Everything the importer wants the user to know, in the order it was found.</param>
/// <param name="Curves">How many curves came across.</param>
/// <param name="Controls">How many controls.</param>
/// <param name="CustomSensors">How many computed sensors.</param>
/// <param name="Validation">What checking the result turned up.</param>
/// <remarks>
/// The notes are the point of this type rather than decoration on it. An import that silently drops
/// a curve, or resolves a sensor to the wrong fan, is worse than one that refuses: the fans quietly
/// do the wrong thing and the user has no reason to look.
/// </remarks>
public sealed record ImportSummary(
    bool Succeeded,
    string Name,
    string? Failure,
    EquatableArray<ImportNote> Notes,
    int Curves,
    int Controls,
    int CustomSensors,
    ConfigurationValidation Validation);

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

    /// <summary>
    /// Reads a FanControl configuration file and saves what it says as a new configuration.
    /// </summary>
    /// <param name="path">The file to read. Opened for reading and never modified.</param>
    /// <param name="name">What to save it as, or null to use the file's own name.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Saved but not applied. The engine keeps running whatever it was running, and the user loads
    /// the import once they have read the notes — which is the whole reason the notes exist, and
    /// would be pointless if the fans had already changed behaviour by the time they saw them.
    /// </remarks>
    Task<ImportSummary> ImportConfigurationAsync(
        string path,
        string? name = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Measures each of these controls: its duty-to-speed table, and the duties that start and
    /// stall it.
    /// </summary>
    /// <remarks>
    /// Minutes long, and it takes over the fans while it runs. Progress arrives on
    /// <see cref="IEngineEvents.OnTuningProgressAsync"/>; cancelling the request stops the run and
    /// keeps whatever had already been measured.
    /// </remarks>
    Task<TuningReport> CalibrateAsync(
        EquatableArray<SensorId> controlIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Works out which fan-speed sensor belongs to which of these controls, by moving one fan at a
    /// time and watching what changes.
    /// </summary>
    Task<TuningReport> PairFansAsync(
        EquatableArray<SensorId> controlIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops whatever tuning run is going.
    /// </summary>
    /// <remarks>
    /// Separate from cancelling the request that started it, because the shell that started it may
    /// not be the one asking — a crashed window leaves a run holding every fan at its baseline, and
    /// this is how the next window gets them back.
    /// </remarks>
    Task<bool> CancelTuningAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Collects everything worth knowing about this engine into one attachable document.
    /// </summary>
    /// <remarks>
    /// Built on request rather than kept up to date. It is what someone attaches to a bug report,
    /// and the thing that decides whether a problem can be diagnosed by whoever reads it.
    /// </remarks>
    Task<DiagnosticReport> GetDiagnosticReportAsync(CancellationToken cancellationToken = default);

    /// <summary>Every plugin the engine has ever seen, approved or not.</summary>
    Task<EquatableArray<PluginSummary>> ListPluginsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a plugin, granting capabilities and specific fans.
    /// </summary>
    /// <remarks>
    /// The grant is bound to the program and account the plugin last connected from, so approving
    /// one that has never connected is refused rather than granted against nothing.
    /// </remarks>
    /// <returns>False when there is no such plugin.</returns>
    Task<bool> ApprovePluginAsync(
        string pluginId,
        EquatableArray<PluginCapability> capabilities,
        EquatableArray<SensorId> controls,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants one more fan to an already-approved plugin.
    /// </summary>
    /// <remarks>
    /// Refused when the engine is not driving that fan. A plugin may only hold a control that is
    /// enabled and has a curve to fall back to, and granting one that is neither would produce a
    /// permission that fails the moment it is used.
    /// </remarks>
    Task<bool> GrantPluginControlAsync(
        string pluginId,
        SensorId controlId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes one fan back. The plugin stays connected and keeps everything else.
    /// </summary>
    /// <remarks>
    /// The narrowest of the three verbs and the one to reach for first. Disabling a plugin because
    /// you wanted one fan back is how a user ends up with a plugin they have forgotten they
    /// switched off.
    /// </remarks>
    Task<bool> RevokePluginControlAsync(
        string pluginId,
        SensorId controlId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns a plugin off, or back on.
    /// </summary>
    /// <remarks>
    /// A disabled plugin loses its fans, has its connection closed, and is refused at the
    /// handshake until it is enabled again. Its grants are kept, so switching it back on is not
    /// configuring it again.
    /// </remarks>
    Task<bool> SetPluginEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops a plugin from the record entirely, so the next connection prompts as if it were new.
    /// </summary>
    /// <remarks>
    /// The destructive verb, and distinct from disabling: a forgotten plugin can come straight back
    /// by connecting, where a disabled one cannot until the user says so.
    /// </remarks>
    Task<bool> ForgetPluginAsync(string pluginId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives a sensor or fan the user's own name, or clears it.
    /// </summary>
    /// <param name="sensorId">Which one.</param>
    /// <param name="name">What to call it. Null or blank restores the provider's own name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// Stored against this machine rather than in the configuration, so it survives switching
    /// profiles and does not travel when one is copied elsewhere. Nothing keys off a name, so
    /// renaming can never break a binding.
    /// </remarks>
    /// <returns>False when nothing changed.</returns>
    Task<bool> RenameAsync(SensorId sensorId, string? name, CancellationToken cancellationToken = default);
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

    /// <summary>
    /// A plugin appeared, disappeared, or had its permissions changed.
    /// </summary>
    /// <remarks>
    /// Carries nothing. The set is small, changes are rare and user-driven, and a payload would go
    /// stale between being built and being read — so the shell is told that something moved and
    /// asks what the truth is now.
    /// </remarks>
    Task OnPluginsChangedAsync();

    /// <summary>A tuning run said what it is doing.</summary>
    /// <remarks>
    /// Broadcast rather than sent to the caller. A calibration takes over every fan in the machine
    /// for several minutes, so a second window open on the same engine needs to be able to say what
    /// is happening rather than appearing to have stopped responding.
    /// </remarks>
    Task OnTuningProgressAsync(TuningProgress progress);
}
