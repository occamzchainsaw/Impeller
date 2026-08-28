using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests;

public class ControlLoopTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private const string PluginA = "com.example.a";

    private sealed class Harness
    {
        public FakeSensorRegistry Registry { get; } = new();

        public ControlOwnershipRegistry Ownership { get; } = new(TimeProvider.System);

        public FakeControl Fan { get; private set; } = null!;

        public FakeSensor Temperature { get; private set; } = null!;

        public ControlLoop Loop { get; private set; } = null!;

        public ControlBinding Binding { get; private set; } = null!;

        /// <summary>
        /// A single fan on a linear curve from 40 to 80 degrees, with ramp limiting switched off
        /// so that tests opt into it explicitly.
        /// </summary>
        public static Harness Build(float initialTemperature = 60f, Action<ControlBinding>? configure = null)
        {
            var harness = new Harness();
            harness.Fan = harness.Registry.Add(new FakeControl());
            harness.Temperature = harness.Registry.Add(new FakeSensor { Value = initialTemperature });

            var curve = new LinearCurve(
                CurveId.New(),
                "ramp",
                harness.Temperature.Id,
                minimumInput: 40f,
                maximumInput: 80f,
                minimumDuty: new Duty(0f),
                maximumDuty: new Duty(100f));

            harness.Binding = new ControlBinding(harness.Fan.Id)
            {
                CurveId = curve.Id,
                MaximumStepUpPerSecond = 0f,
                MaximumStepDownPerSecond = 0f,
            };
            configure?.Invoke(harness.Binding);

            harness.Loop = new ControlLoop(harness.Registry, harness.Ownership, TimeProvider.System);
            harness.Loop.Configure([curve], [harness.Binding]);
            return harness;
        }
    }

    [Fact]
    public void A_curve_owned_control_follows_its_curve()
    {
        var h = Harness.Build(initialTemperature: 60f);

        h.Loop.Tick(Tick);

        Assert.Equal(50f, h.Fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Limits_clamp_the_curve_output()
    {
        var h = Harness.Build(initialTemperature: 80f, configure: b => b.MaximumDuty = new Duty(70f));

        h.Loop.Tick(Tick);

        Assert.Equal(70f, h.Fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void A_minimum_duty_keeps_a_fan_from_stopping()
    {
        var h = Harness.Build(initialTemperature: 20f, configure: b => b.MinimumDuty = new Duty(25f));

        h.Loop.Tick(Tick);

        Assert.Equal(25f, h.Fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void A_disabled_binding_is_left_alone_entirely()
    {
        var h = Harness.Build(configure: b => b.Enabled = false);

        h.Loop.Tick(Tick);

        Assert.Empty(h.Fan.Writes);
    }

    [Fact]
    public void The_ramp_limit_caps_how_fast_a_duty_rises()
    {
        var h = Harness.Build(initialTemperature: 40f, configure: b => b.MaximumStepUpPerSecond = 10f);

        h.Loop.Tick(Tick);
        h.Temperature.Value = 80f;

        h.Loop.Tick(Tick);
        Assert.Equal(10f, h.Fan.CommandedDuty!.Value.Percent, precision: 3);

        h.Loop.Tick(Tick);
        Assert.Equal(20f, h.Fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void The_ramp_limit_scales_with_elapsed_time()
    {
        // A tick that arrives late must allow proportionally more movement, not the same step.
        var h = Harness.Build(initialTemperature: 40f, configure: b => b.MaximumStepUpPerSecond = 10f);

        h.Loop.Tick(Tick);
        h.Temperature.Value = 80f;
        h.Loop.Tick(TimeSpan.FromSeconds(4));

        Assert.Equal(40f, h.Fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void A_missing_reading_holds_the_previous_duty_rather_than_guessing()
    {
        var h = Harness.Build(initialTemperature: 60f);
        h.Loop.Tick(Tick);
        var beforeCount = h.Fan.Writes.Count;

        h.Temperature.Value = null;
        h.Loop.Tick(Tick);

        Assert.Equal(beforeCount, h.Fan.Writes.Count);
        Assert.Equal(50f, h.Fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void A_write_that_would_change_nothing_is_skipped()
    {
        var h = Harness.Build(initialTemperature: 60f);

        h.Loop.Tick(Tick);
        h.Loop.Tick(Tick);
        h.Loop.Tick(Tick);

        Assert.Single(h.Fan.Writes);
    }

    [Fact]
    public void A_control_that_throws_is_reported_without_taking_down_the_tick()
    {
        var h = Harness.Build();
        h.Fan.WriteFault = new InvalidOperationException("device gone");

        var result = h.Loop.Tick(Tick);

        Assert.True(result.Faults.ContainsKey(h.Fan.Id));
        Assert.Equal(0, result.ControlsWritten);
    }

    [Fact]
    public void A_curve_that_throws_does_not_take_down_the_tick()
    {
        var registry = new FakeSensorRegistry();
        var fan = registry.Add(new FakeControl());
        var curve = new ThrowingCurve();
        var binding = new ControlBinding(fan.Id) { CurveId = curve.Id };
        var loop = new ControlLoop(registry, new ControlOwnershipRegistry(TimeProvider.System), TimeProvider.System);
        loop.Configure([curve], [binding]);

        var result = loop.Tick(Tick);

        Assert.Null(result.CurveOutputs[curve.Id]);
        Assert.Empty(fan.Writes);
    }

    [Fact]
    public void An_owning_plugin_drives_the_control_instead_of_the_curve()
    {
        var h = Harness.Build(initialTemperature: 60f);
        h.Ownership.TryAcquire(h.Fan.Id, ControlOwnerKind.Plugin, PluginA);

        Assert.True(h.Loop.TrySetRequestedDuty(h.Fan.Id, new Duty(90f), PluginA));
        h.Loop.Tick(Tick);

        Assert.Equal(90f, h.Fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void A_plugin_that_does_not_own_the_control_cannot_set_a_duty()
    {
        var h = Harness.Build();

        Assert.False(h.Loop.TrySetRequestedDuty(h.Fan.Id, new Duty(90f), PluginA));
    }

    [Fact]
    public void A_stale_plugin_cannot_keep_writing_after_being_force_released()
    {
        // The watchdog path: once released, further requests from that plugin are refused.
        var h = Harness.Build();
        h.Ownership.TryAcquire(h.Fan.Id, ControlOwnerKind.Plugin, PluginA);
        h.Ownership.ForceReleaseAllFrom(PluginA);

        Assert.False(h.Loop.TrySetRequestedDuty(h.Fan.Id, new Duty(90f), PluginA));
    }

    [Fact]
    public void Releasing_a_plugin_hands_the_control_back_to_its_curve()
    {
        var h = Harness.Build(initialTemperature: 60f);
        h.Ownership.TryAcquire(h.Fan.Id, ControlOwnerKind.Plugin, PluginA);
        h.Loop.TrySetRequestedDuty(h.Fan.Id, new Duty(90f), PluginA);
        h.Loop.Tick(Tick);

        h.Ownership.Release(h.Fan.Id, PluginA);
        h.Loop.Tick(Tick);

        Assert.Equal(50f, h.Fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void An_owner_that_has_not_set_a_duty_yet_holds_the_previous_value()
    {
        var h = Harness.Build(initialTemperature: 60f);
        h.Loop.Tick(Tick);

        h.Ownership.TryAcquire(h.Fan.Id, ControlOwnerKind.Plugin, PluginA);
        h.Loop.Tick(Tick);

        Assert.Equal(50f, h.Fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void The_failsafe_drives_every_control_to_its_configured_duty()
    {
        var h = Harness.Build(initialTemperature: 60f, configure: b => b.FailsafeDuty = new Duty(100f));
        h.Loop.Tick(Tick);

        h.Loop.EngageFailsafe();

        Assert.Equal(100f, h.Fan.CommandedDuty!.Value.Percent);
        Assert.True(h.Loop.IsFailsafeEngaged);
    }

    [Fact]
    public void The_failsafe_bypasses_the_ramp_limit()
    {
        // Easing up to a safe speed would defeat the purpose of a failsafe.
        var h = Harness.Build(initialTemperature: 40f, configure: b =>
        {
            b.MaximumStepUpPerSecond = 5f;
            b.FailsafeDuty = new Duty(100f);
        });
        h.Loop.Tick(Tick);

        h.Loop.EngageFailsafe();

        Assert.Equal(100f, h.Fan.CommandedDuty!.Value.Percent);
    }

    [Fact]
    public void The_failsafe_overrides_a_plugin_that_holds_the_control()
    {
        var h = Harness.Build(configure: b => b.FailsafeDuty = new Duty(100f));
        h.Ownership.TryAcquire(h.Fan.Id, ControlOwnerKind.Plugin, PluginA);
        h.Loop.TrySetRequestedDuty(h.Fan.Id, new Duty(10f), PluginA);

        h.Loop.EngageFailsafe();

        Assert.Equal(100f, h.Fan.CommandedDuty!.Value.Percent);
        Assert.Equal(ControlOwnerKind.Failsafe, h.Ownership.GetOwner(h.Fan.Id).Kind);
    }

    [Fact]
    public void The_failsafe_prefers_handing_the_device_back_to_its_firmware()
    {
        var h = Harness.Build();
        h.Fan.SupportsAutomaticMode = true;
        h.Fan.AutomaticModeRestoreSucceeds = true;

        h.Loop.EngageFailsafe();

        Assert.Equal(1, h.Fan.AutomaticModeRestoreAttempts);
        Assert.Empty(h.Fan.Writes);
    }

    [Fact]
    public void The_failsafe_falls_back_to_a_duty_write_when_firmware_handoff_is_refused()
    {
        // Which is what happens on most hardware.
        var h = Harness.Build(configure: b => b.FailsafeDuty = new Duty(100f));
        h.Fan.SupportsAutomaticMode = true;
        h.Fan.AutomaticModeRestoreSucceeds = false;

        h.Loop.EngageFailsafe();

        Assert.Equal(1, h.Fan.AutomaticModeRestoreAttempts);
        Assert.Equal(100f, h.Fan.CommandedDuty!.Value.Percent);
    }

    [Fact]
    public void Ticking_while_the_failsafe_is_engaged_changes_nothing()
    {
        var h = Harness.Build(initialTemperature: 60f, configure: b => b.FailsafeDuty = new Duty(100f));
        h.Loop.EngageFailsafe();
        var writeCount = h.Fan.Writes.Count;

        h.Loop.Tick(Tick);

        Assert.Equal(writeCount, h.Fan.Writes.Count);
    }

    [Fact]
    public void No_new_claims_are_granted_while_the_failsafe_is_engaged()
    {
        var h = Harness.Build();
        h.Loop.EngageFailsafe();

        var result = h.Ownership.TryAcquire(h.Fan.Id, ControlOwnerKind.Plugin, PluginA);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Clearing_the_failsafe_returns_controls_to_their_curves()
    {
        var h = Harness.Build(initialTemperature: 60f, configure: b => b.FailsafeDuty = new Duty(100f));
        h.Loop.EngageFailsafe();

        h.Loop.ClearFailsafe();
        h.Loop.Tick(Tick);

        Assert.False(h.Loop.IsFailsafeEngaged);
        Assert.True(h.Ownership.GetOwner(h.Fan.Id).IsCurve);
        Assert.Equal(50f, h.Fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    [Fact]
    public void Configuring_with_a_cycle_is_rejected()
    {
        var registry = new FakeSensorRegistry();
        var a = new MixCurve(CurveId.New(), "a", MixFunction.Maximum, []);
        var b = new MixCurve(CurveId.New(), "b", MixFunction.Maximum, [a.Id]);
        a.SetSources([b.Id]);

        var loop = new ControlLoop(registry, new ControlOwnershipRegistry(TimeProvider.System), TimeProvider.System);

        Assert.Throws<ArgumentException>(() => loop.Configure([a, b], []));
    }

    [Fact]
    public void Composite_curves_see_this_ticks_values_from_their_inputs()
    {
        var registry = new FakeSensorRegistry();
        var fan = registry.Add(new FakeControl());
        var cpu = registry.Add(new FakeSensor { Value = 70f });
        var gpu = registry.Add(new FakeSensor { Value = 50f });

        var cpuCurve = new LinearCurve(CurveId.New(), "cpu", cpu.Id, 40f, 80f, new Duty(0f), new Duty(100f));
        var gpuCurve = new LinearCurve(CurveId.New(), "gpu", gpu.Id, 40f, 80f, new Duty(0f), new Duty(100f));
        var mix = new MixCurve(CurveId.New(), "mix", MixFunction.Maximum, [cpuCurve.Id, gpuCurve.Id]);

        var binding = new ControlBinding(fan.Id)
        {
            CurveId = mix.Id,
            MaximumStepUpPerSecond = 0f,
            MaximumStepDownPerSecond = 0f,
        };

        var loop = new ControlLoop(registry, new ControlOwnershipRegistry(TimeProvider.System), TimeProvider.System);
        loop.Configure([mix, cpuCurve, gpuCurve], [binding]);

        loop.Tick(Tick);

        // CPU at 70 of 40..80 is 75%; GPU at 50 is 25%. The max wins.
        Assert.Equal(75f, fan.CommandedDuty!.Value.Percent, precision: 3);
    }

    /// <summary>A curve that always throws, for verifying the tick isolates curve faults.</summary>
    private sealed class ThrowingCurve() : FanCurveBase(CurveId.New(), "throws")
    {
        public override Duty? Evaluate(ICurveEvaluationContext context) =>
            throw new InvalidOperationException("curve fault");
    }
}
