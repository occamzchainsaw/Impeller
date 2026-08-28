namespace Impeller.Core.Engine;

/// <summary>
/// Settings for a <see cref="HysteresisGate"/>.
/// </summary>
/// <param name="DeadbandUp">
/// How far the input must rise above the last accepted value before a rise is considered real,
/// in the sensor's own units. Zero disables the rising deadband.
/// </param>
/// <param name="DeadbandDown">How far the input must fall before a fall is considered real.</param>
/// <param name="ResponseUp">
/// How long a rise must persist beyond the deadband before it is accepted. Debounces a
/// brief load spike into no fan change at all.
/// </param>
/// <param name="ResponseDown">
/// How long a fall must persist before it is accepted. Usually set longer than
/// <paramref name="ResponseUp"/>: ramping up promptly is a thermal necessity, while ramping
/// down eagerly just makes the fan audible as it hunts.
/// </param>
public readonly record struct HysteresisSettings(
    float DeadbandUp = 0f,
    float DeadbandDown = 0f,
    TimeSpan ResponseUp = default,
    TimeSpan ResponseDown = default)
{
    /// <summary>Pass-through settings: every input is accepted immediately.</summary>
    public static HysteresisSettings None => new();
}

/// <summary>
/// Suppresses input changes that are too small or too brief to be worth acting on.
/// </summary>
/// <remarks>
/// <para>
/// Two independent mechanisms, applied in order. The <em>deadband</em> ignores changes smaller
/// than a threshold, so a temperature oscillating by a fraction of a degree never reaches the
/// curve. The <em>response time</em> then requires a change beyond that deadband to persist for
/// a while before it is accepted, so a momentary spike does not produce an audible blip.
/// </para>
/// <para>
/// Rising and falling are configured separately, because they are not symmetric problems: a
/// missed rise is a thermal risk, a missed fall is only a missed opportunity to be quieter.
/// </para>
/// <para>Not thread-safe; owned by the single curve that uses it, on the tick loop's thread.</para>
/// </remarks>
public sealed class HysteresisGate(HysteresisSettings settings)
{
    private readonly HysteresisSettings _settings = settings;

    private float? _accepted;
    private float? _pending;
    private TimeSpan _pendingFor;

    /// <summary>The most recently accepted value, or <see langword="null"/> before the first input.</summary>
    public float? AcceptedValue => _accepted;

    /// <summary>
    /// Offers a new reading to the gate.
    /// </summary>
    /// <param name="value">The current sensor reading.</param>
    /// <param name="elapsed">Time since the previous call, used to age any pending change.</param>
    /// <param name="bypassSuppression">
    /// Skips both mechanisms and accepts immediately. The engine sets this when the input sits at
    /// the extreme end of a curve's configured range, where waiting is actively harmful — at the
    /// top of a curve the fan should already be at full, not debouncing its way there.
    /// </param>
    /// <returns>
    /// The value the curve should evaluate against: the new reading if it was accepted, otherwise
    /// the previously accepted one. <see langword="null"/> only before any reading has arrived.
    /// </returns>
    public float? Offer(float value, TimeSpan elapsed, bool bypassSuppression = false)
    {
        // First reading establishes the baseline; there is nothing to compare against yet.
        if (_accepted is not { } accepted)
        {
            _accepted = value;
            ClearPending();
            return value;
        }

        if (bypassSuppression)
        {
            _accepted = value;
            ClearPending();
            return value;
        }

        var delta = value - accepted;
        var rising = delta > 0f;
        var deadband = rising ? _settings.DeadbandUp : _settings.DeadbandDown;

        if (MathF.Abs(delta) <= deadband)
        {
            // Inside the deadband. Any change that was building up is no longer a trend.
            ClearPending();
            return accepted;
        }

        var responseTime = rising ? _settings.ResponseUp : _settings.ResponseDown;

        if (responseTime <= TimeSpan.Zero)
        {
            _accepted = value;
            ClearPending();
            return value;
        }

        // A reversal restarts the clock: time spent trending up says nothing about a fall.
        if (_pending is { } pending && (pending > accepted) != rising)
        {
            _pendingFor = TimeSpan.Zero;
        }

        _pending = value;
        _pendingFor += elapsed;

        if (_pendingFor >= responseTime)
        {
            _accepted = value;
            ClearPending();
            return value;
        }

        return accepted;
    }

    /// <summary>
    /// Discards all history, so the next reading is treated as a first reading.
    /// Called on resume from sleep and on configuration reload.
    /// </summary>
    public void Reset()
    {
        _accepted = null;
        ClearPending();
    }

    private void ClearPending()
    {
        _pending = null;
        _pendingFor = TimeSpan.Zero;
    }
}
