using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Tuning;

/// <summary>Where an auto-pairing run has got to.</summary>
public enum PairingPhase
{
    /// <summary>Everything is at the baseline duty, waiting for the fans to settle there.</summary>
    Settling = 0,

    /// <summary>One control is dropped, watching for a tacho that falls with it.</summary>
    Testing,

    /// <summary>That control is back at baseline, letting its fan recover before the next one.</summary>
    Recovering,

    /// <summary>Done.</summary>
    Finished,
}

/// <summary>Knobs on the pairing procedure.</summary>
public sealed record FanPairingSettings
{
    /// <summary>
    /// The duty everything is held at while not being tested.
    /// </summary>
    /// <remarks>
    /// High enough that dropping from it produces a change a tacho can see, low enough that the
    /// machine is not deafening for the minute or two the run takes.
    /// </remarks>
    public Duty Baseline { get; init; } = new(75f);

    /// <summary>The duty the control under test is dropped to.</summary>
    public Duty Dropped { get; init; } = new(35f);

    /// <summary>
    /// How far a fan's speed must fall, as a fraction of its baseline, to count as the one that
    /// moved.
    /// </summary>
    /// <remarks>
    /// A fifth is well clear of the few percent a tacho wanders by on its own, and well under the
    /// half or so that a real 75-to-35 drop produces. Everything between those is where a wrong
    /// answer would come from.
    /// </remarks>
    public float MinimumDropRatio { get; init; } = 0.2f;

    /// <summary>How many consecutive flat samples count as settled.</summary>
    public int SettleSamples { get; init; } = 2;

    /// <summary>How many samples the trend fit spans.</summary>
    public int TrendWindow { get; init; } = 3;

    /// <summary>How much a fitted speed may move and still be called flat, in RPM.</summary>
    public float RpmTolerance { get; init; } = 32f;

    /// <summary>How long to wait for every fan to settle at the baseline, in samples.</summary>
    public int SettleTimeout { get; init; } = 60;

    /// <summary>How long one control is held down before giving up on it, in samples.</summary>
    public int TestTimeout { get; init; } = 30;

    /// <summary>How long a fan is given to climb back to baseline before the next test, in samples.</summary>
    public int RecoverSamples { get; init; } = 3;
}

/// <summary>
/// Works out which tacho belongs to which control, by moving one fan at a time and watching what
/// changes.
/// </summary>
/// <remarks>
/// <para>
/// Low-tech and effective, which is why it is worth keeping. Nothing on a motherboard says that
/// header three drives the tacho on channel five; the only way to find out is to change one and
/// see what moves. Everything runs at a baseline first so that a drop is a drop rather than a fan
/// that happened to be idle.
/// </para>
/// <para>
/// Advanced one sample at a time, for the same reason as <see cref="CalibrationRun"/>: the whole
/// procedure is counting and comparing, and a test that has to wait real seconds to check it is
/// a test nobody runs.
/// </para>
/// </remarks>
public sealed class FanPairingRun
{
    private readonly FanPairingSettings _settings;
    private readonly List<SensorId> _controls;
    private readonly List<SensorId> _tachometers;
    private readonly Dictionary<SensorId, SensorId> _pairs = [];
    private readonly Dictionary<SensorId, float> _baseline = [];
    private readonly Dictionary<SensorId, StabilityDetector> _stability = [];

    private int _index = -1;
    private int _waited;

    /// <param name="controls">The controls to identify, in the order they will be tested.</param>
    /// <param name="tachometers">Every fan-speed sensor that might belong to one of them.</param>
    /// <param name="settings">Overrides for the procedure, or null for the defaults.</param>
    public FanPairingRun(
        IEnumerable<SensorId> controls,
        IEnumerable<SensorId> tachometers,
        FanPairingSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(tachometers);

        _settings = settings ?? new FanPairingSettings();
        _controls = [.. controls];
        _tachometers = [.. tachometers];

        foreach (var tachometer in _tachometers)
        {
            _stability[tachometer] = NewDetector();
        }

        if (_controls.Count == 0 || _tachometers.Count == 0)
        {
            Phase = PairingPhase.Finished;
        }
    }

    /// <summary>Where the run has got to.</summary>
    public PairingPhase Phase { get; private set; } = PairingPhase.Settling;

    /// <summary>The control currently being moved, or none while settling or finished.</summary>
    public SensorId CurrentControl =>
        Phase == PairingPhase.Testing && _index >= 0 && _index < _controls.Count
            ? _controls[_index]
            : SensorId.None;

