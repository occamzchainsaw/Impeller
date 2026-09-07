using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Curves;

/// <summary>
/// Seeks the duty that holds a temperature at a target, rather than mapping temperature to duty.
/// </summary>
/// <remarks>
/// <para>
/// The other curve types are functions: give them a reading and they return a duty. This one is a
/// controller. Once the temperature is high enough to count as load, it stops asking "what duty
/// does this temperature deserve" and starts asking "is what I am doing working" — nudging the
/// duty up while the trend is still rising and easing it back down when it is not. That is what
/// lets it settle on whatever duty this particular machine needs to hold the target, instead of
/// whatever duty a line drawn through two points happens to say.
/// </para>
/// <para>
/// Two behaviours are deliberately asymmetric, and both matter. Up steps are a full
/// <see cref="Step"/> while down steps are half of one, so it converges from below without
/// oscillating. And rising is judged on a short trend window while falling is judged on a long
/// one, so a brief spike is acted on and a brief dip is ignored — the right bias when the cost of
/// reacting late to heat is worse than the cost of a fan running slightly fast.
/// </para>
/// <para>
/// Below the load threshold it degrades to an ordinary hysteresis-gated ramp from
/// <see cref="MinimumDuty"/> at <see cref="IdleTemperature"/> to <see cref="MaximumDuty"/> at
/// <see cref="LoadTemperature"/>, with a hard floor at or below idle.
/// </para>
/// </remarks>
public sealed class AutoCurve : FanCurveBase
{
    /// <summary>
    /// How far the temperature must move before the idle ramp re-evaluates, in degrees. Not
    /// exposed: the load-mode <see cref="Deadband"/> is the knob users actually reason about, and
    /// this one only shapes the ramp the curve falls back to.
    /// </summary>
    private const float IdleRampDeadband = 2f;

    /// <summary>
    /// Where in the duty range the load integrator starts if it is entered before any duty has
    /// been settled on. Three-quarters up: high enough not to fall behind a load that has already
    /// begun, low enough that it is not simply full speed.
    /// </summary>
    private const float LoadSeedFraction = 0.75f;

    /// <summary>
    /// How far above idle the temperature must climb, as a fraction of the way to the load
    /// threshold's lower edge, before the integrator takes over from the ramp.
    /// </summary>
    private const float LoadEntryFraction = 0.75f;

    /// <summary>Floor a curve works within when none was given. FanControl's own default.</summary>
    private static readonly Duty DefaultMinimumDuty = new(50f);

    /// <summary>Ceiling a curve works within when none was given. FanControl's own default.</summary>
    private static readonly Duty DefaultMaximumDuty = new(80f);

    private readonly SensorId[] _dependencies;

    private TimeSpan _responseTime;

    private LinearRegressionTrend _shortTrend = null!;
    private LinearRegressionTrend _longTrend = null!;
    private SustainTimer _stepUpDelay = null!;
    private SustainTimer _stepDownDelay = null!;
    private HysteresisGate _idleGate = null!;

    private Duty? _command;
    private Duty? _lastLoadTarget;
    private bool _underLoad;

    /// <param name="id">Curve identity.</param>
    /// <param name="name">User-assigned name.</param>
    /// <param name="source">The temperature this curve is trying to hold.</param>
    /// <param name="idleTemperature">At or below this, the curve commands <paramref name="minimumDuty"/>.</param>
    /// <param name="loadTemperature">The temperature the curve seeks to hold.</param>
    /// <param name="minimumDuty">
    /// Floor of the range the curve works within. Defaults to 50%, matching FanControl.
    /// </param>
    /// <param name="maximumDuty">
    /// Ceiling of the range the curve works within. Defaults to 80%, matching FanControl: a
    /// seeking controller with no ceiling below full speed turns ordinary load into full speed.
    /// </param>
    /// <param name="step">
    /// How much the duty moves per adjustment while under load, in percentage points. Down steps
    /// are half this.
    /// </param>
    /// <param name="deadband">
    /// How far below <paramref name="loadTemperature"/> still counts as holding the target, in
    /// degrees. Widening it makes the curve less eager to chase small overshoots.
    /// </param>
    /// <param name="responseTime">
    /// How long a trend must persist before the duty moves. Also sizes the trend windows, at three
    /// and five times this many samples.
    /// </param>
    public AutoCurve(
        CurveId id,
        string name,
        SensorId source,
        float idleTemperature = 35f,
        float loadTemperature = 70f,
        Duty? minimumDuty = null,
        Duty? maximumDuty = null,
        float step = 2f,
        float deadband = 3f,
        TimeSpan responseTime = default)
        : base(id, name)
    {
        Source = source;
        _dependencies = source.IsNone ? [] : [source];

        IdleTemperature = idleTemperature;
        LoadTemperature = loadTemperature;

        // Nullable, not a default-valued struct. default(Duty) is 0%, which is a perfectly
        // ordinary floor to ask for — a GPU whose fan should idle off, a case fan allowed to
        // stop — so using it as the "not specified" sentinel silently replaced every one of those
        // with 50%. The curve then held the fan at half speed while the saved configuration, the
        // UI and the engine's own readback all agreed the floor was zero, and nothing anywhere
        // contradicted itself.
        MinimumDuty = minimumDuty ?? DefaultMinimumDuty;
        MaximumDuty = maximumDuty ?? DefaultMaximumDuty;
        Step = step;
        Deadband = deadband;
        _responseTime = responseTime <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : responseTime;

        Rebuild();
    }

