namespace Impeller.Core.Engine.Curves;

/// <summary>Which way a sensor has been moving over a trend window.</summary>
public enum Trend
{
    /// <summary>Not enough samples yet to say. Reported until the window has filled once.</summary>
    Unknown = -999,

    /// <summary>Falling.</summary>
    Down = -1,

    /// <summary>Flat, within tolerance.</summary>
    Neutral = 0,

    /// <summary>Rising.</summary>
    Up = 1,
}

/// <summary>
/// Classifies the direction of a signal by fitting a least-squares line to a sliding window of
/// samples.
/// </summary>
/// <remarks>
/// <para>
/// A curve that reacts to "is it hotter than last tick" reacts to noise. Fitting a line over a
/// window asks the better question — is this a trend or a wobble — and gives a slope that can be
/// compared against a tolerance rather than a bare threshold.
/// </para>
/// <para>
/// The subtle part is the creep accumulator. A slope too shallow to clear the tolerance on its own
/// is added to a running total, and once that total clears the tolerance a trend is reported and
/// the total resets. Without it, a machine warming by a fraction of a degree per tick trends
/// <see cref="Trend.Neutral"/> forever and the fan never responds to a genuine, slow climb.
/// </para>
/// <para>Not thread-safe; owned by the single curve that uses it, on the tick loop's thread.</para>
/// </remarks>
public sealed class LinearRegressionTrend
{
    private readonly int _windowSize;
    private readonly double _halfWindow;
    private readonly float _tolerance;
    private readonly float[] _samples;

    // Precomputed sums over x = 0..n-1. The x values never change, only the y values, so the
    // denominator of the slope is a constant for the life of the detector.
    private readonly double _sumX;
    private readonly double _sumXSquared;
    private readonly double _denominator;

    private int _index = -1;
    private bool _filled;
    private float _creep;

    /// <param name="windowSize">How many samples the fit spans. Must be at least two.</param>
    /// <param name="tolerance">
    /// How far the fitted line must rise or fall across half the window before the movement counts
    /// as a trend, in the sensor's own units.
    /// </param>
    public LinearRegressionTrend(int windowSize, float tolerance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 2);

        _windowSize = windowSize;
        _halfWindow = windowSize * 0.5;
        _tolerance = tolerance;
        _samples = new float[windowSize];

        _sumX = windowSize * (windowSize - 1) / 2.0;
        _sumXSquared = (windowSize - 1) * windowSize * ((2.0 * windowSize) - 1) / 6.0;
        _denominator = (windowSize * _sumXSquared) - (_sumX * _sumX);
    }

    /// <summary>Whether the window has filled, after which a real trend is reported.</summary>
    public bool IsPrimed => _filled;

    /// <summary>The slope of the most recent fit, in units per sample.</summary>
    public double Slope { get; private set; }

    /// <summary>
    /// Adds a sample and reports the direction of the window it now ends.
    /// </summary>
    public Trend Update(float value)
    {
        if (_index < 0)
        {
            _index = 0;
            _samples[0] = value;
            return Trend.Unknown;
        }

        _index = (_index + 1) % _windowSize;
        _samples[_index] = value;

        Slope = CalculateSlope();

        if (!_filled)
        {
            // Still priming: the buffer's unwritten tail is zeroes, so any slope from it is noise.
            _filled = _index == _windowSize - 1;
            return Trend.Unknown;
        }

        // Rise or fall across half the window, which is what the tolerance is expressed in.
        if (Math.Abs(Slope * _halfWindow) > _tolerance)
        {
            return Slope < 0 ? Trend.Down : Trend.Up;
        }

        _creep += (float)Slope;

        if (Math.Abs(_creep) > _tolerance)
        {
            var direction = _creep < 0 ? Trend.Down : Trend.Up;
            _creep = 0f;
            return direction;
        }

        return Trend.Neutral;
    }

    /// <summary>
    /// Discards every sample, so the detector re-primes before reporting a trend again. Called on
    /// resume from sleep and on configuration reload, where the samples either side of the gap
    /// describe two different situations.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_samples);
        _index = -1;
        _filled = false;
        _creep = 0f;
        Slope = 0;
    }

    /// <summary>
    /// Least-squares slope over the window, walking oldest-to-newest so the sample written last
    /// sits at the highest x and the sign of the slope means what it looks like it means.
    /// </summary>
    private double CalculateSlope()
    {
        double sumY = 0;
        double sumXy = 0;
        var cursor = _index;

        for (var x = 0; x < _windowSize; x++)
        {
            cursor = (cursor + 1) % _windowSize;
            var y = _samples[cursor];
            sumY += y;
            sumXy += x * y;
        }

        return ((_windowSize * sumXy) - (_sumX * sumY)) / _denominator;
    }
}
