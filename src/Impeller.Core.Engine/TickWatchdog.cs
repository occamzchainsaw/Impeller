namespace Impeller.Core.Engine;

/// <summary>What the watchdog thinks of the engine's health right now.</summary>
public enum WatchdogVerdict
{
    /// <summary>Ticks are arriving on time.</summary>
    Healthy = 0,

    /// <summary>The deadline has just been missed. Engage the failsafe.</summary>
    JustTripped,

    /// <summary>Still overdue, and the failsafe is already engaged. Do nothing further.</summary>
    StillTripped,

    /// <summary>Ticks have resumed after a lapse. Hand the fans back to their curves.</summary>
    Recovered,
}

/// <summary>
/// Notices when the tick loop has stopped ticking.
/// </summary>
/// <remarks>
/// <para>
/// This covers the failure the service manager cannot see: a process that is still running, still
/// responding to service control, and no longer doing the one thing it exists to do. A crashed
/// engine gets restarted by the SCM and applies its failsafe on the way out; a <em>hung</em> one
/// just leaves every fan at whatever duty it last wrote, with nothing maintaining it and nothing
/// noticing. On most consumer boards nothing else refreshes PWM once the software driving it stops,
/// so "stuck at 20% while the load climbs" is a real outcome rather than a hypothetical one.
/// </para>
/// <para>
/// Deliberately just the decision, with no timer and no thread of its own, so the rule can be
/// tested at any speed. The caller supplies the clock and does the acting — and should do its
/// polling somewhere that a stuck tick loop cannot also stall.
/// </para>
/// <para>
/// It is not a substitute for the separate watchdog process the architecture calls for. An
/// in-process check cannot survive the process dying. It covers the case that one does not.
/// </para>
/// </remarks>
public sealed class TickWatchdog(TimeSpan timeout)
{
    /// <summary>How overdue a tick must be before the engine is considered hung.</summary>
    public TimeSpan Timeout { get; } = timeout;

    /// <summary>Whether the watchdog currently believes the engine is stuck.</summary>
    public bool IsTripped { get; private set; }

    /// <summary>
    /// Judges the engine's health, and reports transitions rather than states so the caller can
    /// act exactly once on each.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="lastTickCompleted">When the tick loop last finished a pass.</param>
    public WatchdogVerdict Evaluate(DateTimeOffset now, DateTimeOffset lastTickCompleted)
    {
        // A timeout of zero or less disables the watchdog rather than tripping constantly, which
        // is the only sane reading of someone configuring it that way.
        if (Timeout <= TimeSpan.Zero)
        {
            return WatchdogVerdict.Healthy;
        }

        var overdue = now - lastTickCompleted > Timeout;

        if (overdue)
        {
            if (IsTripped)
            {
                return WatchdogVerdict.StillTripped;
            }

            IsTripped = true;
            return WatchdogVerdict.JustTripped;
        }

        if (!IsTripped)
        {
            return WatchdogVerdict.Healthy;
        }

        IsTripped = false;
        return WatchdogVerdict.Recovered;
    }

    /// <summary>Forgets a trip, for a caller that has handled recovery its own way.</summary>
    public void Clear() => IsTripped = false;
}
