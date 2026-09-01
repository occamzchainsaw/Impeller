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
/// <param name="UnboundControls">
/// Controls a sync curve reads that no binding drives. Also not fatal — the sync curve produces
/// no value until something takes charge of the control it is watching.
/// </param>
public readonly record struct CurveOrderResult(
    IReadOnlyList<IFanCurve> Ordered,
    IReadOnlyList<CurveId> Cycle,
    IReadOnlyList<CurveId> MissingDependencies,
    IReadOnlyList<SensorId> UnboundControls)
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
    /// <param name="controlCurves">
    /// Which curve drives which control, so a sync curve reading a control can be ordered
    /// against — and checked for cycles against — the curve behind it. Omit it and control
    /// dependencies are not walked at all, which is the right answer for a curve set that has no
    /// bindings yet.
    /// </param>
    /// <returns>
    /// The ordering, or the cycle that prevents one. Inspect <see cref="CurveOrderResult.IsValid"/>
    /// before using the result.
    /// </returns>
    public static CurveOrderResult Sort(
        IEnumerable<IFanCurve> curves,
        IReadOnlyDictionary<SensorId, CurveId>? controlCurves = null)
    {
        ArgumentNullException.ThrowIfNull(curves);

        var byId = new Dictionary<CurveId, IFanCurve>();
        foreach (var curve in curves)
        {
            byId.TryAdd(curve.Id, curve);
        }

        var context = new SortContext(byId, controlCurves, new List<IFanCurve>(byId.Count));

        foreach (var id in byId.Keys)
        {
            if (Visit(id, context) is { } cycle)
            {
                return new CurveOrderResult([], cycle, [.. context.Missing], [.. context.Unbound]);
            }
        }

        return new CurveOrderResult(context.Ordered, [], [.. context.Missing], [.. context.Unbound]);
    }

    /// <summary>
    /// The state one sort carries through its walk. Grouped into a type so the visit signature
    /// stays legible now that there are two kinds of edge to follow.
    /// </summary>
    private sealed class SortContext(
        Dictionary<CurveId, IFanCurve> byId,
        IReadOnlyDictionary<SensorId, CurveId>? controlCurves,
        List<IFanCurve> ordered)
    {
        public Dictionary<CurveId, IFanCurve> ById { get; } = byId;

        public IReadOnlyDictionary<SensorId, CurveId>? ControlCurves { get; } = controlCurves;

        public List<IFanCurve> Ordered { get; } = ordered;

        public HashSet<CurveId> Missing { get; } = [];

        public HashSet<SensorId> Unbound { get; } = [];

        public Dictionary<CurveId, VisitState> State { get; } = [];

        public List<CurveId> Path { get; } = [];
    }

    /// <summary>
    /// Depth-first visit. Returns the cycle if one is found, otherwise null.
    /// </summary>
    private static IReadOnlyList<CurveId>? Visit(CurveId id, SortContext context)
    {
        if (context.State.TryGetValue(id, out var visited))
        {
            if (visited == VisitState.Done)
            {
                return null;
            }

            // Re-entering a node still on the stack closes a cycle. Report it from its first
            // occurrence so the message names the loop rather than the walk that reached it.
            var start = context.Path.IndexOf(id);
            return [.. context.Path.Skip(start < 0 ? 0 : start), id];
        }

        if (!context.ById.TryGetValue(id, out var curve))
        {
            context.Missing.Add(id);
            return null;
        }

        context.State[id] = VisitState.InProgress;
        context.Path.Add(id);

        foreach (var dependency in curve.CurveDependencies)
        {
            if (Visit(dependency, context) is { } cycle)
            {
                return cycle;
            }
        }

        // A control edge is really an edge to whichever curve drives that control. Following it is
        // what turns "this sync curve eventually drives the fan it reads" into a cycle we reject
        // at load time, rather than a feedback loop nobody notices until a fan starts hunting.
        if (context.ControlCurves is { } controlCurves)
        {
            foreach (var controlId in curve.ControlDependencies)
            {
                if (!controlCurves.TryGetValue(controlId, out var driver) || driver.IsNone)
                {
                    context.Unbound.Add(controlId);
                    continue;
                }

                if (Visit(driver, context) is { } cycle)
                {
                    return cycle;
                }
            }
        }

        context.Path.RemoveAt(context.Path.Count - 1);
        context.State[id] = VisitState.Done;
        context.Ordered.Add(curve);
        return null;
    }

    private enum VisitState
    {
        InProgress,
        Done,
    }
}
