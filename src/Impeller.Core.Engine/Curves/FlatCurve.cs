using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Curves;

/// <summary>
/// Commands one fixed duty, regardless of any sensor.
/// </summary>
/// <remarks>
/// The simplest curve, and the one a plugin-driven or manually-set fan normally uses as its
/// resting assignment. Reads nothing, so it always produces a value.
/// </remarks>
public sealed class FlatCurve(CurveId id, string name, Duty duty) : FanCurveBase(id, name)
{
    /// <summary>The duty this curve commands.</summary>
    public Duty Duty { get; set; } = duty;

    /// <inheritdoc />
    public override Duty? Evaluate(ICurveEvaluationContext context) => Duty;
}
