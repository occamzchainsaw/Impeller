using Impeller.App.ViewModels.Sensors;
using Impeller.Core.Abstractions;
using Impeller.Ipc.Contracts;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// Covers grouping and filtering 193 sensors into something a person can find one in.
/// </summary>
/// <remarks>
/// The names and paths here are the real shapes: the provider prefixes every sensor with its
/// hardware's name, and the path is the rendered fingerprint. A tree that groups invented names
/// correctly and these incorrectly would be worth nothing.
/// </remarks>
public class SensorTreeTests
{
    private static SensorDescriptor Sensor(
        string hardware,
        string name,
        SensorKind kind,
        string path,
        float? value = 40f) =>
        new(SensorId.New(), name, name, hardware, kind, "lhm", path, value);

    private static List<SensorDescriptor> Machine() =>
    [
        Sensor("Nuvoton NCT6687D", "CPU Fan", SensorKind.FanSpeed, "lhm/lpc/nct6687d/0/fan/0", 980f),
        Sensor("Nuvoton NCT6687D", "Fan #4", SensorKind.Control, "lhm/lpc/nct6687d/0/control/4", 55f),
        Sensor("Nuvoton NCT6687D", "System", SensorKind.Temperature, "lhm/lpc/nct6687d/0/temperature/1", 34f),
        Sensor("AMD Ryzen 7 9800X3D", "Core (Tctl/Tdie)", SensorKind.Temperature, "lhm/amdcpu/0/temperature/2", 61f),
        Sensor("AMD Radeon RX 7800 XT", "GPU Core", SensorKind.Temperature, "lhm/gpu-amd/0/temperature/0", 36f),
        Sensor("AMD Radeon RX 7800 XT", "GPU Fan", SensorKind.FanSpeed, "lhm/gpu-amd/0/fan/0", 0f),
    ];

    private static SensorTreeViewModel Loaded()
    {
        var tree = new SensorTreeViewModel();
        tree.Load(Machine());
        return tree;
    }

    [Fact]
    public void Sensors_are_grouped_by_the_hardware_they_are_on()
    {
        var tree = Loaded();

        Assert.Equal(3, tree.Groups.Count);
        Assert.Equal(6, tree.VisibleCount);

        var board = tree.Groups.Single(group => group.Name == "Nuvoton NCT6687D");
        Assert.Equal(3, board.Sensors.Count);
    }

