using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests;

/// <summary>
/// Covers what happens when a control changes hands, and which controls can be claimed at all.
/// </summary>
/// <remarks>
/// <para>
/// Three defects, all in shipped code, all found while planning the plugin channel. A requested
/// duty was written and never removed, so a control acquired by someone new opened at the previous
/// holder's duty. The tick loop skips a disabled binding before it resolves ownership, so a claim
/// on one was granted, its duties accepted, and every one of them discarded without a word. And the
/// claimant check on a duty applied only to plugin-held controls, so anyone could set the duty on a
/// fan the user had pinned by hand.
/// </para>
/// <para>
/// They matter more together than apart: a plugin's acquire and its first duty are two round trips
/// with ticks in between, and disabling a fan so that two programs "do not fight" is exactly what a
/// careful user does.
/// </para>
/// </remarks>
public class ClaimLifecycleTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private const string PluginA = "com.example.a";
    private const string PluginB = "com.example.b";

    /// <summary>A single fan on a flat 50% curve, with ramp limiting off.</summary>
    private sealed class Harness
    {
        public FakeSensorRegistry Registry { get; } = new();

        public ControlOwnershipRegistry Ownership { get; } = new(TimeProvider.System);

        public FakeControl Fan { get; }

        public ControlLoop Loop { get; }

        public FlatCurve Curve { get; }

        public List<ControlOwnershipChange> Changes { get; } = [];

        public Harness()
        {
            Fan = Registry.Add(new FakeControl());
            Curve = new FlatCurve(CurveId.New(), "rest", new Duty(50f));
            Loop = new ControlLoop(Registry, Ownership, TimeProvider.System);

            Ownership.OwnershipChanged += (_, change) => Changes.Add(change);

            Configure();
        }

        public SensorId FanId => Fan.Id;

        /// <summary>Applies a binding, defaulting to one a plugin is allowed to hold.</summary>
        public void Configure(bool enabled = true, bool withCurve = true) =>
            Loop.Configure(
                [Curve],
                [
                    new ControlBinding(Fan.Id)
                    {
                        CurveId = withCurve ? Curve.Id : CurveId.None,
                        Enabled = enabled,
                        MaximumStepUpPerSecond = 0f,
                        MaximumStepDownPerSecond = 0f,
                    },
                ]);

        public float Commanded => Loop.GetCommandedDuty(Fan.Id)!.Value.Percent;

        /// <summary>The reason the last ownership change gave.</summary>
        public OwnershipChangeReason LastReason => Changes[^1].Reason;
    }

    [Fact]
    public void A_control_taken_by_someone_new_does_not_open_at_the_previous_holders_duty()
    {
        // The defect. Without the fix the fan is at 80% the moment B acquires, before B has said
        // anything at all - and it is attributed to B, which has no idea where the number came from.
        var harness = new Harness();

        Assert.True(harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA).Succeeded);
        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(80f), PluginA);
        harness.Loop.Tick(Tick);
        Assert.Equal(80f, harness.Commanded, precision: 3);

        harness.Ownership.Release(harness.FanId, PluginA);
        harness.Loop.Tick(Tick);
        Assert.Equal(50f, harness.Commanded, precision: 3);

        Assert.True(harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginB).Succeeded);
        harness.Loop.Tick(Tick);

        Assert.Equal(50f, harness.Commanded, precision: 3);
    }

    [Fact]
    public void A_control_taken_straight_from_one_holder_by_another_does_not_carry_the_duty_across()
    {
        // No trip through the curve in between, which is the case a cleanup on release would miss
        // if it only ran when a control went back to resting.
        var harness = new Harness();

        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);
        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(80f), PluginA);
        harness.Loop.Tick(Tick);

        harness.Ownership.ForceRelease(harness.FanId, OwnershipChangeReason.Revoked);
        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginB);
        harness.Loop.Tick(Tick);

        Assert.Equal(50f, harness.Commanded, precision: 3);
    }

    [Fact]
    public void A_plugin_that_reconnects_does_not_inherit_the_duty_it_had_before_it_died()
    {
        // The case the stamp alone cannot catch, because the claimant id is the same on both sides
        // of the death. A claim is a session fact; the duty it was holding does not outlive it.
        var harness = new Harness();

        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);
        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(80f), PluginA);
        harness.Loop.Tick(Tick);

        // The process dies: the host force-releases everything it held.
        harness.Ownership.ForceReleaseAllFrom(PluginA, OwnershipChangeReason.Unhealthy);
        harness.Loop.Tick(Tick);
        Assert.Equal(50f, harness.Commanded, precision: 3);

        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);
        harness.Loop.Tick(Tick);

        Assert.Equal(50f, harness.Commanded, precision: 3);
    }

    [Fact]
    public void A_fan_a_plugin_was_driving_is_back_on_its_curve_the_tick_after_the_plugin_goes_away()
    {
        var harness = new Harness();

        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);
        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(90f), PluginA);
        harness.Loop.Tick(Tick);

        Assert.Equal(1, harness.Ownership.ForceReleaseAllFrom(PluginA, OwnershipChangeReason.Unhealthy));
        harness.Loop.Tick(Tick);

        Assert.Equal(50f, harness.Commanded, precision: 3);
    }

    [Fact]
    public void A_duty_from_a_claimant_that_no_longer_holds_the_control_is_not_written()
    {
        // Belt to the cleanup's braces: even with the entry still present, it is unreadable once
        // the control has changed hands.
        var harness = new Harness();

        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.ManualOverride, ControlOwnershipRegistry.ManualClaimant);
        harness.Loop.TrySetRequestedDuty(
            harness.FanId, new Duty(20f), ControlOwnershipRegistry.ManualClaimant);
        harness.Loop.Tick(Tick);
        Assert.Equal(20f, harness.Commanded, precision: 3);

        // A plugin cannot write while the user holds it, and cannot benefit from what the user wrote.
        Assert.False(harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(99f), PluginA));

        harness.Ownership.ForceRelease(harness.FanId, OwnershipChangeReason.Revoked);
        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);
        harness.Loop.Tick(Tick);

        Assert.Equal(50f, harness.Commanded, precision: 3);
    }

    [Fact]
    public void A_plugin_cannot_set_the_duty_on_a_fan_the_user_pinned_by_hand()
    {
        // The claimant check used to run only when a plugin held the control, so a request naming a
        // different claimant was accepted on a user-held one and stored against the owner - which
        // made it indistinguishable from something the user had asked for. Exclusive ownership is
        // the invariant the whole arbitration model rests on, and this was a hole straight through
        // it in the direction nobody was looking.
        var harness = new Harness();

        harness.Loop.TryAcquire(
            harness.FanId, ControlOwnerKind.ManualOverride, ControlOwnershipRegistry.ManualClaimant);
        harness.Loop.TrySetRequestedDuty(
            harness.FanId, new Duty(20f), ControlOwnershipRegistry.ManualClaimant);

        Assert.False(harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(99f), PluginA));

        harness.Loop.Tick(Tick);
        Assert.Equal(20f, harness.Commanded, precision: 3);
    }

    [Fact]
    public void An_unnamed_caller_cannot_set_the_duty_on_a_control_someone_holds()
    {
        var harness = new Harness();
        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);

        Assert.False(harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(99f)));
    }

    [Fact]
    public void A_claimant_that_has_not_said_anything_yet_leaves_the_fan_on_its_curve()
    {
        // Not a hold at whatever was last on the fan. A claimant takes a control before it has
        // computed a duty, and for the gap in between the curve is the only answer that is anyone's
        // actual intent.
        var harness = new Harness();
        harness.Loop.Tick(Tick);

        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);
        harness.Loop.Tick(Tick);

        Assert.Equal(50f, harness.Commanded, precision: 3);
    }

    [Fact]
    public void A_control_with_nothing_behind_it_rests_rather_than_freezing()
    {
        // This used to hold the last duty, on the reasoning that moving a fan to a number nobody
        // chose is worse than leaving it. That was wrong in the other direction: it left the fan at
        // a number nobody chose either - the departed claimant's - for as long as the machine
        // stayed on. A defined resting state is the answer to both. See RestingStateTests.
        var harness = new Harness();
        harness.Configure(withCurve: false);

        harness.Loop.TryAcquire(
            harness.FanId, ControlOwnerKind.ManualOverride, ControlOwnershipRegistry.ManualClaimant);
        harness.Loop.TrySetRequestedDuty(
            harness.FanId, new Duty(35f), ControlOwnershipRegistry.ManualClaimant);
        harness.Loop.Tick(Tick);
        Assert.Equal(35f, harness.Commanded, precision: 3);

        harness.Ownership.ForceRelease(harness.FanId, OwnershipChangeReason.Revoked);
        harness.Loop.Tick(Tick);

        // This fan cannot be handed back to firmware, so it takes the other half of the resting
        // rule: its failsafe duty, which is the value the engine already uses whenever it has to
        // leave a fan somewhere safe without knowing what it should be doing.
        Assert.Equal(100f, harness.Commanded, precision: 3);
    }

    [Fact]
    public void A_plugin_may_claim_a_control_that_is_switched_off()
    {
        // Refused, once, and for a reason that was true of the tick loop rather than of the fan:
        // it skipped a switched-off binding before resolving ownership, so a claim on one meant a
        // plugin whose every write was accepted and discarded. Fixed where it belonged, and the
        // refusal went with it - a user who grants a fan to a program has said what they want, and
        // sending them back to Impeller to switch that fan on is asking twice.
        var harness = new Harness();
        harness.Configure(enabled: false);

        var result = harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);

        Assert.True(result.Succeeded);
        Assert.Equal(ControlOwnerKind.Plugin, harness.Ownership.GetOwner(harness.FanId).Kind);
    }

    [Fact]
    public void A_plugin_may_claim_a_control_with_no_curve()
    {
        // Refused, once. The reasoning was that a curve is what the fan returns to when the plugin
        // lets go, and without one the plugin was the only thing between the fan and a stopped fan.
        // Every enabled control now has a resting state, so the reason is gone - and the rule was
        // inconsistent while it stood, because a person could pin exactly the same fan by hand.
        var harness = new Harness();
        harness.Configure(withCurve: false);

        Assert.True(harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA).Succeeded);
    }

    [Fact]
    public void A_person_may_still_pin_a_control_that_has_no_curve()
    {
        // The weaker rule, on purpose. A pin is written into the configuration and restored on the
        // next start, so a permanent pin with no curve is a configuration the engine supports and a
        // user reasonably wants. Holding a person to the plugin rule would break it.
        var harness = new Harness();
        harness.Configure(withCurve: false);

        var result = harness.Loop.TryAcquire(
            harness.FanId, ControlOwnerKind.ManualOverride, ControlOwnershipRegistry.ManualClaimant);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void A_person_may_pin_a_control_the_engine_is_not_otherwise_driving()
    {
        // Pinning a switched-off fan used to be accepted, saved to the configuration, and never
        // written - so it was refused. It is written now, which makes the refusal the wrong half
        // of the pair to keep: taking a fan by hand is itself the instruction to drive it.
        var harness = new Harness();
        harness.Configure(enabled: false);

        var result = harness.Loop.TryAcquire(
            harness.FanId, ControlOwnerKind.ManualOverride, ControlOwnershipRegistry.ManualClaimant);

        Assert.True(result.Succeeded);

        harness.Loop.TrySetRequestedDuty(
            harness.FanId, new Duty(35f), ControlOwnershipRegistry.ManualClaimant);
        harness.Loop.Tick(Tick);

        Assert.Equal(35f, harness.Commanded, precision: 3);
    }

    [Fact]
    public void A_claim_on_a_control_that_is_not_in_the_configuration_at_all_is_refused()
    {
        var harness = new Harness();

        var result = harness.Loop.TryAcquire(SensorId.New(), ControlOwnerKind.Plugin, PluginA);

        Assert.False(result.Succeeded);
        Assert.Equal(ControlAcquireFailure.UnknownControl, result.Failure);
    }

    [Fact]
    public void A_failsafe_claim_is_taken_regardless_of_what_the_configuration_says()
    {
        // The failsafe runs when the engine has lost its grip. A configuration that would refuse it
        // a control is exactly the configuration whose fans most need driving to safety.
        var harness = new Harness();
        harness.Configure(enabled: false, withCurve: false);

        Assert.True(harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Failsafe).Succeeded);
    }

    [Fact]
    public void A_plugin_keeps_a_fan_a_new_configuration_switches_off()
    {
        // This used to take the fan back, because the tick loop skipped a switched-off binding and
        // a claim it would not honour was worse than no claim. It honours it now, and taking the
        // fan back would be the wrong half to keep: switching a fan off in Impeller so that two
        // programs do not fight over it is exactly what a careful user does before handing it to
        // one of them.
        var harness = new Harness();
        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);
        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(80f), PluginA);

        harness.Configure(enabled: false);
        harness.Loop.Tick(Tick);

        Assert.Equal(ControlOwnerKind.Plugin, harness.Ownership.GetOwner(harness.FanId).Kind);
        Assert.Equal(80f, harness.Commanded, precision: 3);
    }

    [Fact]
    public void A_plugin_holding_a_fan_a_new_configuration_drops_is_taken_off_it_and_told()
    {
        // What is left of the rule, and the one case that genuinely cannot be honoured: with no
        // binding there are no limits, no calibration and no tachometer, and nothing to drive the
        // fan through. Leaving the claim would put the plugin back in the silent-discard state.
        var harness = new Harness();
        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);
        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(80f), PluginA);

        harness.Loop.Configure([harness.Curve], []);

        Assert.Equal(ControlOwnerKind.Curve, harness.Ownership.GetOwner(harness.FanId).Kind);
        Assert.Equal(OwnershipChangeReason.ConfigurationChanged, harness.LastReason);
    }

    [Fact]
    public void A_plugin_keeps_a_fan_whose_curve_is_taken_away()
    {
        // This used to evict the plugin, because a curveless fan was one no plugin could hold. It
        // no longer is: taking the curve away leaves the fan resting when the plugin lets go, which
        // is a defined state, so there is nothing to protect the user from by taking it back.
        var harness = new Harness();
        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);

        harness.Configure(withCurve: false);

        Assert.Equal(ControlOwnerKind.Plugin, harness.Ownership.GetOwner(harness.FanId).Kind);
    }

    [Fact]
    public void A_plugin_keeps_a_fan_a_new_configuration_still_lets_it_hold()
    {
        // Reconfiguring for an unrelated reason must not evict every plugin in the machine.
        var harness = new Harness();
        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);
        harness.Loop.TrySetRequestedDuty(harness.FanId, new Duty(80f), PluginA);

        harness.Configure();
        harness.Loop.Tick(Tick);

        Assert.Equal(ControlOwnerKind.Plugin, harness.Ownership.GetOwner(harness.FanId).Kind);
        Assert.Equal(80f, harness.Commanded, precision: 3);
    }

    [Fact]
    public void A_failsafe_claim_survives_a_configuration_change_that_would_evict_a_plugin()
    {
        var harness = new Harness();
        harness.Loop.EngageFailsafe();
        harness.Loop.ClearFailsafe();

        // Re-take it directly: what matters is that a Failsafe holder is not swept.
        harness.Ownership.TryAcquire(harness.FanId, ControlOwnerKind.Failsafe);
        harness.Configure(enabled: false, withCurve: false);

        Assert.Equal(ControlOwnerKind.Failsafe, harness.Ownership.GetOwner(harness.FanId).Kind);
    }

    [Fact]
    public void Taking_a_control_by_hand_says_so_rather_than_reporting_a_bare_claim()
    {
        // The reasons are what a plugin reacts to. The user taking a fan back is a normal thing to
        // accept; a failsafe is a reason not to ask for it again on a timer.
        var harness = new Harness();

        harness.Loop.TryAcquire(
            harness.FanId, ControlOwnerKind.ManualOverride, ControlOwnershipRegistry.ManualClaimant);

        Assert.Equal(OwnershipChangeReason.TakenByUser, harness.LastReason);

        harness.Loop.EngageFailsafe();
        Assert.Equal(OwnershipChangeReason.Failsafe, harness.LastReason);
    }

    [Fact]
    public void Releasing_a_control_says_it_was_released_rather_than_revoked()
    {
        var harness = new Harness();

        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);
        harness.Ownership.Release(harness.FanId, PluginA);

        Assert.Equal(OwnershipChangeReason.Released, harness.LastReason);
    }

    [Fact]
    public void A_forced_release_carries_the_reason_the_caller_gave_it()
    {
        var harness = new Harness();

        harness.Loop.TryAcquire(harness.FanId, ControlOwnerKind.Plugin, PluginA);
        harness.Ownership.ForceRelease(harness.FanId, OwnershipChangeReason.LeaseExpired);

        Assert.Equal(OwnershipChangeReason.LeaseExpired, harness.LastReason);
    }
}