    /// <summary>What has been worked out so far: control to its tacho.</summary>
    public IReadOnlyDictionary<SensorId, SensorId> Pairs => _pairs;

    /// <summary>Whether the run has finished.</summary>
    public bool IsComplete => Phase == PairingPhase.Finished;

    /// <summary>
    /// The duty the caller should be holding a control at right now.
    /// </summary>
    /// <remarks>
    /// Every control is driven, not only the ones being identified. A fan left on its own curve
    /// would change speed for its own reasons partway through, and there is no way to tell that
    /// apart from the drop the run is looking for.
    /// </remarks>
    public Duty CommandFor(SensorId controlId) =>
        controlId == CurrentControl ? _settings.Dropped : _settings.Baseline;

    /// <summary>
    /// Feeds one round of tacho readings, taken while the controls were held at
    /// <see cref="CommandFor"/>.
    /// </summary>
    /// <param name="readings">Every fan-speed sensor's current value, by id.</param>
    public void Advance(IReadOnlyDictionary<SensorId, float?> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        if (IsComplete)
        {
            return;
        }

        _waited++;

        switch (Phase)
        {
            case PairingPhase.Settling:
                Settle(readings);
                break;

            case PairingPhase.Testing:
                Test(readings);
                break;

            case PairingPhase.Recovering:
                if (_waited >= _settings.RecoverSamples)
                {
                    BeginNextTest(readings);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Waits for every fan to reach the baseline before touching anything.
    /// </summary>
    /// <remarks>
    /// The timeout is generous and not a failure. A machine with one fan that never quite holds
    /// still should still get the other seven paired, and a fan that is genuinely wandering will
    /// simply not produce a confident drop later.
    /// </remarks>
    private void Settle(IReadOnlyDictionary<SensorId, float?> readings)
    {
        var settled = true;

        foreach (var tachometer in _tachometers)
        {
            if (!_stability[tachometer].IsStable(readings.GetValueOrDefault(tachometer)))
            {
                settled = false;
            }
        }

        if (settled || _waited >= _settings.SettleTimeout)
        {
            BeginNextTest(readings);
        }
    }

    /// <summary>
    /// Watches for the fan that fell when this control did.
    /// </summary>
    /// <remarks>
    /// The largest qualifying drop wins rather than the first one seen. Fans share airflow and a
    /// case fan slowing does move its neighbours a little; taking whichever crossed the line first
    /// would sometimes pair a control with the fan next to the one it drives.
    /// </remarks>
    private void Test(IReadOnlyDictionary<SensorId, float?> readings)
    {
        var best = SensorId.None;
        var bestDrop = _settings.MinimumDropRatio;

        foreach (var tachometer in _tachometers)
        {
            if (_pairs.ContainsValue(tachometer)
                || !_baseline.TryGetValue(tachometer, out var was)
                || was <= 0f
                || readings.GetValueOrDefault(tachometer) is not { } now)
            {
                continue;
            }

            var drop = (was - now) / was;

            if (drop > bestDrop)
            {
                best = tachometer;
                bestDrop = drop;
            }
        }

        if (!best.IsNone)
        {
            _pairs[_controls[_index]] = best;
            BeginRecovery();
            return;
        }

        if (_waited >= _settings.TestTimeout)
        {
            // Nothing moved. Either this header drives no fan, or it drives one with no tacho —
            // both are ordinary, and both are reported as unpaired rather than guessed at.
            BeginRecovery();
        }
    }

    private void BeginRecovery()
    {
        Phase = PairingPhase.Recovering;
        _waited = 0;
    }

    /// <summary>
    /// Moves on to the next control, taking fresh baselines first.
    /// </summary>
    /// <remarks>
    /// Baselines are re-read before every test rather than taken once at the start. A run over
    /// eight headers takes minutes, and the machine warms up while it happens.
    /// </remarks>
    private void BeginNextTest(IReadOnlyDictionary<SensorId, float?> readings)
    {
        _index++;

        if (_index >= _controls.Count)
        {
            Phase = PairingPhase.Finished;
            return;
        }

        _baseline.Clear();

        foreach (var tachometer in _tachometers)
        {
            if (readings.GetValueOrDefault(tachometer) is { } value)
            {
                _baseline[tachometer] = value;
            }
        }

        Phase = PairingPhase.Testing;
        _waited = 0;
    }

    private StabilityDetector NewDetector() =>
        new(_settings.SettleSamples, _settings.TrendWindow, _settings.RpmTolerance);
}
