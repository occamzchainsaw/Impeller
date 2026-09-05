using Impeller.App.ViewModels.Sensors;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// The panel that writes a computed sensor.
/// </summary>
/// <remarks>
/// The engine has derived these since Phase 0 and the importer brings FanControl's across, so a
/// machine could arrive with one and no way to make another. These cover the half that was missing:
/// turning what is on screen into a definition the engine will accept.
/// </remarks>
public sealed class CustomSensorEditorTests
{
    private static readonly SensorId Cpu = SensorId.New();
    private static readonly SensorId Gpu = SensorId.New();
    private static readonly SensorId Water = SensorId.New();
    private static readonly SensorId Fan = SensorId.New();

    private static SensorDescriptor Sensor(SensorId id, string name, SensorKind kind, string path) =>
        new(id, name, name, "Nuvoton NCT6687D", kind, "lhm", path, 40f);

    private static List<SensorDescriptor> Machine() =>
    [
        Sensor(Cpu, "CPU", SensorKind.Temperature, "lhm/lpc/nct6687d/0/temperature/0"),
        Sensor(Gpu, "GPU", SensorKind.Temperature, "lhm/gpu-amd/0/temperature/0"),
        Sensor(Water, "Water", SensorKind.Temperature, "lhm/lpc/nct6687d/0/temperature/1"),
        Sensor(Fan, "CPU Fan", SensorKind.FanSpeed, "lhm/lpc/nct6687d/0/fan/0"),
    ];

    private static CustomSensorEditorViewModel Editor(CustomSensorKind kind) =>
        new(new CustomSensorDefinition { Id = SensorId.New(), Kind = kind }, Machine(), isNew: true);

    private static void Tick(CustomSensorEditorViewModel editor, params SensorId[] ids)
    {
        foreach (var id in ids)
        {
            foreach (var group in editor.Sources.Groups)
            {
                foreach (var row in group.Sensors)
                {
                    if (row.Id == id)
                    {
                        row.IsChecked = true;
                    }
                }
            }
        }
    }

    [Fact]
    public void A_mix_is_built_from_what_was_ticked()
    {
        var editor = Editor(CustomSensorKind.Mix);
        editor.Name = "Hottest of the two";
        editor.Function = CustomSensorEditorViewModel.Functions.Single(
            choice => choice.Function == MixFunction.Maximum);

        Tick(editor, Cpu, Gpu);

        var built = editor.Build();

        Assert.Equal("Hottest of the two", built.Name);
        Assert.Equal(CustomSensorKind.Mix, built.Kind);
        Assert.Equal(MixFunction.Maximum, built.Function);
        Assert.Equal([Cpu, Gpu], built.Sources);
    }

    [Fact]
    public void The_order_sensors_are_ticked_in_is_kept()
    {
        // Difference is the first source minus the rest, so the order is the difference between
        // "CPU minus ambient" and "ambient minus CPU".
        var editor = Editor(CustomSensorKind.Mix);
        editor.Name = "Delta";

        Tick(editor, Water, Cpu, Gpu);

        Assert.Equal([Water, Cpu, Gpu], editor.Build().Sources);
    }

    [Fact]
    public void Unticking_one_removes_it_and_leaves_the_rest_in_order()
    {
        var editor = Editor(CustomSensorKind.Mix);
        editor.Name = "Mix";
        Tick(editor, Cpu, Gpu, Water);

        var row = editor.Sources.Groups.SelectMany(group => group.Sensors).Single(item => item.Id == Gpu);
        row.IsChecked = false;

        Assert.Equal([Cpu, Water], editor.Build().Sources);
    }

    [Fact]
    public void Only_temperatures_are_offered()
    {
        // Offering all 194 sensors to be averaged together is offering a great many wrong answers
        // beside the right one.
        var editor = Editor(CustomSensorKind.Mix);

        var offered = editor.Sources.Groups.SelectMany(group => group.Sensors).Select(row => row.Id);

        Assert.DoesNotContain(Fan, offered);
        Assert.Equal(3, editor.Sources.VisibleCount);
    }

    [Fact]
    public void A_sensor_is_never_offered_itself()
    {
        var id = SensorId.New();
        var machine = Machine();
        machine.Add(new SensorDescriptor(
            id, "Existing mix", "Existing mix", "Custom sensors", SensorKind.Temperature,
            ProviderIds.Derived, $"custom/{id}/temperature/0", 44f));

        var editor = new CustomSensorEditorViewModel(
            new CustomSensorDefinition { Id = id, Kind = CustomSensorKind.Mix },
            machine,
            isNew: false);

        Assert.DoesNotContain(id, editor.Sources.Groups.SelectMany(group => group.Sensors).Select(row => row.Id));
    }

    [Fact]
    public void Another_computed_sensor_can_still_be_read()
    {
        var other = SensorId.New();
        var machine = Machine();
        machine.Add(new SensorDescriptor(
            other, "Existing mix", "Existing mix", "Custom sensors", SensorKind.Temperature,
            ProviderIds.Derived, $"custom/{other}/temperature/0", 44f));

        var editor = Editor(CustomSensorKind.Mix);
        editor.Sources.Load(machine);

        Assert.Contains(other, editor.Sources.Groups.SelectMany(group => group.Sensors).Select(row => row.Id));
    }

