using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tuning;

/// <summary>
/// Decides when a noisy reading has settled.
/// </summary>
/// <remarks>
/// <para>
/// Every tuning procedure is the same shape: change something, wait for the machine to finish
/// reacting, write down what it settled at. "Finished reacting" is the hard part — a fan's tacho
/// wanders by tens of RPM while sitting at a constant duty, so comparing consecutive readings
/// never converges and comparing against a fixed band converges immediately on a slow ramp.
/// </para>
/// <para>
/// What works is asking for a neutral <em>trend</em> over a window, several times running, which is
/// what this does. The readings collected while neutral are kept, so the caller records the average
/// of a settled fan rather than whichever single sample happened to trip the condition.
/// </para>
/// </remarks>
public sealed class StabilityDetector
{
    private readonly int _samplesRequired;
    private readonly LinearRegressionTrend _trend;
    private readonly List<float> _settled = [];

    private int _neutral;

    /// <param name="samplesRequired">How many consecutive neutral samples count as settled.</param>
    /// <param name="windowSize">How many samples the trend fit spans.</param>
    /// <param name="tolerance">
    /// How far the fitted line may move across half the window and still be called flat, in the
    /// reading's own units. For a fan tacho this is tens of RPM, not ones.
    /// </param>
    public StabilityDetector(int samplesRequired, int windowSize, float tolerance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samplesRequired, 1);

        _samplesRequired = samplesRequired;
        _trend = new LinearRegressionTrend(windowSize, tolerance);
    }

    /// <summary>The readings taken while the signal was flat, in order.</summary>
    public IReadOnlyList<float> Settled => _settled;

    /// <summary>The average of those readings, or null before anything has settled.</summary>
    public float? Average => _settled.Count == 0 ? null : _settled.Average();

    /// <summary>
    /// Feeds one sample and reports whether the signal has now been flat for long enough.
    /// </summary>
    /// <remarks>
    /// A missing reading resets rather than being skipped. A sensor that stopped answering is not
    /// evidence of stability, and treating a gap as "no change" is how a procedure decides a fan
    /// settled at a speed nobody measured.
    /// </remarks>
    public bool IsStable(float? value)
    {
        if (value is not { } reading || _trend.Update(reading) != Trend.Neutral)
        {
            _settled.Clear();
            _neutral = 0;
            return false;
        }

        _settled.Add(reading);
        _neutral++;

        return _neutral >= _samplesRequired;
    }

    /// <summary>Forgets everything, so the next step starts from a standing start.</summary>
    public void Reset()
    {
        _trend.Reset();
        _settled.Clear();
        _neutral = 0;
    }
}
