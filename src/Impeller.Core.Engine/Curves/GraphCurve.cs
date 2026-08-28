using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Curves;

/// <summary>
/// One vertex of a <see cref="GraphCurve"/>.
/// </summary>
/// <param name="Input">Sensor value, in the source sensor's units.</param>
/// <param name="Duty">Output commanded at that input.</param>
public readonly record struct CurvePoint(float Input, Duty Duty) : IComparable<CurvePoint>
{
    /// <summary>Convenience constructor taking a raw percentage.</summary>
    public CurvePoint(float input, float dutyPercent)
        : this(input, new Duty(dutyPercent))
    {
    }

    /// <summary>Orders by input value, which is the order the curve must be evaluated in.</summary>
    public int CompareTo(CurvePoint other) => Input.CompareTo(other.Input);

    public static bool operator <(CurvePoint left, CurvePoint right) => left.CompareTo(right) < 0;

    public static bool operator >(CurvePoint left, CurvePoint right) => left.CompareTo(right) > 0;

    public static bool operator <=(CurvePoint left, CurvePoint right) => left.CompareTo(right) <= 0;

    public static bool operator >=(CurvePoint left, CurvePoint right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// Interpolates linearly between an arbitrary set of user-placed points.
/// </summary>
/// <remarks>
/// <para>
/// The most expressive curve type, and the one the graph editor edits directly. Outside the
/// first and last point the output is held flat rather than extrapolated: extrapolating a
/// user's hand-drawn curve past where they stopped drawing it produces speeds they never
/// asked for, which at the top end is loud and at the bottom end can stall a fan.
/// </para>
/// <para>
/// Points are kept sorted by input. Two points sharing an input value form a genuine step,
/// which is legal and occasionally what someone wants.
/// </para>
/// </remarks>
public sealed class GraphCurve : SingleSensorCurveBase
{
    private CurvePoint[] _points;

    /// <param name="id">Curve identity.</param>
    /// <param name="name">User-assigned name.</param>
    /// <param name="source">The sensor driving the curve.</param>
    /// <param name="points">
    /// The vertices. Copied and sorted; the caller's collection is not retained.
    /// </param>
    /// <param name="hysteresis">Change-suppression settings for the input.</param>
    public GraphCurve(
        CurveId id,
        string name,
        SensorId source,
        IEnumerable<CurvePoint> points,
        HysteresisSettings hysteresis = default)
        : base(id, name, source, hysteresis)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = Normalize(points);
    }

    /// <summary>
    /// The curve's vertices, in ascending input order.
    /// </summary>
    public IReadOnlyList<CurvePoint> Points => _points;

    /// <summary>
    /// Replaces the vertices. Sorted on assignment, so the editor can hand over points in
    /// whatever order a drag left them.
    /// </summary>
    public void SetPoints(IEnumerable<CurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = Normalize(points);
    }

    /// <inheritdoc />
    protected override Duty MapToDuty(float value)
    {
        var points = _points;

        if (points.Length == 0)
        {
            return Duty.Off;
        }

        // Strictly below the first point, so the step at that input has not been reached yet.
        if (value < points[0].Input)
        {
            return points[0].Duty;
        }

        // At or past the last point. Tested before the segment scan so that points sharing an
        // input resolve to the top of the step rather than the bottom.
        var last = points[^1];
        if (value >= last.Input)
        {
            return last.Duty;
        }

        // Find the segment containing the value. Curves have a handful of points, so a linear
        // scan beats a binary search's overhead and keeps the hot path branch-predictable.
        for (var i = 1; i < points.Length; i++)
        {
            var upper = points[i];
            if (value > upper.Input)
            {
                continue;
            }

            var lower = points[i - 1];
            var span = upper.Input - lower.Input;

            // Coincident inputs are a deliberate step: take the upper point's duty.
            if (span <= 0f)
            {
                return upper.Duty;
            }

            var t = (value - lower.Input) / span;
            return new Duty(lower.Duty.Percent + (t * (upper.Duty.Percent - lower.Duty.Percent)));
        }

        return last.Duty;
    }

    /// <inheritdoc />
    protected override bool ShouldBypassSuppression(float rawValue) =>
        _points.Length > 0 && (rawValue <= _points[0].Input || rawValue >= _points[^1].Input);

    private static CurvePoint[] Normalize(IEnumerable<CurvePoint> points)
    {
        var array = points.ToArray();
        Array.Sort(array);
        return array;
    }
}
