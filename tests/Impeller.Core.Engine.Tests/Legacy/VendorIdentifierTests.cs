using Impeller.Core.Abstractions;
using Impeller.Core.Persistence.Legacy;

namespace Impeller.Core.Engine.Tests.Legacy;

/// <summary>
/// Covers repairing a graphics-card reference by matching the card rather than the path.
/// </summary>
/// <remarks>
/// The sensor names here are the real ones, taken from the engine running as LocalSystem on the
/// machine whose configuration is the import fixture. A vendor alias that works against invented
/// names and not against those would be worth nothing.
/// </remarks>
public class VendorIdentifierTests
{
    private static FakeSensorRegistry Machine()
    {
        var registry = new FakeSensorRegistry();

        registry.Add(new FakeSensor(SensorKind.Temperature, "AMD Radeon RX 7800 XT - GPU Core") { Value = 36f });
        registry.Add(new FakeSensor(SensorKind.Temperature, "AMD Radeon RX 7800 XT - GPU Memory") { Value = 36f });
        registry.Add(new FakeSensor(SensorKind.Temperature, "AMD Radeon RX 7800 XT - GPU Hot Spot") { Value = 42f });
        registry.Add(new FakeSensor(SensorKind.FanSpeed, "AMD Radeon RX 7800 XT - GPU Fan") { Value = 0f });
        registry.Add(new FakeControl("AMD Radeon RX 7800 XT - GPU Fan"));

        // Something else entirely, so a match has to be earned rather than being the only option.
        registry.Add(new FakeSensor(SensorKind.Temperature, "AMD Ryzen 7 9800X3D - Core (Tctl/Tdie)") { Value = 45f });
        registry.Add(new FakeControl("Nuvoton NCT6687D - CPU Fan"));

        return registry;
    }

    [Theory]
    [InlineData("ADLX/AMD Radeon RX 7800 XT/768/control", VendorRole.Control, null)]
    [InlineData("ADLX/AMD Radeon RX 7800 XT/768/fan", VendorRole.FanSpeed, null)]
    [InlineData("ADLX/AMD Radeon RX 7800 XT/768/temp/GPU", VendorRole.Temperature, "GPU")]
    [InlineData("ADLX/AMD Radeon RX 7800 XT/768/temp/Hotspot", VendorRole.Temperature, "Hotspot")]
    public void An_amd_identifier_is_taken_apart_into_card_and_role(
        string identifier,
        VendorRole role,
        string? detail)
    {
        Assert.True(VendorIdentifier.TryParse(identifier, out var reference));

        Assert.Equal("ADLX", reference.Vendor);
        Assert.Equal("AMD Radeon RX 7800 XT", reference.Device);
        Assert.Equal(role, reference.Role);
        Assert.Equal(detail, reference.Detail);
    }

    [Theory]
    [InlineData("NVApiWrapper/0-NVIDIA GeForce RTX 4080/control/0", VendorRole.Control)]
    [InlineData("NVApiWrapper/0-NVIDIA GeForce RTX 4080/fan/0", VendorRole.FanSpeed)]
    [InlineData("NVApiWrapper/0-NVIDIA GeForce RTX 4080/sensor/0", VendorRole.Temperature)]
    public void An_nvidia_identifier_drops_the_enumeration_index_from_the_card_name(
        string identifier,
        VendorRole role)
    {
        Assert.True(VendorIdentifier.TryParse(identifier, out var reference));

        // The leading index is the part that moves when a driver update reorders the cards.
        Assert.Equal("NVIDIA GeForce RTX 4080", reference.Device);
        Assert.Equal(role, reference.Role);
    }

    [Theory]
    [InlineData("/lpc/nct6687d/control/0")]
    [InlineData("Mix/CPU GPU Mix")]
    [InlineData("ADLX/card/768/nonsense")]
    [InlineData("")]
    public void Anything_that_is_not_a_vendor_reference_is_left_alone(string identifier)
    {
        Assert.False(VendorIdentifier.TryParse(identifier, out _));
    }

    [Fact]
    public void A_card_referenced_by_name_resolves_to_the_control_that_is_actually_here()
    {
        var registry = Machine();
        VendorIdentifier.TryParse("ADLX/AMD Radeon RX 7800 XT/768/control", out var reference);

        var sensor = VendorIdentifier.Resolve(reference, registry, out var ambiguous);

        Assert.NotNull(sensor);
        Assert.Equal("AMD Radeon RX 7800 XT - GPU Fan", sensor.Name);
        Assert.Equal(SensorKind.Control, sensor.Kind);
        Assert.False(ambiguous);
    }

    [Fact]
    public void The_tach_and_the_control_are_told_apart_despite_sharing_a_name()
    {
        // Both are called "GPU Fan". Only the kind separates them, and pairing the wrong one would
        // give the start/stop logic a duty where it expects a speed.
        var registry = Machine();

        VendorIdentifier.TryParse("ADLX/AMD Radeon RX 7800 XT/768/fan", out var fan);
        Assert.Equal(SensorKind.FanSpeed, VendorIdentifier.Resolve(fan, registry, out _)!.Kind);

        VendorIdentifier.TryParse("ADLX/AMD Radeon RX 7800 XT/768/control", out var control);
        Assert.Equal(SensorKind.Control, VendorIdentifier.Resolve(control, registry, out _)!.Kind);
    }

    [Theory]
    [InlineData("GPU", "AMD Radeon RX 7800 XT - GPU Core")]
    [InlineData("Hotspot", "AMD Radeon RX 7800 XT - GPU Hot Spot")]
    [InlineData("Memory", "AMD Radeon RX 7800 XT - GPU Memory")]
    public void Each_temperature_maps_onto_the_one_that_means_the_same_thing(string detail, string expected)
    {
        // The two backends do not agree on names: "Hotspot" against "GPU Hot Spot", "GPU" against
        // "GPU Core". Matching on the string would pick the wrong sensor or none.
        var registry = Machine();
        VendorIdentifier.TryParse($"ADLX/AMD Radeon RX 7800 XT/768/temp/{detail}", out var reference);

        Assert.Equal(expected, VendorIdentifier.Resolve(reference, registry, out _)!.Name);
    }

    [Fact]
    public void A_temperature_with_no_equivalent_here_resolves_to_nothing()
    {
        // AMD exposes an intake sensor; LibreHardwareMonitor does not. Handing back the core
        // temperature instead would read plausibly and be wrong.
        var registry = Machine();
        VendorIdentifier.TryParse("ADLX/AMD Radeon RX 7800 XT/768/temp/Intake", out var reference);

        Assert.Null(VendorIdentifier.Resolve(reference, registry, out _));
    }

    [Fact]
    public void A_card_that_is_not_in_the_machine_resolves_to_nothing()
    {
        var registry = Machine();
        VendorIdentifier.TryParse("ADLX/AMD Radeon RX 9070 XT/768/control", out var reference);

        Assert.Null(VendorIdentifier.Resolve(reference, registry, out _));
    }

    [Fact]
    public void Two_identical_cards_resolve_but_say_they_were_ambiguous()
    {
        var registry = Machine();
        registry.Add(new FakeControl("AMD Radeon RX 7800 XT - GPU Fan"));

        VendorIdentifier.TryParse("ADLX/AMD Radeon RX 7800 XT/768/control", out var reference);

        // Name is all there is to go on, so two of the same card cannot be told apart. Resolving to
        // the first is more useful than refusing; saying so is what stops it being a silent guess.
        Assert.NotNull(VendorIdentifier.Resolve(reference, registry, out var ambiguous));
        Assert.True(ambiguous);
    }
}
