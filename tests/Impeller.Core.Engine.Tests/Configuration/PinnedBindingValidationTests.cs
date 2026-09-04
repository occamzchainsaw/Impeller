using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Core.Engine.Configuration;

namespace Impeller.Core.Engine.Tests.Configuration;

/// <summary>
/// Covers a control with no curve behind it, which is a finished state and not a half-made one.
/// </summary>
/// <remarks>
/// <para>
/// Found twice, the same way both times: by watching a correct configuration report itself as a
/// problem. First by importing a real FanControl configuration through the live engine, where the
/// fan the user had set to manual came across correctly and was then warned about; then again after
/// the resting-state work, where three fans with no curve produced three warnings on every single
/// save.
/// </para>
/// <para>
/// A warning that shows up on correct configurations is one people learn to scroll past, which
/// costs the warnings that matter — and that is worse now that the shell shows them as
/// notifications rather than burying them in a bar nobody read.
/// </para>
/// </remarks>
public class PinnedBindingValidationTests
{
    private static (ImpellerConfiguration Configuration, FakeSensorRegistry Registry) Machine(
        Duty? pinned,
        CurveId curve)
    {
        var registry = new FakeSensorRegistry();
        var control = registry.Add(new FakeControl("Rig Cooling"));

        var configuration = new ImpellerConfiguration
        {
            Name = "Imported",
            Controls =
            [
                new ControlBindingDefinition
                {
                    ControlId = control.Id,
                    CurveId = curve,
                    Enabled = true,
                    ManualDuty = pinned,
                },
            ],
        };

        return (configuration, registry);
    }

    [Fact]
    public void A_control_pinned_by_hand_with_no_curve_is_not_a_problem()
    {
        var (configuration, registry) = Machine(Duty.Off, CurveId.None);

        var validation = ConfigurationValidator.Validate(configuration, registry);

        Assert.DoesNotContain(validation.Issues, issue => issue.Code == "binding-without-curve");
        Assert.DoesNotContain(validation.Issues, issue => issue.Code == "missing-binding-curve");
        Assert.False(validation.HasErrors);
    }

    [Fact]
    public void A_pin_of_zero_counts_as_a_pin()
    {
        // The case that prompted this. "Rig Cooling" was deliberately stopped, and zero is a duty
        // like any other — treating it as absent would report the one fan the user had most clearly
        // made a decision about.
        var (configuration, registry) = Machine(Duty.Off, CurveId.None);

        Assert.Empty(ConfigurationValidator.Validate(configuration, registry).Issues);
    }

    [Fact]
    public void A_control_enabled_with_neither_curve_nor_pin_is_not_a_problem_either()
    {
        // This used to be reported, on the reasoning that a fan with nothing driving it was a
        // configuration half-made. It no longer is: an enabled control with no curve rests — handed
        // back to its own firmware where the hardware allows it, driven to its failsafe duty where
        // it does not. See RestingStateTests.
        var (configuration, registry) = Machine(null, CurveId.None);

        Assert.Empty(ConfigurationValidator.Validate(configuration, registry).Issues);
    }

    [Fact]
    public void A_problem_names_the_fan_rather_than_its_identifier()
    {
        // These strings reach the user as notifications now. "Rig Cooling is driven by a curve that
        // no longer exists" is actionable; the same sentence with a GUID in it is not.
        var (configuration, registry) = Machine(null, CurveId.New());

        var issue = Assert.Single(ConfigurationValidator.Validate(configuration, registry).Issues);

        Assert.Contains("Rig Cooling", issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("-", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_control_pointed_at_a_curve_that_is_gone_is_still_reported()
    {
        // The no-curve case must not swallow this one: a binding naming a curve that no longer
        // exists is a real problem whether or not the fan also carries a pin. The difference is
        // that the fan is pointed at something, and that something is missing.
        var (configuration, registry) = Machine(new Duty(40f), CurveId.New());

        var validation = ConfigurationValidator.Validate(configuration, registry);

        Assert.Contains(validation.Issues, issue => issue.Code == "missing-binding-curve");
    }
}
