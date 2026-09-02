using Impeller.Core.Engine.Tuning;

namespace Impeller.Core.Engine.Tests.Tuning;

/// <summary>
/// Covers the thing every tuning step waits on: deciding a reading has settled.
/// </summary>
/// <remarks>
/// Getting this wrong in either direction ruins a calibration silently. Too eager and every row
/// records a fan still spinning down; too reluctant and every step times out and records whatever
/// happened to be on the sensor at the moment patience ran out.
/// </remarks>
public class StabilityDetectorTests
{
    [Fact]
    public void A_flat_reading_settles_once_the_window_has_filled()
    {
        var detector = new StabilityDetector(samplesRequired: 3, windowSize: 6, tolerance: 32f);
        var samples = 0;

        while (!detector.IsStable(1200f))
        {
            Assert.True(++samples < 50, "A perfectly flat signal never settled.");
        }

        // The trend fit has to see a full window before it reports anything, and only then do the
        // consecutive flat samples start counting.
        Assert.True(samples >= 6);
    }

    [Fact]
    public void A_reading_that_is_still_moving_does_not_settle()
    {
        var detector = new StabilityDetector(3, 6, 32f);
        var value = 1800f;

        for (var i = 0; i < 40; i++)
        {
            value -= 100f;
            Assert.False(detector.IsStable(value));
        }
    }

    [Fact]
    public void Noise_inside_the_tolerance_still_counts_as_settled()
    {
        // A tacho on a fan held at one duty wanders by tens of RPM. A detector that called that
        // movement would never let a calibration step finish.
        var detector = new StabilityDetector(3, 6, 32f);
        var wobble = new[] { 1200f, 1207f, 1194f, 1203f, 1198f, 1205f, 1196f, 1201f, 1199f, 1204f };
        var settled = false;

        for (var i = 0; i < 4 && !settled; i++)
        {
            foreach (var value in wobble)
            {
                settled |= detector.IsStable(value);
            }
        }

        Assert.True(settled);
    }

    [Fact]
    public void A_sensor_that_stops_answering_resets_rather_than_being_skipped()
    {
        // A gap is not evidence of stability. Treating it as "no change" is how a step decides a fan
        // settled at a speed nobody measured.
        var detector = new StabilityDetector(3, 6, 32f);

        for (var i = 0; i < 20; i++)
        {
            detector.IsStable(1200f);
        }

        Assert.False(detector.IsStable(null));
        Assert.Empty(detector.Settled);
        Assert.False(detector.IsStable(1200f));
    }

    [Fact]
    public void The_settled_readings_are_kept_so_a_step_records_an_average()
    {
        var detector = new StabilityDetector(3, 6, 32f);

        while (!detector.IsStable(1000f))
        {
            // Prime.
        }

        Assert.NotEmpty(detector.Settled);
        Assert.Equal(1000f, detector.Average!.Value, 3);
    }

    [Fact]
    public void Resetting_makes_it_prime_again()
    {
        var detector = new StabilityDetector(3, 6, 32f);

        while (!detector.IsStable(1000f))
        {
            // Prime.
        }

        detector.Reset();

        Assert.False(detector.IsStable(1000f));
        Assert.Null(detector.Average);
    }
}
