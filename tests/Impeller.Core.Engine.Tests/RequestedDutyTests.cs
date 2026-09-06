using Impeller.Core.Abstractions;
using Impeller.Core.Engine.Curves;

namespace Impeller.Core.Engine.Tests;

/// <summary>
/// What the engine publishes about the gap between a request and a command.
/// </summary>
/// <remarks>
/// This exists because of a real report: an Auto curve on a mixed CPU/GPU sensor reading 47 °C,
/// asking for 30%, on a fan measured during calibration to stall below 40%. The engine did the
/// right thing and wrote 0%, and the dashboard showed a bare "0%" — indistinguishable from a curve
/// that had stopped working. The engine knew both numbers and published only one of them.
/// </remarks>
public sealed class RequestedDutyTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// <summary>A fan on a plain ramp, with a stall floor like the one in the report.</summary>
    private static (ControlLoop Loop, FakeControl Fan, FakeSensor Temperature) Rig(
        float temperature,
        float stopDuty = 0f,
        float startDuty = 0f)
    {
        var registry = new FakeSensorRegistry();
        var fan = registry.Add(new FakeControl());
        var sensor = registry.Add(new FakeSensor { Value = temperature });

        var curve = new LinearCurve(
            CurveId.New(),
            "ramp",
            sensor.Id,
            minimumInput: 35f,
            maximumInput: 75f,
            minimumDuty: new Duty(0f),
            maximumDuty: new Duty(100f));

        var binding = new ControlBinding(fan.Id)
        {
            CurveId = curve.Id,
            MaximumStepUpPerSecond = 0f,
            MaximumStepDownPerSecond = 0f,
            StartDuty = new Duty(startDuty),
            StopDuty = new Duty(stopDuty),
        };

        var loop = new ControlLoop(registry, new ControlOwnershipRegistry(TimeProvider.System), TimeProvider.System);
        loop.Configure([curve], [binding]);

        return (loop, fan, sensor);
    }

    /// <summary>
    /// The reported case, reproduced: the curve wants 30% and the fan is held off.
    /// </summary>
    /// <remarks>
    /// 47 °C on a 35–75 °C ramp across 0–100% is 30%. A fan that stalls below 40% cannot be run
    /// there, so the gate writes nothing — and the whole point of this test is that the 30% is
    /// still reported rather than lost.
    /// </remarks>
    [Fact]
    public void A_fan_held_off_by_its_stall_floor_still_reports_what_the_curve_asked_for()
    {
        var (loop, fan, _) = Rig(temperature: 47f, stopDuty: 40f, startDuty: 42f);

        loop.Tick(Tick);

        Assert.Equal(0f, loop.GetCommandedDuty(fan.Id)!.Value.Percent, 1);
        Assert.Equal(30f, loop.GetTargetDuty(fan.Id)!.Value.Percent, 1);
    }

    /// <summary>
    /// Above the stall floor the two agree, so a card comparing them says nothing.
    /// </summary>
    /// <remarks>
    /// 52 °C is 42.5% on the same ramp, which clears a 40% floor. Worth pinning alongside the case
    /// above: a difference that never closes would make the explanation permanent furniture.
    /// </remarks>
    [Fact]
    public void Above_the_stall_floor_the_request_and_the_command_agree()
    {
        var (loop, fan, _) = Rig(temperature: 52f, stopDuty: 40f, startDuty: 42f);

        loop.Tick(Tick);

        Assert.Equal(42.5f, loop.GetTargetDuty(fan.Id)!.Value.Percent, 1);
        Assert.Equal(42.5f, loop.GetCommandedDuty(fan.Id)!.Value.Percent, 1);
    }

    [Fact]
    public void A_fan_with_no_stall_floor_reports_the_same_number_twice()
    {
        var (loop, fan, _) = Rig(temperature: 47f);

        loop.Tick(Tick);

        Assert.Equal(30f, loop.GetTargetDuty(fan.Id)!.Value.Percent, 1);
        Assert.Equal(30f, loop.GetCommandedDuty(fan.Id)!.Value.Percent, 1);
    }

    /// <summary>
    /// A switched-off fan is nobody asking for anything.
    /// </summary>
    /// <remarks>
    /// The stale-target failure would be quiet and wrong in the most confusing way available: a
    /// card claiming a curve wants 30% for a fan the user has deliberately switched off.
    /// </remarks>
    [Fact]
    public void A_switched_off_fan_reports_no_request_at_all()
    {
        var registry = new FakeSensorRegistry();
        var fan = registry.Add(new FakeControl());
        var sensor = registry.Add(new FakeSensor { Value = 47f });

        var curve = new LinearCurve(
            CurveId.New(), "ramp", sensor.Id, 35f, 75f, new Duty(0f), new Duty(100f));

        var loop = new ControlLoop(registry, new ControlOwnershipRegistry(TimeProvider.System), TimeProvider.System);
        var binding = new ControlBinding(fan.Id) { CurveId = curve.Id };

        loop.Configure([curve], [binding]);
        loop.Tick(Tick);

        Assert.NotNull(loop.GetTargetDuty(fan.Id));

        loop.Configure([curve], [new ControlBinding(fan.Id) { CurveId = curve.Id, Enabled = false }]);
        loop.Tick(Tick);

        Assert.Null(loop.GetTargetDuty(fan.Id));
    }

    /// <summary>The tick result carries it too, which is what reaches the shell every second.</summary>
    [Fact]
    public void The_tick_result_carries_the_target_as_well_as_the_command()
    {
        var (loop, fan, _) = Rig(temperature: 47f, stopDuty: 40f, startDuty: 42f);

        var result = loop.Tick(Tick);

        Assert.Equal(30f, result.TargetDuties[fan.Id].Percent, 1);
        Assert.Equal(0f, result.CommandedDuties[fan.Id].Percent, 1);
    }
}
