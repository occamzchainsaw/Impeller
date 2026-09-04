using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests;

/// <summary>
/// Covers what a fan does when nothing is driving it.
/// </summary>
/// <remarks>
/// <para>
/// The tick loop used to hold the last commanded duty when a control had no curve, which left a fan
/// parked at whatever number some claimant chose before it exited — permanently, with nothing
/// managing it and no way to tell that had happened. That was also the reason a plugin was refused
/// a curveless fan, a rule that was inconsistent (a user could pin one by hand) and undiscoverable
/// (the fix was greyed out with no way to reach it).
/// </para>
/// <para>
/// Giving every enabled control a defined resting state fixes the hazard and removes the rule.
/// </para>
/// </remarks>
public class RestingStateTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private const string Plugin = "com.example.rigfan";

    /// <summary>One fan, optionally with a curve, optionally able to hand back to firmware.</summary>
    private sealed class Harness
    {
        public Harness(bool withCurve = false, bool supportsAutomatic = true)
        {
            Fan = Registry.Add(new FakeControl("System Fan #3") { SupportsAutomaticMode = supportsAutomatic });
            Curve = new FlatCurve(CurveId.New(), "rest", new Duty(50f));
            Loop = new ControlLoop(Registry, Ownership, TimeProvider.System);

            Configure(withCurve);
        }

        public FakeSensorRegistry Registry { get; } = new();

        public ControlOwnershipRegistry Ownership { get; } = new(TimeProvider.System);

        public FakeControl Fan { get; }

        public FlatCurve Curve { get; }

        public ControlLoop Loop { get; }

        public SensorId FanId => Fan.Id;

        public void Configure(bool withCurve, bool enabled = true) =>
            Loop.Configure(
                [Curve],
                [
                    new ControlBinding(Fan.Id)
                    {
                        CurveId = withCurve ? Curve.Id : CurveId.None,
                        Enabled = enabled,
                        FailsafeDuty = new Duty(80f),
                        MaximumStepUpPerSecond = 0f,
                        MaximumStepDownPerSecond = 0f,
                    },
                ]);
    }

    [Fact]
    public void A_fan_with_no_curve_is_handed_back_to_its_own_firmware()
    {
        // The honest answer: Impeller is not driving this fan, so whatever was driving it before
        // Impeller existed should have it back.
        var harness = new Harness();

        harness.Loop.Tick(Tick);

        Assert.Equal(1, harness.Fan.AutomaticModeRestoreAttempts);
        Assert.Empty(harness.Fan.Writes);
    }

    [Fact]
    public void A_fan_handed_back_reports_no_commanded_duty()
    {
        // The firmware has it, so the engine does not know what is standing at it — and a stale
        // number would be worse than none.
        var harness = new Harness();

        harness.Loop.Tick(Tick);

        Assert.Null(harness.Loop.GetCommandedDuty(harness.FanId));
    }

    [Fact]
    public void It_is_handed_back_once_rather_than_every_tick()
    {
        // Some Super I/O chips are slow to write, and hardware that answers a restore with a fresh
        // default would be fought once a second forever.
        var harness = new Harness();

        for (var tick = 0; tick < 10; tick++)
        {
            harness.Loop.Tick(Tick);
        }

        Assert.Equal(1, harness.Fan.AutomaticModeRestoreAttempts);
    }

    [Fact]
    public void A_fan_that_cannot_be_handed_back_gets_its_failsafe_duty_instead()
    {
        var harness = new Harness(supportsAutomatic: false);

        harness.Loop.Tick(Tick);

        Assert.Equal(80f, Assert.Single(harness.Fan.Writes).Percent, precision: 3);
    }

    [Fact]
    public void A_fan_whose_curve_says_nothing_holds_rather_than_resting()
    {
        // Deliberately different. A sensor that stopped reporting for one tick is not a reason to
        // move a fan, and a curve that will speak again next tick is not the same as no curve.
        var harness = new Harness(withCurve: true);
        var quiet = new SilentCurve(harness.Curve.Id);

        harness.Loop.Tick(Tick);
        Assert.Equal(50f, harness.Loop.GetCommandedDuty(harness.FanId)!.Value.Percent, precision: 3);

        harness.Loop.Configure([quiet], harness.Loop.Bindings.Values);
        harness.Loop.Tick(Tick);

        Assert.Equal(50f, harness.Loop.GetCommandedDuty(harness.FanId)!.Value.Percent, precision: 3);
        Assert.Equal(0, harness.Fan.AutomaticModeRestoreAttempts);
    }

    [Fact]
    public void A_disabled_fan_is_left_alone_entirely()
    {
        // Disabled means Impeller does not touch it, which includes not handing it anywhere.
        var harness = new Harness();
        harness.Configure(withCurve: false, enabled: false);

        harness.Loop.Tick(Tick);

        Assert.Equal(0, harness.Fan.AutomaticModeRestoreAttempts);
        Assert.Empty(harness.Fan.Writes);
    }

    // ---- what that unlocks ------------------------------------------------------------------

    [Fact]
    public void A_plugin_may_now_hold_a_fan_that_has_no_curve()
    {
        // The rule this replaces refused exactly this, and the fix was to give the fan a curve -
        // which until the dashboard rework there was no way to do anywhere in the UI.
        var harness = new Harness();

        Assert.True(harness.Loop.CanBeHeldByPlugin(harness.FanId));
        Assert.True(harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, Plugin).Succeeded);
    }

    [Fact]
    public void A_plugin_still_cannot_hold_a_fan_the_engine_is_not_driving()
    {
        var harness = new Harness();
        harness.Configure(withCurve: false, enabled: false);

        Assert.False(harness.Loop.CanBeHeldByPlugin(harness.FanId));

        Assert.Equal(
            ControlAcquireFailure.NotDriven,
            harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, Plugin).Failure);
    }

    [Fact]
    public void A_curveless_fan_a_plugin_lets_go_of_rests_rather_than_freezing()
    {
        // The whole point. Before this, releasing left the fan at 35% for as long as the machine
        // stayed on, and the user had no reason to look.
        var harness = new Harness();

        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, Plugin);
        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(35f), Plugin);
        harness.Loop.Tick(Tick);

        Assert.Equal(35f, harness.Loop.GetCommandedDuty(harness.FanId)!.Value.Percent, precision: 3);

        harness.Ownership.Release(harness.FanId, Plugin);
        harness.Loop.Tick(Tick);

        Assert.Equal(1, harness.Fan.AutomaticModeRestoreAttempts);
        Assert.Null(harness.Loop.GetCommandedDuty(harness.FanId));
    }

    [Fact]
    public void Taking_a_rested_fan_again_drives_it_again()
    {
        // Resting is not a latch. A control that was handed back has to come straight back under
        // engine control the moment somebody claims it.
        var harness = new Harness();

        harness.Loop.Tick(Tick);
        Assert.Equal(1, harness.Fan.AutomaticModeRestoreAttempts);

        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.ManualOverride, "shell");
        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(60f), "shell");
        harness.Loop.Tick(Tick);

        Assert.Equal(60f, harness.Loop.GetCommandedDuty(harness.FanId)!.Value.Percent, precision: 3);

        harness.Ownership.Release(harness.FanId, "shell");
        harness.Loop.Tick(Tick);

        Assert.Equal(2, harness.Fan.AutomaticModeRestoreAttempts);
    }

    /// <summary>A curve that never has an answer, for the tick where a sensor is not reporting.</summary>
    private sealed class SilentCurve(CurveId id) : IFanCurve
    {
        public CurveId Id { get; } = id;

        public string Name => "silent";

        public IReadOnlyCollection<SensorId> SensorDependencies => [];

        public IReadOnlyCollection<CurveId> CurveDependencies => [];

        public IReadOnlyCollection<SensorId> ControlDependencies => [];

        public Duty? Evaluate(ICurveEvaluationContext context) => null;

        public void Reset()
        {
        }
    }
}