    [Fact]
    public void A_group_is_headed_with_the_hardware_name_rather_than_its_path()
    {
        // "lhm/lpc/nct6687d/0" is an identifier and reads like one. Someone looking for their
        // motherboard's fan headers is looking for the name on the box.
        var tree = Loaded();

        Assert.Contains(tree.Groups, group => group.Name == "AMD Radeon RX 7800 XT");
        Assert.DoesNotContain(tree.Groups, group => group.Name.StartsWith("lhm/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_does_not_repeat_the_heading_it_sits_under()
    {
        var tree = Loaded();

        var board = tree.Groups.Single(group => group.Name == "Nuvoton NCT6687D");

        Assert.Contains(board.Sensors, sensor => sensor.Name == "CPU Fan");
        Assert.DoesNotContain(board.Sensors, sensor => sensor.Name.Contains("NCT6687D", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_identical_cards_stay_two_groups()
    {
        // Grouping on the name would fold them together, and the user would have one heading with
        // two of every sensor under it and no way to tell which card is which.
        var sensors = Machine();
        sensors.Add(Sensor("AMD Radeon RX 7800 XT", "GPU Core", SensorKind.Temperature, "lhm/gpu-amd/1/temperature/0"));

        var tree = new SensorTreeViewModel();
        tree.Load(sensors);

        Assert.Equal(2, tree.Groups.Count(group => group.Name == "AMD Radeon RX 7800 XT"));
    }

    [Fact]
    public void Searching_narrows_to_matching_sensors_and_drops_empty_groups()
    {
        var tree = Loaded();

        tree.Search = "gpu";

        Assert.Equal(2, tree.VisibleCount);
        Assert.Single(tree.Groups);
        Assert.Equal("AMD Radeon RX 7800 XT", tree.Groups[0].Name);
    }

    [Fact]
    public void The_hardware_path_is_searchable_as_well_as_the_name()
    {
        // The path is what a log or a diagnostic report names, so pasting one in should find it.
        var tree = Loaded();

        tree.Search = "nct6687d/0/control";

        Assert.Equal(1, tree.VisibleCount);
    }

    [Fact]
    public void Clearing_the_search_brings_everything_back()
    {
        var tree = Loaded();

        tree.Search = "gpu";
        tree.Search = string.Empty;

        Assert.Equal(6, tree.VisibleCount);
    }

    [Fact]
    public void A_picker_restricted_to_temperatures_offers_only_those()
    {
        // A curve reads a temperature. Offering the machine's voltages alongside is offering a
        // hundred wrong answers next to the right one.
        var tree = new SensorTreeViewModel { OnlyKind = SensorKind.Temperature };
        tree.Load(Machine());

        Assert.Equal(3, tree.VisibleCount);
        Assert.All(tree.Groups.SelectMany(group => group.Sensors),
            sensor => Assert.Equal(SensorKind.Temperature, sensor.Kind));
    }

    [Fact]
    public void A_selection_survives_a_search_that_still_contains_it()
    {
        var tree = Loaded();
        var gpu = tree.Groups.Single(group => group.Name == "AMD Radeon RX 7800 XT").Sensors[0];

        tree.Selected = gpu;
        tree.Search = "radeon";

        Assert.NotNull(tree.Selected);
        Assert.Equal(gpu.Id, tree.Selected.Id);
    }

    [Fact]
    public void A_selection_filtered_out_is_cleared_rather_than_left_invisible()
    {
        var tree = Loaded();
        tree.Selected = tree.Groups.Single(group => group.Name == "AMD Radeon RX 7800 XT").Sensors[0];

        tree.Search = "nuvoton";

        Assert.Null(tree.Selected);
    }

    [Fact]
    public void A_tick_updates_the_rows_in_place()
    {
        // Rebuilding once a second would flicker the list, lose the scroll position and drop the
        // selection — which on the picker is the thing the user was in the middle of choosing.
        var sensors = Machine();
        var tree = new SensorTreeViewModel();
        tree.Load(sensors);

        var row = tree.Groups.SelectMany(group => group.Sensors).First(item => item.Id == sensors[0].Id);
        var before = row;

        tree.Apply(new TickSnapshot(1, DateTimeOffset.UnixEpoch, [new SensorReading(sensors[0].Id, 1450f)], []));

        Assert.Same(before, tree.Groups.SelectMany(group => group.Sensors).First(item => item.Id == sensors[0].Id));
        Assert.Equal("1450 RPM", row.ValueText);
    }

    [Theory]
    [InlineData(SensorKind.Temperature, 61.5f, "61.5 °C")]
    [InlineData(SensorKind.FanSpeed, 980f, "980 RPM")]
    [InlineData(SensorKind.Control, 55f, "55 %")]
    [InlineData(SensorKind.Power, 142.4f, "142.4 W")]
    public void A_reading_is_shown_with_its_unit(SensorKind kind, float value, string expected)
    {
        // A bare number is ambiguous between 45 degrees and 45 percent, and those are the two most
        // common kinds on the page.
        var tree = new SensorTreeViewModel();
        tree.Load([Sensor("Board", "Thing", kind, "lhm/board/0/x/0", value)]);

        Assert.Equal(expected, tree.Groups[0].Sensors[0].ValueText);
    }

    [Fact]
    public void A_sensor_that_is_not_reporting_shows_a_dash_rather_than_a_zero()
    {
        var tree = new SensorTreeViewModel();
        tree.Load([Sensor("Board", "Thing", SensorKind.Temperature, "lhm/board/0/temperature/0", null)]);

        Assert.Equal("—", tree.Groups[0].Sensors[0].ValueText);
    }
}
