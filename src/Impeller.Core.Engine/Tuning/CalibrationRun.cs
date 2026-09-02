using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Core.Engine.Tuning;

/// <summary>Where a calibration has got to.</summary>
public enum CalibrationPhase
{
    /// <summary>Holding full speed, waiting for the fan to reach it.</summary>
    Settling = 0,

    /// <summary>Walking the duty down, recording the speed at each step.</summary>
    SteppingDown,

    /// <summary>The fan has stopped; easing back up to find what restarts it.</summary>
    FindingStart,

    /// <summary>Done, with a table.</summary>
    Finished,

    /// <summary>Abandoned. <see cref="CalibrationResult.Failure"/> says why.</summary>
    Failed,
}

/// <summary>Knobs on the calibration procedure. The defaults are the ones that get used.</summary>
public sealed record CalibrationSettings
{
    /// <summary>How far the duty drops per step while measuring.</summary>
    public float StepDown { get; init; } = 10f;

    /// <summary>How far it rises per step while hunting for the start threshold.</summary>
    public float StepUp { get; init; } = 3f;

    /// <summary>
    /// How far above the measured restart duty the recorded start duty sits.
    /// </summary>
    /// <remarks>
    /// A fan that restarted at 24% today wants a little more on a cold morning, with dust in the
    /// bearing, a year from now. The margin is the difference between a start duty that works and
    /// one that worked once.
    /// </remarks>
    public float SafetyMargin { get; init; } = 6f;

    /// <summary>How many consecutive flat samples count as settled.</summary>
    public int SettleSamples { get; init; } = 3;

    /// <summary>How many samples the trend fit spans.</summary>
    public int TrendWindow { get; init; } = 6;

    /// <summary>How much the fitted speed may move and still be called flat, in RPM.</summary>
    public float RpmTolerance { get; init; } = 32f;

    /// <summary>How long to wait for the fan to reach full speed, in samples.</summary>
    public int SpinUpTimeout { get; init; } = 80;

    /// <summary>How long to wait for one downward step to settle, in samples.</summary>
    public int StepTimeout { get; init; } = 20;

    /// <summary>How long to wait for a stopped fan to restart after a nudge, in samples.</summary>
    public int StartTimeout { get; init; } = 4;
}

