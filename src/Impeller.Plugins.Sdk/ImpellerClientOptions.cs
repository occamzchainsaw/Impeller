using Impeller.Plugins.Abstractions;

namespace Impeller.Plugins.Sdk;

/// <summary>How a plugin stands relative to the engine.</summary>
/// <remarks>
/// Five states, not two, because the useful next step differs for every one of them and a plugin
/// that collapses them into "connected or not" makes its user guess. Telling
/// <see cref="AwaitingApproval"/> from <see cref="Refused"/> matters most: the first means go and
/// click something in Impeller, the second means nothing you do in this window will help.
/// </remarks>
public enum ImpellerConnectionState
{
    /// <summary>Not connected. Impeller may not be running.</summary>
    Disconnected = 0,

    /// <summary>Trying to reach the engine.</summary>
    Connecting,

    /// <summary>
    /// Connected, and waiting for the user to approve this plugin in Impeller.
    /// </summary>
    /// <remarks>
    /// A success, not a failure. Every plugin starts here the first time it runs, and one that
    /// treats this as an error and exits leaves nothing in the list for the user to approve.
    /// </remarks>
    AwaitingApproval,

    /// <summary>Connected, approved, and able to claim whatever fans were granted.</summary>
    Ready,

    /// <summary>Turned away. <see cref="ImpellerClient.StatusMessage"/> says why, in a sentence.</summary>
    Refused,
}

/// <summary>A fan this plugin was holding, and why it is no longer holding it.</summary>
public sealed class ControlLostEventArgs(SensorRef control, ControlLostReason reason) : EventArgs
{
    /// <summary>Which fan.</summary>
    public SensorRef Control { get; } = control;

    /// <summary>What took it.</summary>
    public ControlLostReason Reason { get; } = reason;

    /// <summary>
    /// Whether re-acquiring is a reasonable thing to try shortly.
    /// </summary>
    /// <remarks>
    /// False for a failsafe — the engine is in trouble and the fan is at its safe duty, which is
    /// where it should stay — and for anything the user did, because taking back a fan somebody
    /// just took from you is a fight the user did not ask for.
    /// </remarks>
    public bool WorthRetrying => Reason is ControlLostReason.LeaseExpired;
}

/// <summary>
/// Hardware a plugin contributes, for when the engine can drive it.
/// </summary>
/// <remarks>
/// <para>
/// The engine side of the provider role is not built. This interface exists now anyway, because the
/// shape it forces cannot be added later without breaking every plugin that shipped without it.
/// </para>
/// <para>
/// <strong><see cref="ApplyFailsafe"/> is the reason.</strong> The engine cannot put contributed
/// hardware into a safe state when the connection drops: writing to it means posting a message to a
/// process that may well be the reason the engine is failsafing, and if that process is gone the
/// fan is stuck with nothing in the engine able to move it. The only thing that can save it is code
/// on this side, so an SDK that did not demand a local failsafe would be an SDK that quietly made
/// stuck fans possible.
/// </para>
/// </remarks>
public interface IPluginHardware
{
    /// <summary>Drives one contributed control. Called by the engine while connected.</summary>
    void Write(SensorRef control, float percent);

    /// <summary>
    /// Puts every contributed control into its declared safe state. Called on every disconnect.
    /// </summary>
    /// <remarks>
    /// Called on the connection's thread and must not block. Whatever it does needs to be safe to
    /// do repeatedly, since a flapping connection will call it repeatedly.
    /// </remarks>
    void ApplyFailsafe();
}

/// <summary>Timings, and the optional hardware hook.</summary>
/// <remarks>
/// The defaults suit an application with a window. A headless plugin on a machine that boots slowly
/// may want a longer <see cref="MaximumRetryDelay"/>; nothing else here is usually worth changing.
/// </remarks>
public sealed class ImpellerClientOptions
{
    /// <summary>How long one connection attempt may take before it counts as a failure.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How soon to retry after the first failure, and after any dropped connection.</summary>
    public TimeSpan MinimumRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long to wait for an answer to the handshake before giving up and reconnecting.
    /// </summary>
    /// <remarks>
    /// Generous next to the engine's own five-second deadline for a plugin that never speaks. This
    /// guards the mirror-image failure: a connection accepted by something that then says nothing,
    /// which without a bound would leave a plugin waiting forever with its user unable to tell that
    /// from a slow machine.
    /// </remarks>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The longest the backoff grows to.
    /// </summary>
    /// <remarks>
    /// Bounded rather than unbounded, because the common case for a long outage is the user
    /// deliberately restarting the service, and a plugin that had backed off to five minutes would
    /// appear broken for five minutes after they brought it back.
    /// </remarks>
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Hardware this plugin contributes, or null — which is the ordinary case.
    /// </summary>
    /// <remarks>
    /// Supplying this does not make the engine drive your hardware; that half is not built. What it
    /// does today is guarantee <see cref="IPluginHardware.ApplyFailsafe"/> runs whenever the
    /// connection drops.
    /// </remarks>
    public IPluginHardware? Hardware { get; init; }

    /// <summary>
    /// Where to get the connection from, for tests. Null means the engine's named pipe.
    /// </summary>
    /// <remarks>
    /// Internal rather than public because a plugin has exactly one place to connect to and
    /// offering a choice would only invite someone to point it somewhere wrong. It exists so the
    /// client's own behaviour - the handshake, the reconnect loop, what it does when a fan is taken
    /// away - can be exercised against a real engine over an in-memory stream, which is the only
    /// way to test any of it without a service and a pipe.
    /// </remarks>
    internal Func<CancellationToken, Task<Stream>>? Transport { get; init; }
}
