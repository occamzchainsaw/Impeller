using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Curves;

/// <summary>
/// Mirrors another curve's output, optionally shifted.
/// </summary>
/// <remarks>
/// Used to gang several fans to one curve while letting each run slightly faster or slower —
/// a rear exhaust tracking the CPU cooler at a few points below it, say. The offset is applied
/// either as a proportion of the source duty or as a flat number of percentage points.
/// </remarks>
public sealed class SyncCurve : FanCurveBase
{
    private CurveId[] _dependencies;
    private CurveId _source;

    /// <param name="id">Curve identity.</param>
    /// <param name="name">User-assigned name.</param>
    /// <param name="source">The curve to mirror.</param>
    /// <param name="offset">
    /// Shift applied to the mirrored duty: percentage points when
    /// <paramref name="proportional"/> is false, otherwise a percentage of the source value.
    /// </param>
    /// <param name="proportional">Whether <paramref name="offset"/> scales the source rather than shifting it.</param>
    public SyncCurve(CurveId id, string name, CurveId source, float offset = 0f, bool proportional = false)
        : base(id, name)
    {
        _source = source;
        _dependencies = source.IsNone ? [] : [source];
        Offset = offset;
        Proportional = proportional;
    }

    /// <summary>The curve being mirrored.</summary>
    public CurveId Source
    {
        get => _source;
        set
        {
            _source = value;
            _dependencies = value.IsNone ? [] : [value];
        }
    }

    /// <summary>The shift applied to the mirrored duty.</summary>
    public float Offset { get; set; }

    /// <summary>Whether the offset scales the source duty rather than shifting it.</summary>
    public bool Proportional { get; set; }

    /// <inheritdoc />
    public override IReadOnlyCollection<CurveId> CurveDependencies => _dependencies;

    /// <inheritdoc />
    public override Duty? Evaluate(ICurveEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetCurveOutput(_source) is not { } source)
        {
            return null;
        }

        var percent = Proportional
            ? source.Percent * (1f + (Offset / 100f))
            : source.Percent + Offset;

        return new Duty(percent);
    }
}
