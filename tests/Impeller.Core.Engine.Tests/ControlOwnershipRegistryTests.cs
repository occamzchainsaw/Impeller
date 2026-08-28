using Impeller.Core.Abstractions;

namespace Impeller.Core.Engine.Tests;

public class ControlOwnershipRegistryTests
{
    private static readonly SensorId Fan = SensorId.New();
    private const string PluginA = "com.example.a";
    private const string PluginB = "com.example.b";

    private static ControlOwnershipRegistry Build() => new(TimeProvider.System);

    [Fact]
    public void An_unclaimed_control_rests_with_its_curve()
    {
        var owner = Build().GetOwner(Fan);

        Assert.True(owner.IsCurve);
        Assert.True(owner.IsAvailableForClaim);
    }

    [Fact]
    public void A_plugin_can_take_an_unclaimed_control()
    {
        var registry = Build();

        var result = registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA);

        Assert.True(result.Succeeded);
        Assert.Equal(ControlOwnerKind.Plugin, registry.GetOwner(Fan).Kind);
        Assert.Equal(PluginA, registry.GetOwner(Fan).ClaimantId);
    }

    [Fact]
    public void A_second_plugin_is_refused_and_told_who_holds_it()
    {
        // The core exclusivity invariant: claims are refused, never queued or overridden.
        var registry = Build();
        registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA);

        var result = registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginB);

        Assert.False(result.Succeeded);
        Assert.Equal(ControlAcquireFailure.AlreadyOwned, result.Failure);
        Assert.Equal(PluginA, result.CurrentOwner!.Value.ClaimantId);
        Assert.Equal(PluginA, registry.GetOwner(Fan).ClaimantId);
    }

    [Fact]
    public void A_manual_override_is_also_refused_while_a_plugin_holds_the_control()
    {
        var registry = Build();
        registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA);

        var result = registry.TryAcquire(Fan, ControlOwnerKind.ManualOverride);

        Assert.False(result.Succeeded);
        Assert.Equal(ControlAcquireFailure.AlreadyOwned, result.Failure);
    }

    [Fact]
    public void Releasing_returns_the_control_to_its_curve()
    {
        var registry = Build();
        registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA);

        Assert.True(registry.Release(Fan, PluginA));
        Assert.True(registry.GetOwner(Fan).IsCurve);
    }

    [Fact]
    public void A_plugin_cannot_release_a_control_another_plugin_holds()
    {
        var registry = Build();
        registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA);

        Assert.False(registry.Release(Fan, PluginB));
        Assert.Equal(PluginA, registry.GetOwner(Fan).ClaimantId);
    }

    [Fact]
    public void Releasing_an_unclaimed_control_reports_that_nothing_happened()
    {
        Assert.False(Build().Release(Fan, PluginA));
    }

    [Fact]
    public void A_released_control_can_be_claimed_by_someone_else()
    {
        var registry = Build();
        registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA);
        registry.Release(Fan, PluginA);

        Assert.True(registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginB).Succeeded);
    }

    [Fact]
    public void The_failsafe_path_outranks_an_existing_claim()
    {
        // A hung plugin must not be able to keep a fan parked when the engine is recovering.
        var registry = Build();
        registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA);

        Assert.True(registry.TryAcquire(Fan, ControlOwnerKind.Failsafe).Succeeded);
        Assert.Equal(ControlOwnerKind.Failsafe, registry.GetOwner(Fan).Kind);
    }

    [Fact]
    public void ForceRelease_frees_a_control_regardless_of_who_holds_it()
    {
        var registry = Build();
        registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA);

        Assert.True(registry.ForceRelease(Fan));
        Assert.True(registry.GetOwner(Fan).IsCurve);
    }

    [Fact]
    public void ForceReleaseAllFrom_frees_every_control_one_plugin_holds()
    {
        // What the plugin watchdog calls when a heartbeat goes unanswered.
        var registry = Build();
        var second = SensorId.New();
        var untouched = SensorId.New();

        registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA);
        registry.TryAcquire(second, ControlOwnerKind.Plugin, PluginA);
        registry.TryAcquire(untouched, ControlOwnerKind.Plugin, PluginB);

        Assert.Equal(2, registry.ForceReleaseAllFrom(PluginA));
        Assert.True(registry.GetOwner(Fan).IsCurve);
        Assert.True(registry.GetOwner(second).IsCurve);
        Assert.Equal(PluginB, registry.GetOwner(untouched).ClaimantId);
    }

    [Fact]
    public void Suspending_grants_refuses_new_claims_but_still_allows_the_failsafe()
    {
        var registry = Build();
        registry.SuspendGrants();

        var refused = registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA);
        Assert.False(refused.Succeeded);
        Assert.Equal(ControlAcquireFailure.EngineUnavailable, refused.Failure);

        Assert.True(registry.TryAcquire(Fan, ControlOwnerKind.Failsafe).Succeeded);
    }

    [Fact]
    public void Resuming_grants_allows_claims_again()
    {
        var registry = Build();
        registry.SuspendGrants();
        registry.ResumeGrants();

        Assert.True(registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA).Succeeded);
    }

    [Fact]
    public void Acquiring_as_the_curve_is_rejected_as_a_programming_error()
    {
        // Otherwise a caller could evict an owner just by claiming to be the curve.
        var registry = Build();

        Assert.Throws<ArgumentException>(() => registry.TryAcquire(Fan, ControlOwnerKind.Curve));
    }

    [Fact]
    public void ActiveClaims_lists_only_controls_that_are_actually_held()
    {
        var registry = Build();
        var other = SensorId.New();
        registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA);
        registry.TryAcquire(other, ControlOwnerKind.Plugin, PluginB);
        registry.Release(other, PluginB);

        var claims = registry.ActiveClaims;

        Assert.Single(claims);
        Assert.True(claims.ContainsKey(Fan));
    }

    [Fact]
    public void Ownership_changes_are_announced()
    {
        var registry = Build();
        var events = new List<ControlOwnershipChange>();
        registry.OwnershipChanged += (_, e) => events.Add(e);

        registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA);
        registry.Release(Fan, PluginA);

        Assert.Equal(2, events.Count);
        Assert.Equal(ControlOwnerKind.Plugin, events[0].Current.Kind);
        Assert.True(events[1].Current.IsCurve);
    }

    [Fact]
    public void A_refused_claim_announces_nothing()
    {
        var registry = Build();
        registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginA);

        var events = new List<ControlOwnershipChange>();
        registry.OwnershipChanged += (_, e) => events.Add(e);

        registry.TryAcquire(Fan, ControlOwnerKind.Plugin, PluginB);

        Assert.Empty(events);
    }
}
