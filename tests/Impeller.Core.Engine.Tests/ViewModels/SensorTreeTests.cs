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

    /// <summary>A computed sensor, whose fingerprint carries its own id where a chip would be.</summary>
    private static SensorDescriptor Derived(string name)
    {
        var id = SensorId.New();
        return new SensorDescriptor(
            id,
            name,
            name,
            CustomHardware,
            SensorKind.Temperature,
            ProviderIds.Derived,
            $"custom/{id}/temperature/0",
            42f);
    }

    private const string CustomHardware = "Custom sensors";

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
    public void A_renamed_sensor_is_found_by_the_name_the_user_gave_it()
    {
        // The row shows the user's name, so the search has to accept it. Without this, renaming a
        // fan makes it unfindable by the only name its owner now knows it by - and the row is still
        // sitting there, under a search box that says it is not.
        var machine = Machine();
        machine[1] = machine[1] with { DisplayName = "Seat blower" };

        var tree = new SensorTreeViewModel { IncludeControls = true };
        tree.Load(machine);

        tree.Search = "blower";

        var found = tree.Groups.SelectMany(group => group.Sensors).ToList();

        Assert.Single(found);
        Assert.Equal("Seat blower", found[0].Name);
    }

    [Fact]
    public void A_renamed_sensor_is_still_found_by_what_the_hardware_calls_it()
    {
        // The other direction, and the reason the provider name stays in the search: someone
        // reading a manual, or a forum post, knows it as Fan #4 whatever it has been renamed to.
        var machine = Machine();
        machine[1] = machine[1] with { DisplayName = "Seat blower" };

        var tree = new SensorTreeViewModel { IncludeControls = true };
        tree.Load(machine);

        tree.Search = "Fan #4";

        Assert.Single(tree.Groups.SelectMany(group => group.Sensors));
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

    [Fact]
    public void The_chosen_row_knows_it_is_the_chosen_one()
    {
        // A radio button inside a data template cannot ask the tree whether it is the selection, so
        // the answer is carried on the row. Without it the picker opened with nothing checked
        // however long ago the choice had been made.
        var sensors = new[]
        {
            Sensor("Board", "CPU", SensorKind.Temperature, "lhm/board/0/temperature/0", 40f),
            Sensor("Board", "System", SensorKind.Temperature, "lhm/board/0/temperature/1", 30f),
        };

        var tree = new SensorTreeViewModel();
        tree.Load(sensors);

        tree.Select(sensors[0].Id);
        Assert.True(tree.Groups[0].Sensors[0].IsSelected);

        tree.Select(sensors[1].Id);

        Assert.False(tree.Groups[0].Sensors[0].IsSelected);
        Assert.True(tree.Groups[0].Sensors[1].IsSelected);
    }

    [Fact]
    public void Choosing_nothing_clears_the_choice()
    {
        // How a brand new curve is opened. It reads nothing yet, and a picker still showing the
        // previous curve's sensor would name a sensor this curve does not actually read — a panel
        // disagreeing with the thing it would save.
        var sensors = new[] { Sensor("Board", "CPU", SensorKind.Temperature, "lhm/board/0/temperature/0", 40f) };

        var tree = new SensorTreeViewModel();
        tree.Load(sensors);
        tree.Select(sensors[0].Id);

        tree.Select(SensorId.None);

        Assert.Null(tree.Selected);
        Assert.False(tree.Groups[0].Sensors[0].IsSelected);
    }

    [Fact]
    public void A_choice_that_survives_a_search_is_still_shown_as_chosen()
    {
        // Filtering rebuilds every row, so the flag has to be set on the replacement rather than
        // left on the object that was thrown away.
        var sensors = new[]
        {
            Sensor("Board", "CPU package", SensorKind.Temperature, "lhm/board/0/temperature/0", 40f),
            Sensor("Board", "System", SensorKind.Temperature, "lhm/board/0/temperature/1", 30f),
        };

        var tree = new SensorTreeViewModel();
        tree.Load(sensors);
        tree.Select(sensors[0].Id);

        tree.Search = "package";

        Assert.True(tree.Groups[0].Sensors[0].IsSelected);
    }

    [Fact]
    public void Groups_start_closed()
    {
        // 193 rows on screen is not a list, it is a wall to scroll past on the way to the search.
        var tree = Loaded();

        Assert.All(tree.Groups, group => Assert.False(group.IsExpanded, $"{group.Name} opened itself."));
    }

    [Fact]
    public void A_search_opens_what_it_found()
    {
        var tree = Loaded();

        tree.Search = "GPU";

        Assert.NotEmpty(tree.Groups);
        Assert.All(tree.Groups, group => Assert.True(group.IsExpanded, $"{group.Name} stayed shut."));
    }

    [Fact]
    public void Clearing_the_search_closes_them_again()
    {
        var tree = Loaded();

        tree.Search = "GPU";
        tree.Search = string.Empty;

        Assert.All(tree.Groups, group => Assert.False(group.IsExpanded, $"{group.Name} stayed open."));
    }

    [Fact]
    public void A_group_the_user_opened_stays_open_through_a_search()
    {
        // Their own answer to a question, not a side effect of typing, so it outlasts the search.
        var tree = Loaded();
        var board = tree.Groups.Single(group => group.Name == "Nuvoton NCT6687D");

        board.IsExpanded = true;
        tree.Search = "GPU";
        tree.Search = string.Empty;

        Assert.True(tree.Groups.Single(group => group.Name == "Nuvoton NCT6687D").IsExpanded);
    }

    [Fact]
    public void A_group_the_user_closed_stays_closed()
    {
        var tree = Loaded();
        var board = tree.Groups.Single(group => group.Name == "Nuvoton NCT6687D");

        board.IsExpanded = true;
        board.IsExpanded = false;
        tree.Search = "Fan";
        tree.Search = string.Empty;

        Assert.False(tree.Groups.Single(group => group.Name == "Nuvoton NCT6687D").IsExpanded);
    }

    [Fact]
    public void Choosing_a_sensor_opens_the_group_it_is_in()
    {
        // The picker opens on the sensor a curve already reads. Behind a closed group, that looks
        // exactly like nothing having been chosen.
        var machine = Machine();
        var tree = new SensorTreeViewModel();
        tree.Load(machine);

        tree.Select(machine.Single(sensor => sensor.Name == "GPU Core").Id);

        var gpu = tree.Groups.Single(group => group.Name == "AMD Radeon RX 7800 XT");
        Assert.True(gpu.IsExpanded);
        Assert.Same(gpu.Sensors.Single(row => row.Name == "GPU Core"), tree.Selected);
    }

    [Fact]
    public void A_selected_sensor_is_still_visible_after_a_search_is_cleared()
    {
        var machine = Machine();
        var tree = new SensorTreeViewModel();
        tree.Load(machine);
        tree.Select(machine.Single(sensor => sensor.Name == "GPU Core").Id);

        tree.Search = "GPU";
        tree.Search = string.Empty;

        Assert.True(tree.Groups.Single(group => group.Name == "AMD Radeon RX 7800 XT").IsExpanded);
    }

    [Fact]
    public void A_kind_is_named_the_way_a_person_would_name_it()
    {
        // The enum used to be on screen directly, so a tachometer read "FanSpeed".
        var tree = Loaded();
        var board = tree.Groups.Single(group => group.Name == "Nuvoton NCT6687D");

        Assert.Equal("Fan speed", board.Sensors.Single(row => row.Name == "CPU Fan").KindText);
        Assert.Equal("Fan header", board.Sensors.Single(row => row.Name == "Fan #4").KindText);
        Assert.Equal("Temperature", board.Sensors.Single(row => row.Name == "System").KindText);
    }

    [Fact]
    public void Computed_sensors_share_one_group_instead_of_one_each()
    {
        // Their fingerprints carry their own ids, so keyed like hardware every one of them was a
        // group of its own - and the heading was the GUID.
        var tree = new SensorTreeViewModel();
        tree.Load([.. Machine(), Derived("Water average"), Derived("CPU and GPU mix")]);

        var custom = tree.Groups.Single(group => group.Name == CustomHardware);

        Assert.Equal(2, custom.Sensors.Count);
        Assert.Equal(4, tree.Groups.Count);
    }
}
