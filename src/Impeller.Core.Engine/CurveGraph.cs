using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine;

/// <summary>The outcome of ordering a set of curves for evaluation.</summary>
/// <param name="Ordered">
/// The curves in dependency order: every curve appears after each curve it reads. Empty when
/// <paramref name="Cycle"/> is non-empty.
/// </param>
/// <param name="Cycle">
/// The curves forming a dependency cycle, in the order they were walked, or empty when the
/// graph is acyclic.
/// </param>
/// <param name="MissingDependencies">
/// Referenced curves that are not in the supplied set — a mix curve pointing at a curve the
/// user has since deleted. Not fatal: the mix simply gets no value from that input.
/// </param>
public readonly record struct CurveOrderResult(
    IReadOnlyList<IFanCurve> Ordered,
    IReadOnlyList<CurveId> Cycle,
    IReadOnlyList<CurveId> MissingDependencies)
{
    /// <summary>Whether the curves can be evaluated.</summary>
    public bool IsValid => Cycle.Count == 0;
}

/// <summary>
/// Orders curves so that composite curves evaluate after their inputs.
/// </summary>
/// <remarks>
/// A mix curve reading two other curves must see this tick's values, not last tick's, so the
/// evaluation order is a topological sort of the dependency graph. Cycles — a mix that
/// eventually feeds itself — are rejected here, when a configuration is loaded or edited,
/// rather than being discovered as a stack overflow on the tick loop.
/// </remarks>
public static class CurveGraph
{
    /// <summary>
    /// Sorts curves into evaluation order.
    /// </summary>
    /// <param name="curves">The curves to order. Duplicated ids are ignored after the first.</param>
    /// <returns>
    /// The ordering, or the cycle that prevents one. Inspect <see cref="CurveOrderResult.IsValid"/>
    /// before using the result.
    /// </returns>
    public static CurveOrderResult Sort(IEnumerable<IFanCurve> curves)
    {
        ArgumentNullException.ThrowIfNull(curves);

        var byId = new Dictionary<CurveId, IFanCurve>();
        foreach (var curve in curves)
        {
            byId.TryAdd(curve.Id, curve);
        }

        var ordered = new List<IFanCurve>(byId.Count);
        var missing = new HashSet<CurveId>();
        var state = new Dictionary<CurveId, VisitState>(byId.Count);
        var path = new List<CurveId>();

        foreach (var id in byId.Keys)
        {
            if (Visit(id, byId, state, ordered, missing, path) is { } cycle)
            {
                return new CurveOrderResult([], cycle, [.. missing]);
            }
        }

        return new CurveOrderResult(ordered, [], [.. missing]);
    }

    /// <summary>
    /// Depth-first visit. Returns the cycle if one is found, otherwise null.
    /// </summary>
    private static IReadOnlyList<CurveId>? Visit(
        CurveId id,
        Dictionary<CurveId, IFanCurve> byId,
        Dictionary<CurveId, VisitState> state,
        List<IFanCurve> ordered,
        HashSet<CurveId> missing,
        List<CurveId> path)
    {
        if (state.TryGetValue(id, out var visited))
        {
            if (visited == VisitState.Done)
            {
                return null;
            }

            // Re-entering a node still on the stack closes a cycle. Report it from its first
            // occurrence so the message names the loop rather than the walk that reached it.
            var start = path.IndexOf(id);
            var cycle = path.Skip(start < 0 ? 0 : start).Append(id).ToArray();
            return cycle;
        }

        if (!byId.TryGetValue(id, out var curve))
        {
            missing.Add(id);
            return null;
        }

        state[id] = VisitState.InProgress;
        path.Add(id);

        foreach (var dependency in curve.CurveDependencies)
        {
            if (Visit(dependency, byId, state, ordered, missing, path) is { } cycle)
            {
                return cycle;
            }
        }

        path.RemoveAt(path.Count - 1);
        state[id] = VisitState.Done;
        ordered.Add(curve);
        return null;
    }

    private enum VisitState
    {
        InProgress,
        Done,
    }
}
