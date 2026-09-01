using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests.Curves;

public class SyncCurveTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    [Fact]
    public void A_curve_source_is_mirrored()
    {
        var source = CurveId.New();
        var curve = new SyncCurve(CurveId.New(), "sync", source);

        var result = curve.Evaluate(new TestCurveContext().WithCurve(source, new Duty(40f)));

        Assert.Equal(40f, result!.Value.Percent, precision: 3);
    }

    [Fact]
    public void A_control_source_is_mirrored()
    {
        var control = SensorId.New();
        var curve = new SyncCurve(CurveId.New(), "sync", control);

        var result = curve.Evaluate(new TestCurveContext().WithControl(control, new Duty(65f)));

        Assert.Equal(65f, result!.Value.Percent, precision: 3);
    }

    [Theory]
    [InlineData(40f, 10f, false, 50f)]   // flat shift
    [InlineData(40f, -10f, false, 30f)]  // negative shift
    [InlineData(40f, 50f, true, 60f)]    // +50% of 40
    [InlineData(40f, -25f, true, 30f)]   // -25% of 40
    public void The_offset_applies_the_same_way_to_either_source(
        float sourceDuty,
        float offset,
        bool proportional,
        float expected)
    {
        var control = SensorId.New();
        var curve = new SyncCurve(CurveId.New(), "sync", control, offset, proportional);

        var result = curve.Evaluate(new TestCurveContext().WithControl(control, new Duty(sourceDuty)));

        Assert.Equal(expected, result!.Value.Percent, precision: 3);
    }

    [Fact]
    public void An_unwritten_control_yields_no_value_rather_than_the_bare_offset()
    {
        // The fan holds whatever it was doing. Returning the offset alone would drop a synced fan
        // to 10% the moment its partner had not been written yet.
        var curve = new SyncCurve(CurveId.New(), "sync", SensorId.New(), offset: 10f);

        Assert.Null(curve.Evaluate(new TestCurveContext()));
    }

    [Fact]
    public void Setting_a_control_source_replaces_a_curve_source()
    {
        var sourceCurve = CurveId.New();
        var sourceControl = SensorId.New();
        var curve = new SyncCurve(CurveId.New(), "sync", sourceCurve);

        curve.SourceControl = sourceControl;

        Assert.Equal(SyncSourceKind.Control, curve.SourceKind);
        Assert.Empty(curve.CurveDependencies);
        Assert.Equal(sourceControl, Assert.Single(curve.ControlDependencies));
        Assert.True(curve.SourceCurve.IsNone);
    }

    [Fact]
    public void A_source_of_none_leaves_the_curve_with_nothing_to_mirror()
    {
        var curve = new SyncCurve(CurveId.New(), "sync", SensorId.None);

        Assert.Equal(SyncSourceKind.None, curve.SourceKind);
        Assert.Empty(curve.ControlDependencies);
        Assert.Null(curve.Evaluate(new TestCurveContext()));
    }

    [Fact]
    public void Mirroring_a_control_reports_the_limited_duty_not_the_curve_behind_it()
    {
        // The whole reason a control source exists. The pump's curve asks for 100%; its binding
        // caps it at 60%; a fan synced to the pump must follow the pump to 60%, not to 100%.
        var registry = new FakeSensorRegistry();
        var pump = registry.Add(new FakeControl("pump"));
        var fan = registry.Add(new FakeControl("fan"));

        var pumpCurve = new FlatCurve(CurveId.New(), "full", Duty.Full);
        var syncCurve = new SyncCurve(CurveId.New(), "follow pump", pump.Id);

        var loop = new ControlLoop(registry, new ControlOwnershipRegistry(TimeProvider.System), TimeProvider.System);
        loop.Configure(
            [pumpCurve, syncCurve],
            [
                new ControlBinding(pump.Id)
                {
                    CurveId = pumpCurve.Id,
                    MaximumDuty = new Duty(60f),
                    MaximumStepUpPerSecond = 0f,
                    MaximumStepDownPerSecond = 0f,
                },
                new ControlBinding(fan.Id)
                {
                    CurveId = syncCurve.Id,
                    MaximumStepUpPerSecond = 0f,
                    MaximumStepDownPerSecond = 0f,
                },
            ]);

        // The first tick has no commanded duty to mirror; the second sees the capped 60%.
        loop.Tick(Tick);
        loop.Tick(Tick);

        Assert.Equal(60f, pump.CommandedDuty!.Value.Percent, precision: 3);
        Assert.Equal(60f, fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void A_sync_curve_that_reaches_its_own_control_is_rejected()
    {
        // Loop detected. The original names the offending control in its error; we at least
        // refuse to configure rather than letting the fan hunt.
        var registry = new FakeSensorRegistry();
        var fan = registry.Add(new FakeControl());

        var syncCurve = new SyncCurve(CurveId.New(), "itself", fan.Id);
        var binding = new ControlBinding(fan.Id) { CurveId = syncCurve.Id };

        var loop = new ControlLoop(registry, new ControlOwnershipRegistry(TimeProvider.System), TimeProvider.System);

        Assert.Throws<ArgumentException>(() => loop.Configure([syncCurve], [binding]));
    }

    [Fact]
    public void A_control_hop_through_a_second_curve_is_still_a_cycle()
    {
        // fan A is driven by a mix of fan B's sync curve, and fan B syncs back to fan A.
        var registry = new FakeSensorRegistry();
        var fanA = registry.Add(new FakeControl("a"));
        var fanB = registry.Add(new FakeControl("b"));

        var followsB = new SyncCurve(CurveId.New(), "follows b", fanB.Id);
        var followsA = new SyncCurve(CurveId.New(), "follows a", fanA.Id);

        var loop = new ControlLoop(registry, new ControlOwnershipRegistry(TimeProvider.System), TimeProvider.System);

        Assert.Throws<ArgumentException>(() => loop.Configure(
            [followsB, followsA],
            [
                new ControlBinding(fanA.Id) { CurveId = followsB.Id },
                new ControlBinding(fanB.Id) { CurveId = followsA.Id },
            ]));
    }

    [Fact]
    public void A_control_nothing_drives_is_reported_but_not_fatal()
    {
        var orphan = SensorId.New();
        var curve = new SyncCurve(CurveId.New(), "sync", orphan);

        var order = CurveGraph.Sort([curve], new Dictionary<SensorId, CurveId>());

        Assert.True(order.IsValid);
        Assert.Equal(orphan, Assert.Single(order.UnboundControls));
    }

    [Fact]
    public void Control_edges_are_ignored_when_no_binding_map_is_supplied()
    {
        // Sorting a curve set on its own — validating an edit before it is bound to anything —
        // must not report every control source as unbound.
        var curve = new SyncCurve(CurveId.New(), "sync", SensorId.New());

        var order = CurveGraph.Sort([curve]);

        Assert.True(order.IsValid);
        Assert.Empty(order.UnboundControls);
    }
}