    /// <summary>The temperature sensor driving the curve.</summary>
    public SensorId Source { get; }

    /// <summary>At or below this temperature the curve commands <see cref="MinimumDuty"/>.</summary>
    public float IdleTemperature { get; set; }

    /// <summary>The temperature the curve seeks to hold once under load.</summary>
    public float LoadTemperature { get; set; }

    /// <summary>Floor of the duty range the curve works within.</summary>
    public Duty MinimumDuty { get; set; }

    /// <summary>Ceiling of the duty range the curve works within.</summary>
    public Duty MaximumDuty { get; set; }

    /// <summary>How far the duty moves per upward adjustment, in percentage points.</summary>
    public float Step { get; set; }

    /// <summary>How far below <see cref="LoadTemperature"/> still counts as on target, in degrees.</summary>
    public float Deadband { get; set; }

    /// <summary>
    /// How long a trend must persist before the duty moves. Changing it rebuilds the trend
    /// windows, discarding accumulated history, since a window sized for the old value says
    /// nothing about the new one.
    /// </summary>
    public TimeSpan ResponseTime
    {
        get => _responseTime;
        set
        {
            var clamped = value <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : value;
            if (clamped == _responseTime)
            {
                return;
            }

            _responseTime = clamped;
            Rebuild();
        }
    }

    /// <summary>Whether the integrator currently has control, rather than the idle ramp.</summary>
    public bool IsUnderLoad => _underLoad;

    /// <inheritdoc />
    public override IReadOnlyCollection<SensorId> SensorDependencies => _dependencies;

    /// <inheritdoc />
    public override Duty? Evaluate(ICurveEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetSensorValue(Source) is not { } temperature)
        {
            // No reading. Hold, and keep the state we have — a gap in readings is not a reason to
            // forget which side of the load threshold we were on.
            return null;
        }

        if (_command is not { } previous)
        {
            // Nothing settled yet. Snap to whichever end of the range the reading calls for, and
            // seed the integrator so that entering load mode next tick starts somewhere sensible.
            _lastLoadTarget ??= new Duty(
                MinimumDuty.Percent + (LoadSeedFraction * (MaximumDuty.Percent - MinimumDuty.Percent)));

            _command = temperature > LoadTemperature ? MaximumDuty : MinimumDuty;
            return _command;
        }

        // Both windows are advanced every tick, whichever mode is in charge, so neither goes stale
        // while the other is being consulted.
        var longTrend = _longTrend.Update(temperature);
        var shortTrend = _shortTrend.Update(temperature);

        if (IsLoadTemperature(temperature))
        {
            _underLoad = true;
            _command = StepUnderLoad(previous, temperature, shortTrend, longTrend, context.Elapsed);
            return _command;
        }

        if (_underLoad
            && longTrend is Trend.Neutral or Trend.Down
            && _stepDownDelay.Elapse(context.Elapsed))
        {
            // Load is genuinely over, not momentarily dipping. Hand back to the ramp with a clean
            // gate so the first idle reading is accepted rather than debounced against load-era
            // history.
            _underLoad = false;
            _stepDownDelay.Reset();
            _stepUpDelay.Reset();
            _idleGate.Reset();
        }

        if (_underLoad)
        {
            return previous;
        }