/// <summary>What a calibration produced.</summary>
/// <param name="Points">The measured duty-to-speed table, fastest first.</param>
/// <param name="StartDuty">The duty that reliably breaks the fan away from rest.</param>
/// <param name="StopDuty">The duty below which it stalls instead of running slowly.</param>
/// <param name="Failure">Why the run was abandoned, or null when it was not.</param>
public sealed record CalibrationResult(
    EquatableArray<CalibrationPointDefinition> Points,
    Duty StartDuty,
    Duty StopDuty,
    string? Failure = null)
{
    /// <summary>Whether there is a table worth keeping.</summary>
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Measures one fan: what speed each duty produces, the duty below which it stalls, and the duty
/// that starts it again.
/// </summary>
/// <remarks>
/// <para>
/// A state machine advanced one sample at a time rather than an async procedure full of delays.
/// The procedure is entirely about counting — how many flat samples before a step counts as
/// settled, how many before it is abandoned — and written as a sequence of awaits none of that is
/// testable without either real fans or an elaborate clock. Written this way, a test drives a
/// simulated fan through a full run in microseconds and asserts on the table that comes out.
/// </para>
/// <para>
/// The caller writes <see cref="Command"/> to the hardware, waits its sample interval, reads the
/// paired tacho and calls <see cref="Advance"/>. Nothing here knows what a sample interval is.
/// </para>
/// </remarks>
public sealed class CalibrationRun
{
    private readonly CalibrationSettings _settings;
    private readonly List<CalibrationPointDefinition> _points = [];

    private StabilityDetector _stability;
    private int _waited;
    private float _command = Duty.MaxPercent;
    private float _stopDuty;

    /// <param name="settings">Overrides for the procedure, or null for the defaults.</param>
    public CalibrationRun(CalibrationSettings? settings = null)
    {
        _settings = settings ?? new CalibrationSettings();
        _stability = NewDetector();
    }

    /// <summary>Where the run has got to.</summary>
    public CalibrationPhase Phase { get; private set; } = CalibrationPhase.Settling;

    /// <summary>The duty the caller should be holding the control at right now.</summary>
    public Duty Command => new(_command);

    /// <summary>The result, once <see cref="IsComplete"/> is set. Null before that.</summary>
    public CalibrationResult? Result { get; private set; }

    /// <summary>Whether the run has finished, one way or the other.</summary>
    public bool IsComplete => Phase is CalibrationPhase.Finished or CalibrationPhase.Failed;

    /// <summary>
    /// Feeds one reading of the fan's speed, taken while the control was held at
    /// <see cref="Command"/>.
    /// </summary>
    /// <param name="rpm">The tacho reading, or null when the sensor did not answer.</param>
    public void Advance(float? rpm)
    {
        if (IsComplete)
        {
            return;
        }

        _waited++;

        switch (Phase)
        {
            case CalibrationPhase.Settling:
                SpinUp(rpm);
                break;

            case CalibrationPhase.SteppingDown:
                StepDown(rpm);
                break;

            case CalibrationPhase.FindingStart:
                FindStart(rpm);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Waits for full speed, and records it as the top of the table.
    /// </summary>
    /// <remarks>
    /// The fastest of the settled readings is taken rather than their average, because this one
    /// point sets the scale everything below is compared against and a fan that had not quite
    /// finished spinning up would otherwise pull it down.
    /// </remarks>
    private void SpinUp(float? rpm)
    {
        var settled = _stability.IsStable(rpm) ? _stability.Settled.Max() : (float?)null;

        if (settled is null && _waited < _settings.SpinUpTimeout)
        {
            return;
        }

        // Out of patience: whatever it is reading now will do. Nothing at all, or a fan that is not
        // turning at full duty, means there is nothing here to calibrate.
        if ((settled ?? rpm) is not { } reading || reading <= 0f)
        {
            Fail("The fan reported no speed at full duty. Check that it is paired with the right tacho sensor.");
            return;
        }

        Record(reading);
        BeginStepDown();
    }

    /// <summary>
    /// Walks the duty down, recording where the fan settles at each step, until it stops.
    /// </summary>
    /// <remarks>
    /// A step ends on any of three conditions: the speed settled, the fan stopped, or the step ran
    /// out of patience. The third is not a failure — some fans never hold quite still — and the
    /// reading taken at that moment is better than no row at all.
    /// </remarks>
    private void StepDown(float? rpm)
    {
        if (rpm is { } stalled && stalled <= 0f)
        {
            Record(0f);
            BeginFindStart();
            return;
        }

        if (_stability.IsStable(rpm))
        {
            Record(_stability.Average!.Value);
        }
        else if (_waited >= _settings.StepTimeout)
        {
            // Carry the last speed forward when the sensor said nothing. A step whose speed could
            // not be measured is at worst no faster than the one above it, which is what the
            // monotonic clamp in Record already assumes; inventing a zero would read as a stall.
            Record(rpm ?? _points[^1].Rpm);
        }
        else
        {
            return;
        }

        if (_command <= Duty.MinPercent)
        {
            // Down to nothing and still turning. Some pumps and most graphics-card fans behave this
            // way, and they have no stall point to record.
            Finish(Duty.Off, Duty.Off);
            return;
        }

        NextStepDown();
    }

    /// <summary>
    /// Eases back up from a stopped fan until it turns again.
    /// </summary>
    /// <remarks>
    /// Each nudge gets only a few samples: a fan that is going to start does so within a second or
    /// two, and waiting longer at each step makes the procedure slower without making the answer
    /// better.
    /// </remarks>
    private void FindStart(float? rpm)
    {
        if (rpm is { } value && value > 0f)
        {
            Finish(new Duty(Clamp(_command + _settings.SafetyMargin)), new Duty(_stopDuty));
            return;
        }

        if (_waited < _settings.StartTimeout)
        {
            return;
        }

        if (_command >= Duty.MaxPercent)
        {
            // Full duty and still not turning, having been turning a minute ago. A threshold taken
            // from this would record a fault as a property of the fan.
            Finish(Duty.Off, Duty.Off);
            return;
        }

        _command = Clamp(_command + _settings.StepUp);
        _waited = 0;
    }

    private void BeginStepDown()
    {
        Phase = CalibrationPhase.SteppingDown;
        NextStepDown();
    }

    /// <summary>
    /// Drops to the next duty on the ladder.
    /// </summary>
    /// <remarks>
    /// Ten at a time down to 10%, then 1%, then 0. The two extra rungs at the bottom are where a
    /// fan's stall point actually lives, and stepping straight from 10 to 0 would place the
    /// threshold somewhere inside a band ten points wide.
    /// </remarks>
    private void NextStepDown()
    {
        _command = _command switch
        {
            <= 1f => 0f,
            <= 10f => 1f,
            _ => Clamp(_command - _settings.StepDown),
        };

        _stability = NewDetector();
        _waited = 0;
    }

    /// <summary>
    /// Records where the fan stalled and starts hunting for what restarts it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stop duty is the lowest duty the fan was still measured turning at, not the one that
    /// stalled it. The true stall point sits somewhere in the ten-point gap between the two, and
    /// only the upper end of that gap was actually observed to work. The app this replaces takes
    /// the stalling duty plus a fixed six, which lands inside the unmeasured gap: on a fan that
    /// stops below 18% but was stepped 20, 10, that yields a stop duty of 16, and every command
    /// between 16 and 18 then reaches a fan that cannot turn there — the exact stall this value
    /// exists to prevent.
    /// </para>
    /// <para>
    /// Being conservative costs nothing here. The engine commands zero at or below the stop duty,
    /// so erring high only means a fan is off across a band where it would have been stalled or
    /// nearly so anyway.
    /// </para>
    /// </remarks>
    private void BeginFindStart()
    {
        Phase = CalibrationPhase.FindingStart;

        _stopDuty = _points
            .Where(point => point.Rpm > 0)
            .Select(point => point.Duty.Percent)
            .DefaultIfEmpty(0f)
            .Min();

        _waited = 0;
    }

    /// <summary>
    /// Adds a row, never faster than the row above it.
    /// </summary>
    /// <remarks>
    /// The clamp is what keeps the table invertible. Duty to speed is monotonic in the physical
    /// world but not always in the measurements — a settling fan or a coarse tacho can read faster
    /// at a lower duty — and a table with a bump in it turns a speed request into whichever of two
    /// duties the search happened to reach first.
    /// </remarks>
    private void Record(float rpm)
    {
        var value = (int)MathF.Round(Math.Max(rpm, 0f));

        if (_points.Count > 0)
        {
            value = Math.Min(value, _points[^1].Rpm);
        }

        _points.Add(new CalibrationPointDefinition(new Duty(_command), value));
    }

    private void Finish(Duty start, Duty stop)
    {
        Phase = CalibrationPhase.Finished;
        Result = new CalibrationResult([.. _points], start, stop);

        // Back to something safe. The run is over and the caller is about to release the control;
        // a fan left at whatever the last measuring step happened to be is a fan left slow.
        _command = Duty.MaxPercent;
    }

    private void Fail(string reason)
    {
        Phase = CalibrationPhase.Failed;
        Result = new CalibrationResult([], Duty.Off, Duty.Off, reason);
        _command = Duty.MaxPercent;
    }

    private StabilityDetector NewDetector() =>
        new(_settings.SettleSamples, _settings.TrendWindow, _settings.RpmTolerance);

    private static float Clamp(float percent) =>
        Math.Clamp(percent, Duty.MinPercent, Duty.MaxPercent);
}
