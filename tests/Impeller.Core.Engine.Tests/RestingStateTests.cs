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
        public Harness(bool withCurve = false, bool supportsAutomatic = true, bool enabled = true)
        {
            Fan = Registry.Add(new FakeControl("System Fan #3") { SupportsAutomaticMode = supportsAutomatic });
            Curve = new FlatCurve(CurveId.New(), "rest", new Duty(50f));
            Loop = new ControlLoop(Registry, Ownership, TimeProvider.System);

            Configure(withCurve, enabled);
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
    public void A_fan_that_was_never_driven_is_left_alone_entirely()
    {
        // Disabled means Impeller does not touch it, which includes not handing it anywhere.
        var harness = new Harness(enabled: false);

        harness.Loop.Tick(Tick);

        Assert.Equal(0, harness.Fan.AutomaticModeRestoreAttempts);
        Assert.Empty(harness.Fan.Writes);
    }

    [Fact]
    public void Switching_a_fan_off_hands_it_back_rather_than_leaving_it_where_it_was()
    {
        // The last way left to freeze a fan. The tick loop skips a disabled binding before it
        // reaches the resting logic - correctly, because disabled has to mean the engine does not
        // touch this control - so a fan pinned at 20 % and then switched off stayed at 20 % for as
        // long as the machine was on, with nothing managing it and no reason to look. The handover
        // has to happen on the way out, while the engine still considers the control its own.
        var harness = new Harness(withCurve: true);

        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.ManualOverride, "shell");
        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(20f), "shell");
        harness.Loop.Tick(Tick);

        Assert.Equal(20f, harness.Fan.CommandedDuty!.Value.Percent, precision: 3);

        harness.Configure(withCurve: true, enabled: false);

        Assert.Equal(1, harness.Fan.AutomaticModeRestoreAttempts);
    }

    [Fact]
    public void A_fan_that_cannot_be_handed_back_gets_its_failsafe_duty_when_switched_off()
    {
        var harness = new Harness(withCurve: true, supportsAutomatic: false);

        harness.Loop.Tick(Tick);
        harness.Fan.Writes.Clear();

        harness.Configure(withCurve: true, enabled: false);

        Assert.Equal(80f, Assert.Single(harness.Fan.Writes).Percent, precision: 3);
    }

    [Fact]
    public void Switching_a_fan_off_twice_hands_it_back_once()
    {
        // Every configuration change runs this, and a fan that has already been let go of must not
        // be written again each time something unrelated is saved.
        var harness = new Harness(withCurve: true);

        harness.Loop.Tick(Tick);
        harness.Configure(withCurve: true, enabled: false);
        harness.Configure(withCurve: true, enabled: false);

        Assert.Equal(1, harness.Fan.AutomaticModeRestoreAttempts);
    }

    // ---- what that unlocks ------------------------------------------------------------------

    [Fact]
    public void A_plugin_may_now_hold_a_fan_that_has_no_curve()
    {
        // The rule this replaces refused exactly this, and the fix was to give the fan a curve -
        // which until the dashboard rework there was no way to do anywhere in the UI.
        var harness = new Harness();

        Assert.True(harness.Loop.CanBeClaimed(harness.FanId));
        Assert.True(harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, Plugin).Succeeded);
    }

    [Fact]
    public void A_person_may_hold_a_fan_that_has_no_curve()
    {
        // The shell required a curve before it would let anyone drive a fan by hand. The engine
        // never did, and the rule made no sense: a pin is a complete instruction, and having to
        // invent a curve you do not want in order to ignore it is absurd.
        var harness = new Harness();

        Assert.True(harness.Loop.IsDriven(harness.FanId));
        Assert.True(harness.Loop
            .TryAcquire(harness.FanId, ControlOwnerKind.ManualOverride, "shell")
            .Succeeded);

        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(35f), "shell");
        harness.Loop.Tick(Tick);

        Assert.Equal(35f, harness.Fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void A_plugin_may_hold_a_fan_that_is_switched_off_and_the_fan_actually_turns()
    {
        // The rule that replaced the curve rule, and it was the same mistake: switching a fan off
        // meant the tick loop skipped it before it looked at ownership, so the claim had to be
        // refused or the plugin's duties would vanish. Refusing it told a user who had granted a
        // fan to a program to go and switch that fan on in Impeller - which is the configuring a
        // plugin exists to save them from, and which they had every reason to think they had done.
        //
        // Asserted on the hardware rather than on the claim, because a claim that succeeds and is
        // then discarded by the tick is the exact failure the old rule was papering over.
        var harness = new Harness();
        harness.Configure(withCurve: false, enabled: false);

        Assert.True(harness.Loop.CanBeClaimed(harness.FanId));
        Assert.True(harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, Plugin).Succeeded);

        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(42f), Plugin);
        harness.Loop.Tick(Tick);

        Assert.Equal(42f, harness.Fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void A_switched_off_fan_the_engine_never_drove_stays_untouched_tick_after_tick()
    {
        // The guard rail on the change above. The tick loop looks at switched-off bindings now,
        // where it used to skip them outright, so the promise that nothing reaches the hardware
        // until the user asks is no longer structural and has to be asserted. Handing a fan back
        // to firmware is a write like any other, and a fan nobody has ever taken does not need it.
        var harness = new Harness(enabled: false);

        for (var tick = 0; tick < 10; tick++)
        {
            harness.Loop.Tick(Tick);
        }

        Assert.Equal(0, harness.Fan.AutomaticModeRestoreAttempts);
        Assert.Empty(harness.Fan.Writes);
    }

    [Fact]
    public void A_switched_off_fan_a_plugin_lets_go_of_still_rests()
    {
        // Letting go of a switched-off fan has to land in the same place as letting go of a
        // curveless one. The record of the rest is cleared while the plugin drives it, so this is
        // the case that would freeze the fan if that record were merely never written.
        var harness = new Harness();
        harness.Configure(withCurve: false, enabled: false);

        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, Plugin);
        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(42f), Plugin);
        harness.Loop.Tick(Tick);

        Assert.Equal(42f, harness.Fan.CommandedDuty!.Value.Percent, precision: 3);

        harness.Ownership.Release(harness.FanId, Plugin);
        harness.Loop.Tick(Tick);

        Assert.Equal(1, harness.Fan.AutomaticModeRestoreAttempts);
    }

    [Fact]
    public void Nothing_may_hold_a_fan_that_is_not_in_the_configuration()
    {
        // What is left of the rule. Not a policy - there is no binding, so there are no limits, no
        // calibration and no paired tachometer, and nothing to drive the fan through.
        var harness = new Harness();
        harness.Loop.Configure([harness.Curve], []);

        Assert.False(harness.Loop.CanBeClaimed(harness.FanId));

        Assert.Equal(
            ControlAcquireFailure.NotDriven,
            harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, Plugin).Failure);
    }

    [Fact]
    public void A_fan_taken_off_the_page_while_a_plugin_drove_it_is_handed_back()
    {
        // Removing a fan from the dashboard is now the way to reach the hazard that switching one
        // off used to reach: the tick loop only walks the bindings it has, so without the handover
        // on the way out the fan would stay at the plugin's last duty for the life of the machine.
        var harness = new Harness();

        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, Plugin);
        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(35f), Plugin);
        harness.Loop.Tick(Tick);

        Assert.Equal(35f, harness.Fan.CommandedDuty!.Value.Percent, precision: 3);

        harness.Loop.Configure([harness.Curve], []);

        Assert.Equal(1, harness.Fan.AutomaticModeRestoreAttempts);
        Assert.Equal(ControlOwnerKind.Curve, harness.Ownership.GetOwner(harness.FanId).Kind);
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
