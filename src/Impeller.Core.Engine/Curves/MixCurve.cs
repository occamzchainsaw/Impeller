using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Curves;

/// <summary>
/// Combines several other curves into one output.
/// </summary>
/// <remarks>
/// <para>
/// Inputs are other curves, not sensors, so each input can carry its own hysteresis and
/// response behaviour before being combined. The engine evaluates curves in dependency order,
/// so every input has already produced this tick's value by the time the mix reads it.
/// </para>
/// <para>
/// Inputs that produced no value are skipped rather than treated as zero — a temperature sensor
/// that stopped reporting should not drag a <see cref="MixFunction.Minimum"/> mix down to
/// silence, nor pull an <see cref="MixFunction.Average"/> toward zero. If no input produced a
/// value, the mix produces none either and the engine holds the previous duty.
/// </para>
/// </remarks>
public sealed class MixCurve : FanCurveBase
{
    private CurveId[] _sources;

    /// <param name="id">Curve identity.</param>
    /// <param name="name">User-assigned name.</param>
    /// <param name="function">How to combine the inputs.</param>
    /// <param name="sources">
    /// The input curves. Order matters only for <see cref="MixFunction.Difference"/>.
    /// </param>
    public MixCurve(CurveId id, string name, MixFunction function, IEnumerable<CurveId> sources)
        : base(id, name)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Function = function;
        _sources = sources.ToArray();
    }

    /// <summary>How the inputs are combined.</summary>
    public MixFunction Function { get; set; }

    /// <summary>The input curves.</summary>
    public IReadOnlyList<CurveId> Sources => _sources;

    /// <inheritdoc />
    public override IReadOnlyCollection<CurveId> CurveDependencies => _sources;

    /// <summary>Replaces the input curves.</summary>
    public void SetSources(IEnumerable<CurveId> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToArray();
    }

    /// <inheritdoc />
    public override Duty? Evaluate(ICurveEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sources = _sources;
        if (sources.Length == 0)
        {
            return null;
        }

        float accumulator = 0f;
        var count = 0;

        foreach (var source in sources)
        {
            if (context.GetCurveOutput(source) is not { } value)
            {
                continue;
            }

            var percent = value.Percent;
            count++;

            if (count == 1)
            {
                accumulator = percent;
                continue;
            }

            accumulator = Function switch
            {
                MixFunction.Maximum => MathF.Max(accumulator, percent),
                MixFunction.Minimum => MathF.Min(accumulator, percent),
                MixFunction.Average or MixFunction.Sum => accumulator + percent,
                MixFunction.Difference => accumulator - percent,
                _ => MathF.Max(accumulator, percent),
            };
        }

        if (count == 0)
        {
            return null;
        }

        if (Function == MixFunction.Average)
        {
            accumulator /= count;
        }

        return new Duty(accumulator);
    }
}
