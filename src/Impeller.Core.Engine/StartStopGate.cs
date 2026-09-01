using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine;

/// <summary>
/// Gets a stopped fan spinning again, and refuses to command a duty too low to sustain it.
/// </summary>
/// <remarks>
/// <para>
/// A fan has two speeds it cares about that a curve knows nothing about: the duty it needs to
/// break away from rest, which is higher, and the duty below which it stalls, which is lower.
/// Commanding 8% to a fan that needs 22% to start does not produce a slow fan. It produces a
/// stalled one — drawing current, reporting zero RPM, and moving no air at all, which is the
/// worst possible outcome for something whose job is cooling.
/// </para>
/// <para>
/// So this does two things. Below the stop threshold it commands zero rather than the threshold,
/// because a fan that is off is honest and a fan that is stalled is not. And on the way back up it
/// holds the start duty long enough for the fan to break away before handing control back to the
/// curve — watching the tach, where one is configured, rather than trusting the timer alone.
/// </para>
/// <para>Not thread-safe; owned by the tick loop, one per control.</para>
/// </remarks>
public sealed class StartStopGate(ControlBinding binding)
{
    /// <summary>
    /// How much to add each tick when the kick has run its course and the fan still is not
    /// turning. Small enough to creep, large enough to get somewhere.
    /// </summary>
    private const float CrawlStep = 3f;

    private readonly ControlBinding _binding = binding;

    private TimeSpan _kicking;

    /// <summary>Whether the gate is currently holding a fan at its start duty.</summary>
    public bool IsStarting => _kicking > TimeSpan.Zero;

    /// <summary>
    /// Works out what to actually write, given what the owner asked for and what the control is
    /// doing now.
    /// </summary>
    /// <param name="target">The duty the owner wants, already clamped to the binding's limits.</param>
    /// <param name="current">The duty standing at the control.</param>
    /// <param name="pairedRpm">
    /// The paired tach reading, or <see langword="null"/> when no fan sensor is paired with this
    /// control. Zero means the fan is not turning, which is the only direct evidence available
    /// that a start attempt has not worked.
    /// </param>
    /// <param name="elapsed">Time since the previous tick.</param>
    public Duty Resolve(Duty target, Duty current, float? pairedRpm, TimeSpan elapsed)
    {
        var threshold = _binding.StopThreshold;

        // Nothing configured: behave exactly as a binding with no start/stop knowledge.
        if (threshold <= 0f)
        {
            return _binding.ApplySlewLimit(current, target, elapsed);
        }

        if (target.Percent > threshold && NeedsStarting(current, threshold, pairedRpm))
        {
            _kicking += elapsed;

            if (_kicking < _binding.StartKickDuration)
            {
                // Hold the start duty. Ramping toward it defeats the point — the fan needs the
                // whole of it at once to break away.
                return _binding.StartDuty;
            }

            if (pairedRpm == 0f)
            {
                // The kick did not take and the tach proves it. Creep upward rather than sitting
                // at a start duty this particular fan has turned out not to respect.
                return new Duty(current.Percent + CrawlStep);
            }
        }
        else
        {
            _kicking = TimeSpan.Zero;
        }

        var next = _binding.ApplySlewLimit(current, target, elapsed);

        // Anything at or under the stop threshold is a stall waiting to happen. Off instead.
        return next.Percent <= threshold ? Duty.Off : next;
    }

    /// <summary>Discards the kick, so a reconfigured or resumed control starts one cleanly.</summary>
    public void Reset() => _kicking = TimeSpan.Zero;

    /// <summary>
    /// Whether the fan needs help getting going: it was stopped, or the tach says it is not
    /// turning, or — with no tach to consult — it is sitting at exactly the start duty, which is
    /// where a previous unsuccessful attempt would have left it.
    /// </summary>
    private bool NeedsStarting(Duty current, float threshold, float? pairedRpm) =>
        current.Percent <= threshold
        || pairedRpm == 0f
        || (pairedRpm is null && Math.Abs(current.Percent - _binding.StartDuty.Percent) < 0.01f);
}
