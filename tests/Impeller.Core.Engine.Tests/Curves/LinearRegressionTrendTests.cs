using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests.Curves;

public class LinearRegressionTrendTests
{
    private static Trend Feed(LinearRegressionTrend trend, params float[] values)
    {
        var last = Trend.Unknown;

        foreach (var value in values)
        {
            last = trend.Update(value);
        }

        return last;
    }

    [Fact]
    public void A_window_that_has_not_filled_reports_unknown()
    {
        var trend = new LinearRegressionTrend(windowSize: 4, tolerance: 1f);

        // Four samples fill the window; the trend of a half-written buffer is the trend of its
        // unwritten zeroes, which is not information.
        Assert.Equal(Trend.Unknown, Feed(trend, 10f, 20f, 30f, 40f));
        Assert.False(trend.IsPrimed is false);
    }

    [Fact]
    public void A_real_trend_is_reported_once_the_window_fills()
    {
        var trend = new LinearRegressionTrend(windowSize: 4, tolerance: 1f);

        Assert.Equal(Trend.Up, Feed(trend, 10f, 11f, 12f, 13f, 14f));
    }

    [Fact]
    public void A_falling_signal_trends_down()
    {
        var trend = new LinearRegressionTrend(windowSize: 4, tolerance: 1f);

        Assert.Equal(Trend.Down, Feed(trend, 40f, 39f, 38f, 37f, 36f));
    }

    [Fact]
    public void A_flat_signal_trends_neutral()
    {
        var trend = new LinearRegressionTrend(windowSize: 4, tolerance: 1f);

        Assert.Equal(Trend.Neutral, Feed(trend, 50f, 50f, 50f, 50f, 50f));
    }

    [Fact]
    public void Noise_around_a_flat_mean_does_not_register_as_a_trend()
    {
        var trend = new LinearRegressionTrend(windowSize: 6, tolerance: 1f);

        Assert.Equal(Trend.Neutral, Feed(trend, 50f, 50.4f, 49.6f, 50.3f, 49.7f, 50f, 50.2f));
    }

    [Fact]
    public void A_drift_too_slow_to_clear_the_tolerance_is_caught_by_the_creep_accumulator()
    {
        // 0.15 per sample over a ten-sample window rises 0.75 across half a window, under the
        // tolerance of 1. Judged tick by tick this is flat forever — which is exactly the machine
        // that heats up slowly and never gets more airflow. The accumulator has to catch it.
        var trend = new LinearRegressionTrend(windowSize: 10, tolerance: 1f);
        var reported = new List<Trend>();

        for (var i = 0; i < 20; i++)
        {
            reported.Add(trend.Update(i * 0.15f));
        }

        var afterPriming = reported.Skip(10).ToList();

        Assert.Contains(Trend.Neutral, afterPriming);
        Assert.Contains(Trend.Up, afterPriming);

        // And it must not fire constantly: the accumulator resets each time it reports.
        Assert.True(afterPriming.Count(t => t == Trend.Up) < afterPriming.Count);
    }

    [Fact]
    public void The_creep_accumulator_resets_after_reporting()
    {
        var trend = new LinearRegressionTrend(windowSize: 10, tolerance: 1f);

        for (var i = 0; i < 10; i++)
        {
            trend.Update(i * 0.15f);
        }

        var firstReport = -1;
        var secondReport = -1;

        for (var i = 10; i < 40; i++)
        {
            if (trend.Update(i * 0.15f) != Trend.Up)
            {
                continue;
            }

            if (firstReport < 0)
            {
                firstReport = i;
            }
            else
            {
                secondReport = i;
                break;
            }
        }

        Assert.True(firstReport > 0, "the drift was never reported");
        Assert.True(secondReport > firstReport, "the drift was reported only once");

        // Reporting every tick would mean the accumulator never cleared.
        Assert.True(secondReport - firstReport > 1);
    }

    [Fact]
    public void The_slope_sign_follows_the_direction_the_samples_were_written()
    {
        var rising = new LinearRegressionTrend(windowSize: 4, tolerance: 1f);
        Feed(rising, 10f, 11f, 12f, 13f, 14f);
        Assert.True(rising.Slope > 0);

        var falling = new LinearRegressionTrend(windowSize: 4, tolerance: 1f);
        Feed(falling, 14f, 13f, 12f, 11f, 10f);
        Assert.True(falling.Slope < 0);
    }

    [Fact]
    public void Reset_makes_the_detector_prime_again()
    {
        var trend = new LinearRegressionTrend(windowSize: 4, tolerance: 1f);
        Feed(trend, 10f, 11f, 12f, 13f, 14f);

        trend.Reset();

        Assert.False(trend.IsPrimed);
        Assert.Equal(Trend.Unknown, trend.Update(15f));
    }

    [Fact]
    public void A_window_smaller_than_two_samples_is_rejected()
    {
        // One sample has no slope, and the regression denominator would be zero.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LinearRegressionTrend(windowSize: 1, tolerance: 1f));
    }
}
