using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine.Configuration;

namespace Impeller.Core.Engine.Tuning;

/// <summary>Which procedure is running.</summary>
public enum TuningKind
{
    /// <summary>Measuring one fan's duty-to-speed table and its start and stop thresholds.</summary>
    Calibration = 0,

    /// <summary>Working out which tacho belongs to which control.</summary>
    Pairing,
}

/// <summary>Where a tuning run has got to, for a progress dialog to render.</summary>
/// <param name="Kind">Which procedure.</param>
/// <param name="Stage">What it is doing, in a few words.</param>
/// <param name="Detail">The specifics — which fan, at what duty, reading what.</param>
/// <param name="Completed">How many controls are done.</param>
/// <param name="Total">How many there are.</param>
/// <param name="Finished">Whether this is the last report.</param>
public sealed record TuningProgress(
    TuningKind Kind,
    string Stage,
    string Detail,
    int Completed,
    int Total,
    bool Finished = false);

/// <summary>What a run worked out about one control.</summary>
/// <param name="ControlId">Which control.</param>
/// <param name="ControlName">What it is called, so a report reads without a second lookup.</param>
/// <param name="Succeeded">Whether anything usable came out.</param>
/// <param name="Message">What happened, in a sentence.</param>
public sealed record TuningOutcome(
    SensorId ControlId,
    string ControlName,
    bool Succeeded,
    string Message);

/// <summary>Everything a finished run has to say.</summary>
/// <param name="Kind">Which procedure ran.</param>
/// <param name="Outcomes">One entry per control it touched.</param>
/// <param name="Cancelled">Whether the user stopped it early.</param>
/// <param name="Saved">Whether the results were written into the configuration.</param>
public sealed record TuningReport(
    TuningKind Kind,
    EquatableArray<TuningOutcome> Outcomes,
    bool Cancelled = false,
    bool Saved = false);

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

        var outcomes = targets.Select(controlId => new TuningOutcome(
            controlId,
            NameOf(controlId),
            run.Pairs.ContainsKey(controlId),
            run.Pairs.TryGetValue(controlId, out var tachometer)
                ? $"Paired with {NameOf(tachometer)}."
                : "Nothing changed speed with it. The header may be empty, or its fan may have no tacho."))
            .ToList();

        var saved = run.Pairs.Count > 0 && RecordPairs(run.Pairs);

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
            };
        }

        return Apply(current with { Controls = [.. controls] });
    }

    /// <summary>Writes discovered pairings into the configuration and applies it.</summary>
    private bool RecordPairs(IReadOnlyDictionary<SensorId, SensorId> pairs)
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
