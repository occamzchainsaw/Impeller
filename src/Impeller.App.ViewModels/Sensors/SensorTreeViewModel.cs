using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Impeller.Core.Abstractions;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels.Sensors;

/// <summary>One sensor, as a row.</summary>
public sealed partial class SensorItemViewModel(SensorDescriptor descriptor) : ObservableObject
{
    /// <summary>Stable identity, and the only thing a configuration ever stores.</summary>
    public SensorId Id { get; } = descriptor.Id;

    /// <summary>
    /// What to show, with the hardware's name taken off the front.
    /// </summary>
    /// <remarks>
    /// The provider gives every sensor its hardware's name as a prefix, which is right for a flat
    /// list and wrong under a heading that already says it. "Nuvoton NCT6687D — CPU Fan" under a
    /// "Nuvoton NCT6687D" heading is a row that reads its own address out twice.
    /// </remarks>
    public string Name { get; } = SensorNaming.WithoutHardware(descriptor.Name);

    /// <summary>The full name, for a tooltip and for searching.</summary>
    public string FullName { get; } = descriptor.Name;

    /// <summary>What it measures.</summary>
    public SensorKind Kind { get; } = descriptor.Kind;

    /// <summary>Where it lives, shown when two sensors are otherwise indistinguishable.</summary>
    public string HardwarePath { get; } = descriptor.HardwarePath;

    /// <summary>The latest reading, formatted with its unit.</summary>
    [ObservableProperty]
    public partial string ValueText { get; private set; } = Format(descriptor.Kind, descriptor.Value);

    /// <summary>Takes a new reading.</summary>
    public void Apply(float? value) => ValueText = Format(Kind, value);

    /// <summary>
    /// A reading with its unit, or a dash.
    /// </summary>
    /// <remarks>
    /// A bare number is ambiguous between 45 degrees and 45 percent, and those are the two most
    /// common kinds on the page.
    /// </remarks>
    private static string Format(SensorKind kind, float? value) => value switch
    {
        null => "—",
        var reading => kind switch
        {
            SensorKind.Temperature => $"{reading:0.#} °C",
            SensorKind.FanSpeed => $"{reading:0} RPM",
            SensorKind.Control or SensorKind.Load or SensorKind.Level => $"{reading:0.#} %",
            SensorKind.Power => $"{reading:0.#} W",
            SensorKind.Voltage => $"{reading:0.###} V",
            SensorKind.Current => $"{reading:0.##} A",
            SensorKind.Clock => $"{reading:0} MHz",
            SensorKind.Flow => $"{reading:0.#} L/h",
            SensorKind.Data => $"{reading:0.#} GB",
            _ => $"{reading:0.##}",
        },
    };
}

/// <summary>One piece of hardware, and the sensors on it.</summary>
public sealed partial class SensorGroupViewModel(string key, string name) : ObservableObject
{
    /// <summary>What identifies this hardware, structurally rather than by name.</summary>
    public string Key { get; } = key;

    /// <summary>What to call it.</summary>
    public string Name { get; } = name;

    /// <summary>The sensors on it that currently pass the filter.</summary>
    public ObservableCollection<SensorItemViewModel> Sensors { get; } = [];

    /// <summary>Whether anything in it survived the filter.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;
}

/// <summary>
/// Every sensor the engine can see, grouped by the hardware it belongs to and searchable.
/// </summary>
/// <remarks>
/// <para>
/// This machine reports 193 sensors, which is why a flat list is not an option and a dropdown
/// certainly is not. Grouped by hardware with a search box, the same tree serves both the sensors
/// page and the picker a curve uses to choose its input.
/// </para>
/// <para>
/// Grouping is structural — on the hardware part of the fingerprint's path — rather than on the
/// name. Names are provider-supplied and two pieces of hardware are allowed to share one; the path
/// is what actually distinguishes them, and using it means a second identical card does not fold
/// into the first.
/// </para>
/// </remarks>
public sealed partial class SensorTreeViewModel : ObservableObject
{
    private readonly List<SensorDescriptor> _all = [];
    private readonly Dictionary<SensorId, SensorItemViewModel> _items = [];

    /// <summary>The hardware groups that currently have anything to show.</summary>
    public ObservableCollection<SensorGroupViewModel> Groups { get; } = [];

    /// <summary>
    /// Restricts the tree to one kind of sensor.
    /// </summary>
    /// <remarks>
    /// Set by a picker choosing a curve's input, where offering the machine's voltages alongside
    /// its temperatures is offering a hundred wrong answers next to the right one.
    /// </remarks>
    public SensorKind? OnlyKind { get; set; }

    /// <summary>Whether writable controls are offered as well as readable sensors.</summary>
    public bool IncludeControls { get; set; } = true;

