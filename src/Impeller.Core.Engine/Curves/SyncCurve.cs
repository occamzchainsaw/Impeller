using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Curves;

/// <summary>
/// Mirrors another curve's output or another control's duty, optionally shifted.
/// </summary>
/// <remarks>
/// <para>
/// Used to gang several fans together while letting each run slightly faster or slower — a rear
/// exhaust tracking the CPU cooler a few points below it, say. The offset is applied either as a
/// proportion of the source duty or as a flat number of percentage points.
/// </para>
/// <para>
/// Mirroring a <em>control</em> is the more useful of the two and is what the app this replaces
/// does. It differs from mirroring that control's curve in exactly the cases a user notices: the
/// control's own floor and ceiling, its ramp limit, and any manual override standing on it are
/// all already baked into the value being copied. Sync a fan to a pump capped at 60% and it
/// follows the pump to 60%, not to the 100% the pump's curve was asking for.
/// </para>
/// <para>
/// Reading a control lags a tick, since every curve is evaluated before any control is written.
/// That is what a mirror should do — and it is also why a sync curve that reaches its own control
/// is rejected at load time rather than merely damped.
/// </para>
/// </remarks>
public sealed class SyncCurve : FanCurveBase
{
    private CurveId[] _curveDependencies = [];
    private SensorId[] _controlDependencies = [];

    private CurveId _sourceCurve;
    private SensorId _sourceControl;

    /// <param name="id">Curve identity.</param>
    /// <param name="name">User-assigned name.</param>
    /// <param name="sourceCurve">The curve to mirror.</param>
    /// <param name="offset">
    /// Shift applied to the mirrored duty: percentage points when
    /// <paramref name="proportional"/> is false, otherwise a percentage of the source value.
    /// </param>
    /// <param name="proportional">Whether <paramref name="offset"/> scales the source rather than shifting it.</param>
    public SyncCurve(CurveId id, string name, CurveId sourceCurve, float offset = 0f, bool proportional = false)
        : base(id, name)
    {
        SourceCurve = sourceCurve;
        Offset = offset;
        Proportional = proportional;
    }

    /// <param name="id">Curve identity.</param>
    /// <param name="name">User-assigned name.</param>
    /// <param name="sourceControl">The control whose duty to mirror.</param>
    /// <param name="offset">
    /// Shift applied to the mirrored duty: percentage points when
    /// <paramref name="proportional"/> is false, otherwise a percentage of the source value.
    /// </param>
    /// <param name="proportional">Whether <paramref name="offset"/> scales the source rather than shifting it.</param>
    public SyncCurve(CurveId id, string name, SensorId sourceControl, float offset = 0f, bool proportional = false)
        : base(id, name)
    {
        SourceControl = sourceControl;
        Offset = offset;
        Proportional = proportional;
    }

    /// <summary>Which kind of thing this curve is mirroring.</summary>
    public SyncSourceKind SourceKind { get; private set; }

    /// <summary>
    /// The curve being mirrored. Setting it switches the source kind; setting it to
    /// <see cref="CurveId.None"/> leaves the curve with no source at all.
    /// </summary>
    public CurveId SourceCurve
    {
        get => _sourceCurve;
        set
        {
            _sourceCurve = value;
            _sourceControl = SensorId.None;
            _curveDependencies = value.IsNone ? [] : [value];
            _controlDependencies = [];
            SourceKind = value.IsNone ? SyncSourceKind.None : SyncSourceKind.Curve;
        }
    }

    /// <summary>
    /// The control being mirrored. Setting it switches the source kind; setting it to
    /// <see cref="SensorId.None"/> leaves the curve with no source at all.
    /// </summary>
    public SensorId SourceControl
    {
        get => _sourceControl;
        set
        {
            _sourceControl = value;
            _sourceCurve = CurveId.None;
            _controlDependencies = value.IsNone ? [] : [value];
            _curveDependencies = [];
            SourceKind = value.IsNone ? SyncSourceKind.None : SyncSourceKind.Control;
        }
    }

    /// <summary>The shift applied to the mirrored duty.</summary>
    public float Offset { get; set; }

    /// <summary>Whether the offset scales the source duty rather than shifting it.</summary>
    public bool Proportional { get; set; }

    /// <inheritdoc />
    public override IReadOnlyCollection<CurveId> CurveDependencies => _curveDependencies;

    /// <inheritdoc />
    public override IReadOnlyCollection<SensorId> ControlDependencies => _controlDependencies;

    /// <inheritdoc />
    public override Duty? Evaluate(ICurveEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var source = SourceKind switch
        {
            SyncSourceKind.Curve => context.GetCurveOutput(_sourceCurve),
            SyncSourceKind.Control => context.GetControlDuty(_sourceControl),
            _ => null,
        };

        if (source is not { } value)
        {
            // Nothing to mirror yet — an unwritten control on the first tick, or a source curve
            // that could not produce a value. Hold rather than dropping the fan to the offset.
            return null;
        }

        var percent = Proportional
            ? value.Percent * (1f + (Offset / 100f))
            : value.Percent + Offset;

        return new Duty(percent);
    }
}
