using Impeller.Core.Abstractions;
using Impeller.Ipc.Contracts;

namespace Impeller.EngineService.Ipc;

/// <summary>
/// The seam between the engine and anything watching it.
/// </summary>
/// <remarks>
/// The tick loop should not know that an RPC channel exists, and the RPC channel should not have
/// to poll the tick loop to find out what it did. One small object in between keeps both true, and
/// keeps the engine's behaviour testable without a pipe.
/// </remarks>
public sealed class EngineNotifications
{
    /// <summary>Raised after each tick, with every current reading.</summary>
    public event EventHandler<TickSnapshot>? Ticked;

    /// <summary>Raised when the configuration in force has changed.</summary>
    public event EventHandler<ConfigurationResult>? ConfigurationChanged;

    /// <summary>Raised when the set of available hardware has changed.</summary>
    public event EventHandler? HardwareChanged;

    /// <summary>Raised on each sample of a tuning run.</summary>
    public event EventHandler<TuningProgress>? TuningProgressed;

    /// <summary>Raised when a plugin connected, went away, or had its permissions changed.</summary>
    public event EventHandler? PluginsChanged;

    /// <summary>
    /// Whether anything is listening for ticks.
    /// </summary>
    /// <remarks>
    /// Checked before a snapshot is built, not after. An unattended machine has no reason to
    /// serialise several hundred readings a second into nothing.
    /// </remarks>
    public bool HasTickListeners => Ticked is not null;

    /// <summary>Announces a completed tick.</summary>
    public void RaiseTick(TickSnapshot snapshot) => Ticked?.Invoke(this, snapshot);

    /// <summary>Announces a configuration change.</summary>
    public void RaiseConfigurationChanged(ConfigurationResult result) =>
        ConfigurationChanged?.Invoke(this, result);

    /// <summary>Announces that the available hardware has changed.</summary>
    public void RaiseHardwareChanged() => HardwareChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Announces where a tuning run has got to.</summary>
    public void RaiseTuningProgress(TuningProgress progress) =>
        TuningProgressed?.Invoke(this, progress);

    /// <summary>Announces that something about the plugins has changed.</summary>
    public void RaisePluginsChanged() => PluginsChanged?.Invoke(this, EventArgs.Empty);
}
