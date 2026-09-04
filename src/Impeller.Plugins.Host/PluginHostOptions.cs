using Impeller.Plugins.Abstractions;

namespace Impeller.Plugins.Host;

/// <summary>
/// The timings the plugin channel runs on.
/// </summary>
/// <remarks>
/// <para>
/// Every value here is deliberately slower than the engine's own watchdog. A plugin must never be
/// able to be the thing that trips <c>EngineOptions.TickTimeout</c>: the tick loop is what keeps
/// the fans under control, and a health check that could take it down would make connecting a
/// plugin a thermal risk rather than a feature.
/// </para>
/// <para>
/// Overridable mainly so tests can compress them. In the service they are left alone.
/// </para>
/// </remarks>
public sealed class PluginHostOptions
{
    /// <summary>How long a connection has to say hello before it is dropped.</summary>
    public TimeSpan HandshakeDeadline { get; init; } = PluginProtocol.HandshakeDeadline;

    /// <summary>How often an admitted plugin is asked whether it is still there.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long one ping may take before it counts as missed.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many pings in a row may go unanswered before the session is dropped.
    /// </summary>
    /// <remarks>
    /// Consecutive, not cumulative. A plugin that misses one ping an hour is not eventually
    /// condemned for it — that counter-that-never-decays is exactly the bug that makes
    /// FanControl declare a working plugin dead after five hours.
    /// </remarks>
    public int UnhealthyAfterMissedPings { get; init; } = 3;

    /// <summary>
    /// How long an existing session gets to answer when a second connection claims its id.
    /// </summary>
    /// <remarks>
    /// Much shorter than the ordinary ping timeout, because a person is waiting: this fires when
    /// someone restarts their plugin after it hung, and the old session is almost certainly gone.
    /// </remarks>
    public TimeSpan EvictionPingTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many notifications may be queued for one plugin before the oldest are dropped.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget broadcast is unbounded by nature: a plugin that stops reading its end of the
    /// pipe grows the transport's buffers without limit, which is unbounded memory growth
    /// <em>in the engine</em>, caused by a plugin. The cap turns that into a dropped notification
    /// and an unhealthy session.
    /// </remarks>
    public int MaxQueuedNotifications { get; init; } = 64;

    /// <summary>
    /// How many connections may sit un-handshaken at once.
    /// </summary>
    /// <remarks>
    /// Counted separately from admitted sessions and kept well below the pipe's instance limit.
    /// Without a cap, any local process can open connections and never speak until the pipe is
    /// full - a denial of service on the whole plugin channel whose only symptom is that plugins
    /// stop being able to connect.
    /// </remarks>
    public int MaxUnannouncedConnections { get; init; } = 4;

    /// <summary>
    /// How long a refused connection is left open before it is closed.
    /// </summary>
    /// <remarks>
    /// A refusal is the answer to a call still in flight, so closing the connection the instant the
    /// decision is made destroys the very sentence that tells a plugin author what is wrong -
    /// they see a dropped connection instead of "use reverse DNS with at least three labels". The
    /// linger is what makes the refusal arrive; it is short because a refused connection that never
    /// closes is a connection anyone can accumulate.
    /// </remarks>
    public TimeSpan RefusedLinger { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long every plugin together gets to acknowledge shutdown.
    /// </summary>
    /// <remarks>
    /// In total, not each. A service that a plugin can keep alive is a machine that cannot be
    /// rebooted, so this is a courtesy with a hard edge.
    /// </remarks>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromMilliseconds(500);
}
