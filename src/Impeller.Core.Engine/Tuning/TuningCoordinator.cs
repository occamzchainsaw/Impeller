using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine.Configuration;

namespace Impeller.Core.Engine.Tuning;

/// <summary>
/// Runs the live half of the tuning procedures: takes the controls, drives the hardware, and writes
/// what it learns back into the configuration.
/// </summary>
/// <remarks>
/// <para>
/// The procedures themselves are in <see cref="CalibrationRun"/> and <see cref="FanPairingRun"/> and
/// know nothing about hardware, clocks or ownership. This is the part that cannot be tested without
/// a machine, so it is deliberately the thin part: claim, write, wait, read, hand the reading to the
/// state machine, repeat.
/// </para>
/// <para>
/// Controls are written directly rather than through the tick loop, and that is the point. Every
/// protection the loop applies — the ramp limiter, the start kick, the avoided bands — exists to
/// make a fan behave well, and every one of them would corrupt a measurement of how the fan behaves.
/// Ownership is still taken, so the loop leaves the control alone while the run has it and nothing
/// else can claim it.
/// </para>
/// </remarks>
public sealed class TuningCoordinator(
    ISensorRegistry registry,
    ControlOwnershipRegistry ownership,
    ConfigurationCoordinator configuration,
    TimeProvider timeProvider)
{
    /// <summary>Who the engine records as holding a control during a tuning run.</summary>
    /// <remarks>
    /// Distinct from the shell's manual claimant so the two cannot release each other's controls,
    /// and so the dashboard can say a fan is being measured rather than that someone pinned it.
    /// </remarks>
    public const string Claimant = "tuning";

    private int _running;

    /// <summary>Whether this coordinator is the thing currently holding grants suspended.</summary>
    private bool _suspendedGrants;

    /// <summary>How long each sample takes. One second, matching the tick rate.</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Raised on every sample, so a dialog can say what is happening.</summary>
    public event EventHandler<TuningProgress>? Progressed;

    /// <summary>Whether a run is in flight. Only one at a time, machine-wide.</summary>
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>
    /// Measures each of these controls in turn.
    /// </summary>
    /// <remarks>
    /// One at a time rather than in parallel. Fans share airflow and a case full of them changing
    /// speed at once measures the case, not the fan.
    /// </remarks>
    public async Task<TuningReport> CalibrateAsync(
        IEnumerable<SensorId> controlIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlIds);

        var targets = controlIds.ToList();
        var outcomes = new List<TuningOutcome>();
        var tables = new Dictionary<SensorId, CalibrationResult>();
        var cancelled = false;

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return Busy(TuningKind.Calibration);
        }

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (outcome, result) = await CalibrateOneAsync(targets[i], i, targets.Count, cancellationToken)
                    .ConfigureAwait(false);

                outcomes.Add(outcome);

                if (result is { Succeeded: true })
                {
                    tables[targets[i]] = result;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Whatever finished before the cancellation is still worth keeping: a run over eight
            // fans that is stopped after six should not throw six measurements away.
            cancelled = true;
        }
        finally
        {
            ReleaseAll(targets);
            Interlocked.Exchange(ref _running, 0);
        }

        var saved = tables.Count > 0 && RecordCalibration(tables);

        var report = new TuningReport(TuningKind.Calibration, [.. outcomes], cancelled, saved);
        Report(TuningKind.Calibration, "Finished", Summarise(outcomes), targets.Count, targets.Count, true);
        return report;
    }

    /// <summary>
    /// Works out which tacho belongs to which of these controls.
    /// </summary>
    /// <remarks>
    /// Every control the engine knows about is held at the baseline, not just the ones being
    /// identified, because a fan left on its own curve changes speed for its own reasons and there
    /// is no way to tell that apart from the drop the run is looking for.
    /// </remarks>
    public async Task<TuningReport> PairAsync(
        IEnumerable<SensorId> controlIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlIds);

        var targets = controlIds.ToList();
        var held = registry.Controls.Select(control => control.Id).ToList();

        var tachometers = registry.Sensors
            .Where(sensor => sensor.Kind == SensorKind.FanSpeed)
            .Select(sensor => sensor.Id)
            .ToList();

        var run = new FanPairingRun(targets, tachometers);
        var cancelled = false;

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return Busy(TuningKind.Pairing);
        }

        try
        {
            var unavailable = Claim(held);

            if (unavailable is { } blocked)
            {
                return Refused(TuningKind.Pairing, blocked);
            }

            while (!run.IsComplete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var controlId in held)
                {
                    Write(controlId, run.CommandFor(controlId));
                }

                await Task.Delay(SampleInterval, timeProvider, cancellationToken).ConfigureAwait(false);

                var readings = tachometers.ToDictionary(id => id, registry.GetValue);
                run.Advance(readings);

                Report(
                    TuningKind.Pairing,
                    run.Phase == PairingPhase.Settling ? "Settling" : "Testing",
                    DescribeCurrent(run),
                    run.Pairs.Count,
                    targets.Count);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            ReleaseAll(held);
            Interlocked.Exchange(ref _running, 0);
        }

        var pairs = run.Pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
        var byIdentity = PairByIdentity(targets, pairs);

        var outcomes = targets.Select(controlId => new TuningOutcome(
            controlId,
            NameOf(controlId),
            pairs.ContainsKey(controlId),
            pairs.TryGetValue(controlId, out var tachometer)
                ? byIdentity.Contains(controlId)
                    ? $"Paired with {NameOf(tachometer)} — it is the only fan sensor on the same device, "
                        + "and moving the control did not change any speed."
                    : $"Paired with {NameOf(tachometer)}."
                : "Nothing changed speed with it. The header may be empty, or its fan may have no tacho."))
            .ToList();

        var saved = pairs.Count > 0 && RecordPairs(pairs);

        Report(TuningKind.Pairing, "Finished", Summarise(outcomes), targets.Count, targets.Count, true);
        return new TuningReport(TuningKind.Pairing, [.. outcomes], cancelled, saved);
    }

    private async Task<(TuningOutcome Outcome, CalibrationResult? Result)> CalibrateOneAsync(
        SensorId controlId,
        int index,
        int total,
        CancellationToken cancellationToken)
    {
        var name = NameOf(controlId);

        if (Binding(controlId) is not { } binding || binding.PairedFanSensorId.IsNone)
        {
            // Nothing to measure against. Pairing first is the fix, and saying so is more use than
            // reporting that the calibration failed.
            return (
                new TuningOutcome(controlId, name, false, "No tacho is paired with this control. Run pairing first."),
                null);
        }

        if (Claim([controlId]) is { } blocked)
        {
            return (new TuningOutcome(controlId, name, false, $"{blocked} is in use by something else."), null);
        }

        var run = new CalibrationRun();

        while (!run.IsComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Write(controlId, run.Command);
            await Task.Delay(SampleInterval, timeProvider, cancellationToken).ConfigureAwait(false);

            var rpm = registry.GetValue(binding.PairedFanSensorId);
            run.Advance(rpm);

            Report(
                TuningKind.Calibration,
                run.Phase.ToString(),
                $"{name} at {run.Command} — {rpm?.ToString("0") ?? "no reading"} RPM",
                index,
                total);
        }

        // Back to full speed and released. The run left the fan wherever the last step put it.
        Write(controlId, Duty.Full);

        var result = run.Result!;

        return (
            new TuningOutcome(
                controlId,
                name,
                result.Succeeded,
                result.Succeeded
                    ? $"{result.Points.Count} points measured. Starts at {result.StartDuty}, stalls below {result.StopDuty}."
                    : result.Failure!),
            result);
    }

    /// <summary>
    /// Takes every control the run needs, all or nothing.
    /// </summary>
    /// <returns>The name of the first control that could not be taken, or null when all were.</returns>
    /// <remarks>
    /// A partial claim is released again rather than left standing. Half a machine held by an
    /// abandoned tuning run is worse than a run that did not start.
    /// </remarks>
    private string? Claim(List<SensorId> controlIds)
    {
        for (var i = 0; i < controlIds.Count; i++)
        {
            var owner = ownership.GetOwner(controlIds[i]);

            // A plugin mid-claim, or a failsafe that has not cleared, both outrank a tuning run:
            // one is another program's decision and the other is a machine in trouble.
            if (owner.Kind is ControlOwnerKind.Plugin or ControlOwnerKind.Failsafe)
            {
                ReleaseAll(controlIds.Take(i).ToList());
                return NameOf(controlIds[i]);
            }

            // A pin the user set is displaced rather than refused. Re-applying the configuration
            // when the run ends puts it back, and a fan quietly pinned days ago is exactly the sort
            // of thing that would otherwise make one column of the measurements nonsense.
            if (owner.Kind == ControlOwnerKind.ManualOverride)
            {
                ownership.ForceRelease(controlIds[i]);
            }

            if (!ownership.TryAcquire(controlIds[i], ControlOwnerKind.ManualOverride, Claimant).Succeeded)
            {
                ReleaseAll(controlIds.Take(i).ToList());
                return NameOf(controlIds[i]);
            }
        }

        // Held for the whole run, not merely taken at the start of one. Refusing to begin while a
        // plugin holds a fan is not enough on its own: a plugin that connects, or reconnects, a
        // few seconds into a run claims its fan back and moves it — and a run works by moving one
        // fan at a time and attributing everything that changes to that fan. A pairing run in that
        // state does not fail. It produces a confident, wrong answer, which is how a GPU came to be
        // paired with the tacho of somebody else's case fan.
        ownership.SuspendGrants();
        _suspendedGrants = true;

        return null;
    }

    /// <summary>
    /// Hands every control back.
    /// </summary>
    /// <remarks>
    /// Re-applying the configuration afterwards is what restores a pin the run took over, since
    /// applying restores every stored manual duty. Nothing here needs to remember what it displaced.
    /// </remarks>
    private void ReleaseAll(List<SensorId> controlIds)
    {
        // Only if this coordinator suspended them. Grants are also suspended by a failsafe, and a
        // tuning run tidying up must not be what re-arms plugin claims on a machine in trouble.
        if (_suspendedGrants)
        {
            ownership.ResumeGrants();
            _suspendedGrants = false;
        }

        foreach (var controlId in controlIds)
        {
            ownership.Release(controlId, Claimant);
        }
    }

    /// <summary>
    /// Writes a duty straight to the hardware.
    /// </summary>
    /// <remarks>
    /// A control that throws is not fatal to the run: the state machine will see a speed that does
    /// not move and report that nothing reacted, which is a truer description than an exception
    /// halfway through a ladder.
    /// </remarks>
    private void Write(SensorId controlId, Duty duty)
    {
        try
        {
            registry.GetControl(controlId)?.Write(duty);
        }
        catch (Exception)
        {
            // Reported by the measurement, not by the write.
        }
    }

    /// <summary>Writes measured tables into the configuration and applies it.</summary>
    private bool RecordCalibration(Dictionary<SensorId, CalibrationResult> tables)
    {
        var current = configuration.Current;
        var controls = current.Controls.ToArray();

        for (var i = 0; i < controls.Length; i++)
        {
            if (!tables.TryGetValue(controls[i].ControlId, out var result))
            {
                continue;
            }

            controls[i] = controls[i] with
            {
                Calibration = result.Points,
                StartDuty = result.StartDuty,
                StopDuty = result.StopDuty,
                MinimumDuty = FloorFrom(result, controls[i].MinimumDuty),
            };
        }

        return Apply(current with { Controls = [.. controls] });
    }

    /// <summary>
    /// The lowest duty this fan was actually seen turning at, which becomes the floor the engine
    /// will not command below.
    /// </summary>
    /// <remarks>
    /// Measuring a stall point and then leaving the floor at zero is what lets a curve ask a fan
    /// for less than it can physically do. The fan then either sits stalled at a duty the engine
    /// believes is driving it, or is switched off by the start/stop gate and never asked for
    /// enough to restart - both of which read as "the fan is dead" rather than as a limit nobody
    /// wrote down.
    /// <para>
    /// Only ever raised, never lowered. Somebody who has deliberately set a floor above what the
    /// hardware requires - a fan that whines, or one that must never idle slowly - has said
    /// something a measurement does not overrule.
    /// </para>
    /// </remarks>
    private static Duty FloorFrom(CalibrationResult result, Duty existing)
    {
        var lowestTurning = float.MaxValue;

        foreach (var point in result.Points)
        {
            if (point.Rpm > 0f && point.Duty.Percent < lowestTurning)
            {
                lowestTurning = point.Duty.Percent;
            }
        }

        if (lowestTurning is float.MaxValue)
        {
            // Nothing in the table turned. That is a fan with no tacho or a header driving
            // nothing, and neither is evidence about a floor.
            return existing;
        }

        return lowestTurning > existing.Percent ? new Duty(lowestTurning) : existing;
    }

    /// <summary>
    /// Pairs whatever the moving test could not, using the fact that a control and its tacho are
    /// usually the same channel of the same device.
    /// </summary>
    /// <remarks>
    /// The moving test works by dropping one control and watching for a speed that falls with it.
    /// It cannot see a fan that is already as slow as it goes — a GPU at idle, or a header pinned
    /// at its floor — because there is no drop left to measure, and those fans came out unpaired
    /// with no RPM readout and no way to calibrate them, which needs a pairing first.
    /// <para>
    /// Only ever consulted for controls the measurement did not settle, and only where the answer
    /// is unambiguous: the same channel of the same device, or a device carrying exactly one fan
    /// sensor. A measured pairing is always better evidence than a matching channel number, so
    /// this never overrules one.
    /// </para>
    /// </remarks>
    /// <returns>The controls that were paired this way, for reporting.</returns>
    private HashSet<SensorId> PairByIdentity(
        IEnumerable<SensorId> targets,
        Dictionary<SensorId, SensorId> pairs)
    {
        var added = new HashSet<SensorId>();

        var tachometers = registry.Sensors
            .Where(sensor => sensor.Kind == SensorKind.FanSpeed)
            .ToList();

        foreach (var controlId in targets)
        {
            if (pairs.ContainsKey(controlId)
                || registry.GetControl(controlId) is not { } control)
            {
                continue;
            }

            var device = control.Fingerprint;

            var candidates = tachometers
                .Where(sensor => !pairs.ContainsValue(sensor.Id)
                    && sensor.Fingerprint.ProviderId == device.ProviderId
                    && sensor.Fingerprint.HardwareKey == device.HardwareKey)
                .ToList();

            var match = candidates.FirstOrDefault(sensor => sensor.Fingerprint.Channel == device.Channel)
                ?? (candidates.Count == 1 ? candidates[0] : null);

            if (match is null)
            {
                continue;
            }

            pairs[controlId] = match.Id;
            added.Add(controlId);
        }

        return added;
    }

    /// <summary>Writes discovered pairings into the configuration and applies it.</summary>
    private bool RecordPairs(Dictionary<SensorId, SensorId> pairs)
    {
        var current = configuration.Current;
        var controls = current.Controls.ToArray();

        for (var i = 0; i < controls.Length; i++)
        {
            if (pairs.TryGetValue(controls[i].ControlId, out var tachometer))
            {
                controls[i] = controls[i] with { PairedFanSensorId = tachometer };
            }
        }

        return Apply(current with { Controls = [.. controls] });
    }

    /// <summary>
    /// Applies the amended configuration, which also hands back any pin the run displaced.
    /// </summary>
    private bool Apply(ImpellerConfiguration updated) => !configuration.Apply(updated).HasErrors;

    private ControlBindingDefinition? Binding(SensorId controlId) =>
        configuration.Current.Controls.FirstOrDefault(control => control.ControlId == controlId);

    private string NameOf(SensorId id) =>
        registry.Sensors.FirstOrDefault(sensor => sensor.Id == id)?.Name ?? id.ToString();

    private string DescribeCurrent(FanPairingRun run) =>
        run.CurrentControl.IsNone
            ? "Bringing every fan to a steady speed."
            : $"Slowing {NameOf(run.CurrentControl)} to see which fan follows it.";

    private static string Summarise(List<TuningOutcome> outcomes)
    {
        var done = outcomes.Count(outcome => outcome.Succeeded);
        return $"{done} of {outcomes.Count} succeeded.";
    }

    /// <summary>
    /// A run that never started because something else holds a control it needs.
    /// </summary>
    /// <remarks>
    /// Returned from inside the run's <c>try</c>, so releasing the gate is left to the
    /// <c>finally</c> that is about to execute rather than done here.
    /// </remarks>
    private static TuningReport Refused(TuningKind kind, string blocked) => new(
        kind,
        [new TuningOutcome(SensorId.None, blocked, false, $"{blocked} is in use by something else.")]);

    /// <summary>
    /// A run refused because another one is already going.
    /// </summary>
    /// <remarks>
    /// Refused rather than queued. Both procedures take over the whole machine for minutes at a
    /// time, and a second one starting quietly once the first had finished would be a surprise
    /// nobody was still expecting by then.
    /// </remarks>
    private static TuningReport Busy(TuningKind kind) => new(
        kind,
        [new TuningOutcome(SensorId.None, string.Empty, false, "Another tuning run is already in progress.")]);

    private void Report(TuningKind kind, string stage, string detail, int completed, int total, bool finished = false) =>
        Progressed?.Invoke(this, new TuningProgress(kind, stage, detail, completed, total, finished));
}