        _command = RampWhileIdle(temperature, context.Elapsed) ?? previous;
        return _command;
    }

    /// <inheritdoc />
    public override void Reset()
    {
        _shortTrend.Reset();
        _longTrend.Reset();
        _stepUpDelay.Reset();
        _stepDownDelay.Reset();
        _idleGate.Reset();

        _command = null;
        _lastLoadTarget = null;
        _underLoad = false;
    }

    /// <summary>
    /// One adjustment of the load integrator. Returns the duty to command, which is the previous
    /// target unchanged when neither direction has sustained itself long enough to act on.
    /// </summary>
    private Duty StepUnderLoad(
        Duty previous,
        float temperature,
        Trend shortTrend,
        Trend longTrend,
        TimeSpan elapsed)
    {
        var target = _lastLoadTarget ?? previous;

        if (ShouldStepUp(temperature, shortTrend))
        {
            _stepDownDelay.Reset();

            if (_stepUpDelay.Elapse(elapsed))
            {
                target = Clamp(previous.Percent + Step);
            }
        }
        else if (ShouldStepDown(temperature, longTrend))
        {
            _stepUpDelay.Reset();

            if (_stepDownDelay.Elapse(elapsed))
            {
                // Half a step down against a full step up. Converging from below and easing off
                // slowly is what stops the duty hunting either side of the target.
                target = Clamp(previous.Percent - (Step / 2f));
            }
        }
        else
        {
            _stepUpDelay.Reset();
            _stepDownDelay.Reset();
        }

        _lastLoadTarget = target;
        return target;
    }

    /// <summary>
    /// The fallback ramp used below the load threshold. Returns null when the gate has suppressed
    /// the reading and there is nothing new to say.
    /// </summary>
    private Duty? RampWhileIdle(float temperature, TimeSpan elapsed)
    {
        if (temperature <= IdleTemperature)
        {
            _idleGate.Reset();
            return MinimumDuty;
        }

        // Past the top of the ramp the answer is already saturated, so debouncing there would only
        // delay a fan that should have responded.
        var accepted = _idleGate.Offer(temperature, elapsed, bypassSuppression: temperature >= LoadTemperature);

        if (accepted is not { } gated)
        {
            return null;
        }

        var span = LoadTemperature - IdleTemperature;

        if (span <= 0f)
        {
            // A degenerate or inverted range has no ramp. Anything above idle is at the top of it.
            return MaximumDuty;
        }

        var slope = (MaximumDuty.Percent - MinimumDuty.Percent) / span;
        return Clamp(MinimumDuty.Percent + (slope * (gated - IdleTemperature)));
    }

    /// <summary>
    /// Whether the integrator should take over. Deliberately below the target: waiting until the
    /// temperature actually reaches the target means always arriving late.
    /// </summary>
    private bool IsLoadTemperature(float temperature) =>
        temperature >= IdleTemperature
            + (LoadEntryFraction * (LoadTemperature - Deadband - IdleTemperature));

    /// <summary>
    /// Step up when the short trend is rising and we are within the deadband of the target, or
    /// unconditionally once past the target — at which point the trend no longer matters.
    /// </summary>
    private bool ShouldStepUp(float temperature, Trend trend) =>
        (trend == Trend.Up && temperature >= LoadTemperature - Deadband)
        || temperature > LoadTemperature;

    /// <summary>
    /// Step down on a falling long trend, or when flat and comfortably below the target. Note that
    /// flat-but-on-target is neither: that is the state the curve is trying to reach.
    /// </summary>
    private bool ShouldStepDown(float temperature, Trend trend) =>
        trend == Trend.Down
        || (trend == Trend.Neutral && temperature <= LoadTemperature - Deadband);

    private Duty Clamp(float percent) =>
        new(Math.Clamp(percent, MinimumDuty.Percent, MaximumDuty.Percent));

    /// <summary>
    /// Rebuilds everything sized from <see cref="ResponseTime"/>. Windows are measured in samples,
    /// so at the engine's nominal one-second tick they span three and five times the response time.
    /// </summary>
    private void Rebuild()
    {
        var samples = Math.Max(1, (int)Math.Round(_responseTime.TotalSeconds));

        _shortTrend = new LinearRegressionTrend(Math.Max(2, samples * 3), tolerance: 1f);
        _longTrend = new LinearRegressionTrend(Math.Max(2, samples * 5), tolerance: 1f);

        _stepUpDelay = new SustainTimer(_responseTime);
        _stepDownDelay = new SustainTimer(_responseTime * 2);

        _idleGate = new HysteresisGate(new HysteresisSettings(
            DeadbandUp: IdleRampDeadband,
            DeadbandDown: IdleRampDeadband,
            ResponseUp: _responseTime,
            ResponseDown: _responseTime));
    }

    /// <summary>
    /// Fires once a condition has held for long enough, and keeps firing until reset.
    /// </summary>
    /// <remarks>
    /// The latch is the point. The first adjustment waits out the response time; every adjustment
    /// after that happens on each tick, so a sustained climb ramps continuously instead of
    /// stuttering one step per response interval. Whoever observes a change of direction resets it.
    /// </remarks>
    private sealed class SustainTimer(TimeSpan threshold)
    {
        private TimeSpan _accumulated;

        public bool Elapse(TimeSpan elapsed)
        {
            _accumulated += elapsed;
            return _accumulated >= threshold;
        }

        public void Reset() => _accumulated = TimeSpan.Zero;
    }
}
