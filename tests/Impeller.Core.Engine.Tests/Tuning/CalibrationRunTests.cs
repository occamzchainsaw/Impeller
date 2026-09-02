using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Tuning;

namespace Impeller.Core.Engine.Tests.Tuning;

/// <summary>
/// Covers measuring one fan end to end, against a model that lags and stalls the way a real one
/// does.
/// </summary>
/// <remarks>
/// The whole reason the procedure is a state machine rather than a sequence of awaits is that these
/// run in microseconds. A calibration takes several minutes on real hardware, so a version of this
/// that waited would be a version nobody ran.
/// </remarks>
public class CalibrationRunTests
{
    /// <summary>Drives a run to completion, one sample at a time, and hands back what it found.</summary>
    private static CalibrationResult Calibrate(SimulatedFan fan, CalibrationSettings? settings = null)
    {
        var run = new CalibrationRun(settings);

        for (var sample = 0; sample < 2000 && !run.IsComplete; sample++)
        {
            fan.Apply(run.Command);
            run.Advance(fan.Rpm);
        }

        Assert.True(run.IsComplete, $"The run was still in {run.Phase} after 2000 samples.");
        return run.Result!;
    }

    [Fact]
    public void A_fan_that_behaves_normally_produces_a_table_and_both_thresholds()
    {
        var result = Calibrate(new SimulatedFan(maximumRpm: 1800, stallsBelow: 18f, startsAbove: 24f));

        Assert.True(result.Succeeded);
        Assert.Null(result.Failure);

        // Full speed down to the stall, at ten points a step, so ten rows before the ladder's tail.
        Assert.Equal(Duty.Full, result.Points[0].Duty);
        Assert.True(result.Points.Count >= 9, $"Only {result.Points.Count} points were measured.");
        Assert.True(result.Points[0].Rpm > 1500, $"Full speed measured {result.Points[0].Rpm} RPM.");
    }

    [Fact]
    public void The_table_never_reads_faster_at_a_lower_duty()
    {
        // Not cosmetic: the table is inverted to answer "what duty gives me this speed", and a bump
        // in it makes that question have two answers.
        var result = Calibrate(new SimulatedFan());

        for (var i = 1; i < result.Points.Count; i++)
        {
            Assert.True(result.Points[i].Duty < result.Points[i - 1].Duty);
            Assert.True(result.Points[i].Rpm <= result.Points[i - 1].Rpm);
        }
    }

    [Fact]
    public void The_stop_duty_is_a_duty_the_fan_was_actually_seen_turning_at()
    {
        // The measurement brackets the true stall point between the lowest duty that turned and the
        // one that did not. Only the first of those was observed, and picking anything below it is
        // picking a number out of the gap the procedure never tested.
        var fan = new SimulatedFan(stallsBelow: 18f, startsAbove: 24f);
        var result = Calibrate(fan);

        var lowestTurning = result.Points
            .Where(point => point.Rpm > 0)
            .Min(point => point.Duty.Percent);

        Assert.Equal(lowestTurning, result.StopDuty.Percent);
        Assert.True(result.StopDuty.Percent >= 18f, $"Stop duty {result.StopDuty} would stall this fan.");
    }

    [Fact]
    public void The_start_duty_clears_the_duty_that_actually_restarted_it()
    {
        var result = Calibrate(new SimulatedFan(stallsBelow: 18f, startsAbove: 24f));

        // Found by nudging up in threes from a standstill, then margined. Landing exactly on the
        // restart duty would be a fan that starts on a good day.
        Assert.True(result.StartDuty.Percent > 24f, $"Start duty {result.StartDuty} has no margin.");
        Assert.True(result.StartDuty.Percent <= 24f + 3f + 6f);
        Assert.True(result.StartDuty > result.StopDuty);
    }

    [Fact]
    public void A_fan_that_never_stops_gets_a_table_and_no_thresholds()
    {
        // Pumps and most graphics-card fans keep turning at zero duty. Inventing a stall point for
        // one would make the engine cut it off at a speed it was perfectly happy at.
        var result = Calibrate(new SimulatedFan(stallsBelow: -1f, startsAbove: 0f));

        Assert.True(result.Succeeded);
        Assert.Equal(Duty.Off, result.StartDuty);
        Assert.Equal(Duty.Off, result.StopDuty);
        Assert.Equal(Duty.Off, result.Points[^1].Duty);
        Assert.True(result.Points[^1].Rpm > 0);
    }

    [Fact]
    public void A_control_with_no_fan_on_it_fails_rather_than_recording_zeroes()
    {
        // The common cause is a control paired with the wrong tacho, and a table full of zeroes
        // would be applied as though it meant something.
        var run = new CalibrationRun(new CalibrationSettings { SpinUpTimeout = 10 });

        for (var sample = 0; sample < 20 && !run.IsComplete; sample++)
        {
            run.Advance(0f);
        }

        Assert.Equal(CalibrationPhase.Failed, run.Phase);
        Assert.False(run.Result!.Succeeded);
        Assert.Empty(run.Result.Points);
        Assert.Contains("no speed", run.Result.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_tacho_that_stops_answering_fails_rather_than_hanging()
    {
        var run = new CalibrationRun(new CalibrationSettings { SpinUpTimeout = 10 });

        for (var sample = 0; sample < 20 && !run.IsComplete; sample++)
        {
            run.Advance(null);
        }

        Assert.Equal(CalibrationPhase.Failed, run.Phase);
    }

    [Fact]
    public void The_control_is_left_at_full_speed_when_the_run_ends()
    {
        // Whatever happened, the run is over and the control is about to be handed back. Leaving it
        // at the last measuring step means leaving a fan parked at 10% until something else moves it.
        var run = new CalibrationRun(new CalibrationSettings { SpinUpTimeout = 5 });
        var fan = new SimulatedFan();

        while (!run.IsComplete)
        {
            fan.Apply(run.Command);
            run.Advance(fan.Rpm);
        }

        Assert.Equal(Duty.Full, run.Command);
    }

    [Fact]
    public void Samples_after_the_run_has_finished_change_nothing()
    {
        var fan = new SimulatedFan();
        var run = new CalibrationRun();

        while (!run.IsComplete)
        {
            fan.Apply(run.Command);
            run.Advance(fan.Rpm);
        }

        var settled = run.Result;
        run.Advance(0f);
        run.Advance(1200f);

        Assert.Same(settled, run.Result);
    }
}
