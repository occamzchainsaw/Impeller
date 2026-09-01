namespace Impeller.Core.Abstractions;

/// <summary>
/// A source of sensors and controls: the LibreHardwareMonitor backend, a vendor SDK
/// wrapper, or a plugin exposing its own hardware.
/// </summary>
/// <remarks>
/// Providers are polled by the engine, never the other way round. <see cref="RefreshAsync"/>
/// is called on a cadence the engine chooses (see <see cref="PollInterval"/>), so a slow
/// provider degrades its own freshness rather than stalling the tick loop.
/// </remarks>
public interface ISensorProvider : IAsyncDisposable
{
    /// <summary>
    /// Stable provider id, forming the first component of every fingerprint it mints.
    /// Lowercase, no spaces, for example <c>lhm</c>.
    /// </summary>
    string ProviderId { get; }

    /// <summary>Human-readable name for the settings UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// How often this provider wants refreshing. Storage SMART data is expensive to read and
    /// changes slowly, so it asks for a long interval; motherboard sensors ask for the tick rate.
    /// </summary>
    TimeSpan PollInterval { get; }

    /// <summary>Every sensor currently exposed, including those backing controls.</summary>
    IReadOnlyList<ISensor> Sensors { get; }

    /// <summary>The subset of sensors that can be written to.</summary>
    IReadOnlyList<IControl> Controls { get; }

    /// <summary>
    /// Brings the provider up: opens hardware, enumerates sensors, mints fingerprints.
    /// </summary>
    /// <returns>The outcome, including any hardware groups that failed to initialise.</returns>
    Task<ProviderInitializationResult> InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Re-reads current values into the existing sensor instances.
    /// </summary>
    /// <remarks>
    /// Must not add or remove sensors: identity is established at initialisation. A provider that
    /// detects hardware appearing or disappearing raises <see cref="TopologyChanged"/> instead,
    /// letting the engine re-initialise it on its own terms.
    /// </remarks>
    Task RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Raised when the set of available hardware has changed and the provider needs
    /// re-initialising — a USB fan controller unplugged, a GPU waking from sleep.
    /// </summary>
    event EventHandler? TopologyChanged;
}

/// <summary>
/// A provider whose sensors are computed from other providers' sensors rather than read from
/// hardware.
/// </summary>
/// <remarks>
/// The registry refreshes these after everything else, and one at a time. Both matter: a sensor
/// derived from a temperature that has not been re-read yet would lag a tick behind the hardware
/// it claims to describe, and one derived from another derived sensor would lag further still. The
/// ordering makes a chain of them correct within a single tick.
/// </remarks>
public interface IDerivedSensorProvider : ISensorProvider;

/// <summary>The outcome of bringing a provider up.</summary>
/// <param name="Succeeded">Whether the provider is usable at all.</param>
/// <param name="SensorCount">How many sensors were enumerated.</param>
/// <param name="ControlCount">How many of those are writable.</param>
/// <param name="FailedGroups">
/// Hardware groups that could not be read, by display name. A provider can succeed overall while
/// individual groups fail — a missing GPU driver should not cost you motherboard fan control.
/// </param>
/// <param name="Error">The failure, when <paramref name="Succeeded"/> is false.</param>
public sealed record ProviderInitializationResult(
    bool Succeeded,
    int SensorCount,
    int ControlCount,
    IReadOnlyList<string> FailedGroups,
    Exception? Error = null)
{
    /// <summary>A clean, fully successful initialisation.</summary>
    public static ProviderInitializationResult Success(int sensorCount, int controlCount) =>
        new(true, sensorCount, controlCount, []);

    /// <summary>A successful initialisation in which some hardware groups were unavailable.</summary>
    public static ProviderInitializationResult Partial(
        int sensorCount,
        int controlCount,
        IReadOnlyList<string> failedGroups) =>
        new(true, sensorCount, controlCount, failedGroups);

    /// <summary>A provider that could not start at all.</summary>
    public static ProviderInitializationResult Failed(Exception error) =>
        new(false, 0, 0, [], error);
}