    /// <summary>What the user typed. Matched against the full name and the hardware path.</summary>
    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    /// <summary>The row the user picked, if this tree is being used to choose one.</summary>
    [ObservableProperty]
    public partial SensorItemViewModel? Selected { get; set; }

    /// <summary>How many sensors are showing, after filtering.</summary>
    [ObservableProperty]
    public partial int VisibleCount { get; private set; }

    /// <summary>Takes the full set of sensors, replacing whatever was there.</summary>
    public void Load(IEnumerable<SensorDescriptor> sensors)
    {
        ArgumentNullException.ThrowIfNull(sensors);

        _all.Clear();
        _all.AddRange(sensors);
        Rebuild();
    }

    /// <summary>Selects the row for an id, if the tree holds one. Used to show a curve's current input.</summary>
    public void Select(SensorId id) => Selected = _items.GetValueOrDefault(id);

    /// <summary>Takes one tick's readings, updating rows in place.</summary>
    /// <remarks>
    /// Matched by id and written into the existing rows rather than rebuilt. Rebuilding once a
    /// second would flicker the list, lose the user's scroll position, and drop their selection —
    /// which on the picker is the thing they were in the middle of choosing.
    /// </remarks>
    public void Apply(TickSnapshot tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        foreach (var reading in tick.Sensors)
        {
            if (_items.TryGetValue(reading.Id, out var item))
            {
                item.Apply(reading.Value);
            }
        }
    }

    partial void OnSearchChanged(string value) => Rebuild();

    private void Rebuild()
    {
        var previous = Selected?.Id;

        Groups.Clear();
        _items.Clear();

        var matching = _all.Where(Matches).ToList();

        // Grouped, then each group's rows ordered by kind so a card's temperatures, speeds and
        // controls arrive in the same order on every piece of hardware.
        foreach (var group in matching.GroupBy(SensorNaming.HardwareKey))
        {
            var view = new SensorGroupViewModel(group.Key, SensorNaming.HardwareName(group));

            foreach (var descriptor in group.OrderBy(sensor => sensor.Kind).ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase))
            {
                var item = new SensorItemViewModel(descriptor);
                view.Sensors.Add(item);
                _items[descriptor.Id] = item;
            }

            Groups.Add(view);
        }

        VisibleCount = _items.Count;

        // A selection that is still in the tree survives a search; one that has been filtered out
        // is cleared rather than left pointing at a row nobody can see.
        Selected = previous is { } id ? _items.GetValueOrDefault(id) : null;
    }

    private bool Matches(SensorDescriptor sensor)
    {
        if (OnlyKind is { } kind && sensor.Kind != kind)
        {
            return false;
        }

        if (!IncludeControls && sensor.Kind == SensorKind.Control)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(Search)
            || sensor.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || sensor.HardwarePath.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Turns provider-supplied names and paths into something to group and label by.</summary>
internal static class SensorNaming
{
    /// <summary>What the provider puts between a hardware name and a sensor name.</summary>
    private const string Separator = " — ";

    /// <summary>
    /// The hardware part of a sensor's path.
    /// </summary>
    /// <remarks>
    /// The rendered fingerprint ends in its kind and channel, so dropping the last two segments
    /// leaves exactly the hardware. Structural, so two identical cards stay two groups.
    /// </remarks>
    public static string HardwareKey(SensorDescriptor sensor)
    {
        var path = sensor.HardwarePath;
        var kind = path.LastIndexOf('/');

        if (kind <= 0)
        {
            return path;
        }

        var channel = path.LastIndexOf('/', kind - 1);
        return channel <= 0 ? path : path[..channel];
    }

    /// <summary>
    /// What to head a group with: the hardware name the members agree on, or the path.
    /// </summary>
    /// <remarks>
    /// Taken from the members rather than from the path because the path is an identifier and reads
    /// like one. A user looking for their motherboard's fan headers is looking for "Nuvoton
    /// NCT6687D", not "lhm/lpc/nct6687d/0".
    /// </remarks>
    public static string HardwareName(IEnumerable<SensorDescriptor> group)
    {
        foreach (var sensor in group)
        {
            var separator = sensor.Name.IndexOf(Separator, StringComparison.Ordinal);

            if (separator > 0)
            {
                return sensor.Name[..separator];
            }
        }

        return group.Select(HardwareKey).FirstOrDefault() ?? "Hardware";
    }

    /// <summary>A sensor's own name, with its hardware's name taken off the front.</summary>
    public static string WithoutHardware(string name)
    {
        var separator = name.IndexOf(Separator, StringComparison.Ordinal);
        return separator > 0 ? name[(separator + Separator.Length)..] : name;
    }
}