    [Fact]
    public void An_offset_sensor_reads_exactly_one()
    {
        // Ticking a second replaces the first, so what is on screen is what gets saved rather than
        // several being ticked and the first quietly used.
        var editor = Editor(CustomSensorKind.Offset);
        editor.Name = "CPU plus five";
        editor.Offset = 5d;

        Tick(editor, Cpu);
        Tick(editor, Gpu);

        var built = editor.Build();

        Assert.Equal([Gpu], built.Sources);
        Assert.Equal(5f, built.Offset);
        Assert.Equal(1, editor.SourceCount);
    }

    [Fact]
    public void An_averaging_window_of_nothing_falls_back_rather_than_dividing_by_it()
    {
        var editor = Editor(CustomSensorKind.TimeAverage);
        editor.Name = "Smoothed";
        editor.WindowSeconds = 0d;
        Tick(editor, Cpu);

        Assert.Equal(TimeSpan.FromSeconds(10), editor.Build().Window);
    }

    [Fact]
    public void A_file_sensor_reads_no_sensors_and_says_what_its_number_is()
    {
        var editor = Editor(CustomSensorKind.File);
        editor.Name = "Room";
        editor.Path = @"C:\probe\room.txt";
        editor.Measure = CustomSensorEditorViewModel.Measures.Single(
            choice => choice.Kind == SensorKind.Temperature);

        var built = editor.Build();

        Assert.Empty(built.Sources);
        Assert.Equal(@"C:\probe\room.txt", built.Path);
        Assert.Equal(SensorKind.Temperature, built.Measures);
    }

    [Fact]
    public void Everything_else_measures_what_it_reads()
    {
        var editor = Editor(CustomSensorKind.Mix);
        editor.Name = "Mix";
        Tick(editor, Cpu, Gpu);

        Assert.Equal(SensorKind.Temperature, editor.Build().Measures);
    }

    [Fact]
    public void A_sensor_with_no_name_cannot_be_saved()
    {
        var editor = Editor(CustomSensorKind.Mix);
        Tick(editor, Cpu);

        Assert.False(editor.CanSave);

        editor.Name = "Named";
        Assert.True(editor.CanSave);
    }

    [Fact]
    public void A_sensor_that_reads_nothing_cannot_be_saved()
    {
        var editor = Editor(CustomSensorKind.Mix);
        editor.Name = "Reads nothing";

        Assert.False(editor.CanSave);
    }

    [Fact]
    public void A_file_sensor_needs_a_file_rather_than_a_sensor()
    {
        var editor = Editor(CustomSensorKind.File);
        editor.Name = "Room";

        Assert.False(editor.CanSave);

        editor.Path = @"C:\probe\room.txt";
        Assert.True(editor.CanSave);
    }

    [Fact]
    public void An_existing_sensor_opens_with_everything_it_was_built_from()
    {
        var definition = new CustomSensorDefinition
        {
            Id = SensorId.New(),
            Name = "Loop delta",
            Kind = CustomSensorKind.Mix,
            Function = MixFunction.Difference,
            Sources = [Water, Cpu],
            AllowMissingSource = true,
        };

        var editor = new CustomSensorEditorViewModel(definition, Machine(), isNew: false);

        Assert.Equal("Loop delta", editor.Name);
        Assert.Equal(MixFunction.Difference, editor.Function.Function);
        Assert.True(editor.AllowMissingSource);
        Assert.Equal([Water, Cpu], editor.Sources.Checked);
        Assert.False(editor.IsNew);
    }

    [Fact]
    public void Reopening_and_saving_changes_nothing()
    {
        var definition = new CustomSensorDefinition
        {
            Id = SensorId.New(),
            Name = "Loop delta",
            Kind = CustomSensorKind.Mix,
            Function = MixFunction.Difference,
            Measures = SensorKind.Temperature,
            Sources = [Water, Cpu],
            AllowMissingSource = true,
        };

        var built = new CustomSensorEditorViewModel(definition, Machine(), isNew: false).Build();

        Assert.Equal(definition, built);
    }

    [Fact]
    public void The_group_holding_a_ticked_sensor_opens_itself()
    {
        // Otherwise a sensor already in the mix is behind a closed group, which reads as not
        // being in the mix at all.
        var definition = new CustomSensorDefinition
        {
            Id = SensorId.New(),
            Name = "GPU only",
            Kind = CustomSensorKind.Mix,
            Sources = [Gpu],
        };

        var editor = new CustomSensorEditorViewModel(definition, Machine(), isNew: false);

        Assert.All(
            editor.Sources.Groups.Where(group => group.Sensors.Any(row => row.IsChecked)),
            group => Assert.True(group.IsExpanded, $"{group.Name} hid a ticked sensor."));
    }
}
