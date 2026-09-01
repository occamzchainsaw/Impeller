using System.Text.Json.Serialization;

namespace Impeller.Core.Abstractions;

/// <summary>
/// Identity for a curve, distinct from <see cref="SensorId"/> so a curve and a sensor
/// can never be confused for one another in a saved configuration.
/// </summary>
[JsonConverter(typeof(CurveIdJsonConverter))]
public readonly record struct CurveId(Guid Value)
{
    /// <summary>A id that refers to no curve.</summary>
    public static CurveId None => default;

    /// <summary>True when this refers to no curve.</summary>
    public bool IsNone => Value == Guid.Empty;

    /// <summary>Mints a new curve id.</summary>
    public static CurveId New() => new(Guid.NewGuid());

    /// <summary>Parses a curve id from its round-trip string form.</summary>
    public static bool TryParse(string? text, out CurveId id)
    {
        if (Guid.TryParse(text, out var guid))
        {
            id = new CurveId(guid);
            return true;
        }

        id = None;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// What a curve is allowed to see when it evaluates.
/// </summary>
/// <remarks>
/// Deliberately narrow. A curve reads sensor values and other curves' outputs and returns a
/// duty; it does not reach hardware, mutate state, or know which control it drives. That keeps
/// every curve type a pure function of its inputs, which is what makes them testable without
/// any hardware present.
/// </remarks>
public interface ICurveEvaluationContext
{
    /// <summary>How long since the previous tick. Curves that integrate over time need this rather than assuming 1 s.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>The current tick's timestamp, from the engine's <see cref="TimeProvider"/>.</summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>
    /// The latest value for a sensor, or <see langword="null"/> if it is unknown or not reporting.
    /// </summary>
    float? GetSensorValue(SensorId id);

    /// <summary>
    /// The duty another curve produced this tick, for composite curves.
    /// </summary>
    /// <remarks>
    /// Curves are evaluated in dependency order, so a child curve has already produced its value
    /// by the time a parent asks for it. Cycles are rejected when the configuration is loaded,
    /// not discovered here.
    /// </remarks>
    Duty? GetCurveOutput(CurveId id);

    /// <summary>
    /// The duty a control is actually being held at, or <see langword="null"/> if the engine has
    /// not written it yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the value <em>after</em> the control's own floor, ceiling and ramp limit, which is
    /// the whole reason it is exposed separately from <see cref="GetCurveOutput"/>: a curve that
    /// mirrors a fan wants the speed that fan is really running at, not the speed its curve asked
    /// for before the binding clamped it.
    /// </para>
    /// <para>
    /// It necessarily lags by one tick, since every curve is evaluated before any control is
    /// written. That is the correct behaviour rather than a limitation — a mirror reads what the
    /// thing it mirrors is doing, and reading a value being computed in the same pass is what
    /// makes feedback loops possible in the first place.
    /// </para>
    /// </remarks>
    Duty? GetControlDuty(SensorId controlId);
}

/// <summary>
/// A rule that turns sensor readings into a control output.
/// </summary>
public interface IFanCurve
{
    /// <summary>Stable identity, referenced by controls and by composite curves.</summary>
    CurveId Id { get; }

    /// <summary>User-assigned name.</summary>
    string Name { get; }

    /// <summary>
    /// Every sensor this curve reads. Used to evaluate dependency order, to warn when a
    /// configuration references hardware that is no longer present, and to decide which
    /// providers actually need polling.
    /// </summary>
    IReadOnlyCollection<SensorId> SensorDependencies { get; }

    /// <summary>
    /// Every curve this curve reads. Used for the same ordering and validation, and to reject
    /// cycles at load time.
    /// </summary>
    IReadOnlyCollection<CurveId> CurveDependencies { get; }

    /// <summary>
    /// Every control whose commanded duty this curve reads.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="SensorDependencies"/>, which is about reading a measurement.
    /// This is about reading an <em>output</em>, and it creates a dependency edge that runs
    /// through whichever curve drives that control — so a sync curve pointed, however
    /// indirectly, at the fan it itself drives is a cycle, and is rejected at load time.
    /// </remarks>
    IReadOnlyCollection<SensorId> ControlDependencies { get; }

    /// <summary>
    /// Computes this tick's output.
    /// </summary>
    /// <returns>
    /// The duty to command, or <see langword="null"/> when the curve cannot produce a value —
    /// a missing input sensor, or a smoothing filter that has not accumulated enough samples.
    /// The engine holds the previous duty rather than substituting a default, since guessing
    /// at a fan speed is worse than briefly not changing one.
    /// </returns>
    Duty? Evaluate(ICurveEvaluationContext context);

    /// <summary>
    /// Clears any accumulated internal state (hysteresis latches, trend windows, rolling averages).
    /// Called when the engine resumes from sleep or reloads a configuration, so stale pre-suspend
    /// readings cannot influence the first tick after waking.
    /// </summary>
    void Reset();
}
