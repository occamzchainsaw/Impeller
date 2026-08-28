using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests;

public class CurveGraphTests
{
    private static FlatCurve Flat(string name) => new(CurveId.New(), name, new Duty(50f));

    private static MixCurve Mix(string name, params CurveId[] sources) =>
        new(CurveId.New(), name, MixFunction.Maximum, sources);

    [Fact]
    public void Independent_curves_are_all_returned()
    {
        var a = Flat("a");
        var b = Flat("b");

        var result = CurveGraph.Sort([a, b]);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Ordered.Count);
    }

    [Fact]
    public void A_composite_curve_is_ordered_after_its_inputs()
    {
        var cpu = Flat("cpu");
        var gpu = Flat("gpu");
        var mix = Mix("mix", cpu.Id, gpu.Id);

        // Deliberately supplied with the dependent curve first.
        var result = CurveGraph.Sort([mix, cpu, gpu]);

        Assert.True(result.IsValid);
        var order = result.Ordered.Select(c => c.Id).ToList();
        Assert.True(order.IndexOf(cpu.Id) < order.IndexOf(mix.Id));
        Assert.True(order.IndexOf(gpu.Id) < order.IndexOf(mix.Id));
    }

    [Fact]
    public void Chained_composites_are_ordered_deepest_first()
    {
        var leaf = Flat("leaf");
        var middle = Mix("middle", leaf.Id);
        var top = Mix("top", middle.Id);

        var result = CurveGraph.Sort([top, middle, leaf]);

        Assert.True(result.IsValid);
        Assert.Equal([leaf.Id, middle.Id, top.Id], result.Ordered.Select(c => c.Id));
    }

    [Fact]
    public void A_curve_shared_by_two_dependents_appears_once_before_both()
    {
        var shared = Flat("shared");
        var left = Mix("left", shared.Id);
        var right = Mix("right", shared.Id);

        var result = CurveGraph.Sort([left, right, shared]);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.Ordered.Count);
        Assert.Single(result.Ordered, c => c.Id == shared.Id);

        var order = result.Ordered.Select(c => c.Id).ToList();
        Assert.True(order.IndexOf(shared.Id) < order.IndexOf(left.Id));
        Assert.True(order.IndexOf(shared.Id) < order.IndexOf(right.Id));
    }

    [Fact]
    public void A_direct_cycle_is_reported_rather_than_ordered()
    {
        var a = Mix("a");
        var b = Mix("b", a.Id);
        a.SetSources([b.Id]);

        var result = CurveGraph.Sort([a, b]);

        Assert.False(result.IsValid);
        Assert.Empty(result.Ordered);
        Assert.Contains(a.Id, result.Cycle);
        Assert.Contains(b.Id, result.Cycle);
    }

    [Fact]
    public void A_self_reference_is_reported_as_a_cycle()
    {
        var a = Mix("a");
        a.SetSources([a.Id]);

        var result = CurveGraph.Sort([a]);

        Assert.False(result.IsValid);
        Assert.Contains(a.Id, result.Cycle);
    }

    [Fact]
    public void A_longer_cycle_is_reported()
    {
        var a = Mix("a");
        var b = Mix("b", a.Id);
        var c = Mix("c", b.Id);
        a.SetSources([c.Id]);

        var result = CurveGraph.Sort([a, b, c]);

        Assert.False(result.IsValid);
        Assert.Empty(result.Ordered);
    }

    [Fact]
    public void A_reference_to_a_deleted_curve_is_reported_but_not_fatal()
    {
        var deleted = CurveId.New();
        var mix = Mix("mix", deleted);

        var result = CurveGraph.Sort([mix]);

        Assert.True(result.IsValid);
        Assert.Equal([mix.Id], result.Ordered.Select(c => c.Id));
        Assert.Equal([deleted], result.MissingDependencies);
    }

    [Fact]
    public void An_empty_set_sorts_to_nothing()
    {
        var result = CurveGraph.Sort([]);

        Assert.True(result.IsValid);
        Assert.Empty(result.Ordered);
    }
}
