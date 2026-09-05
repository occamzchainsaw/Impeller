using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Impeller.Core.Abstractions;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels.Sensors;

/// <summary>One sensor, as a row.</summary>
public sealed partial class SensorItemViewModel(
    SensorDescriptor descriptor,
    Func<SensorId, string?, Task>? rename = null) : ObservableObject
{
    /// <summary>Stable identity, and the only thing a configuration ever stores.</summary>
    public SensorId Id { get; } = descriptor.Id;

    /// <summary>What to show: the user's name for it, or the sensor's own.</summary>
    /// <remarks>
    /// Under a heading that already names the hardware, a row that repeats it reads its own address
    /// out twice. The provider supplies the two separately, so there is nothing to strip.
    /// </remarks>
    public string Name { get; } = descriptor.DisplayName;

    /// <summary>What the hardware calls it, so a renamed sensor can still be found by it.</summary>
    public string ProviderName { get; } = descriptor.Name;

    /// <summary>The name as the user is editing it.</summary>
    [ObservableProperty]
    public partial string EditableName { get; set; } = descriptor.DisplayName;

    /// <summary>Whether the field is open, so a refresh does not overwrite what is being typed.</summary>
    public bool IsRenaming { get; set; }

    /// <summary>
    /// Whether this is the row a picker has chosen.
    /// </summary>
    /// <remarks>
    /// Carried on the row rather than compared against the tree's selection in the view, because a
    /// radio button in a data template has no way to ask "am I the selected one" — which is why the
    /// picker used to open with nothing checked however long ago the choice had been made.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Its hardware and its own name together, for a tooltip and for a flat list.</summary>
    public string FullName { get; } = string.IsNullOrWhiteSpace(descriptor.HardwareName)
        ? descriptor.Name
        : $"{descriptor.HardwareName} — {descriptor.Name}";

    /// <summary>
    /// Gives it the user's own name, or restores the provider's.
    /// </summary>
    /// <remarks>
    /// Here as well as on a fan's card, because a temperature has no card and "Radiator out" is
    /// worth exactly as much as a named fan.
    /// </remarks>
    [RelayCommand]
    private async Task RenameAsync()
    {
        IsRenaming = false;

        var wanted = EditableName?.Trim() ?? string.Empty;

        if (rename is null || string.Equals(wanted, Name, StringComparison.Ordinal))
        {
            return;
        }

        var value = wanted.Length == 0 || string.Equals(wanted, ProviderName, StringComparison.Ordinal)
            ? null
            : wanted;

        await rename(Id, value).ConfigureAwait(true);
    }

    /// <summary>What it measures.</summary>
    public SensorKind Kind { get; } = descriptor.Kind;

    /// <summary>What it measures, in words.</summary>
    /// <remarks>
    /// The enum was on screen directly, so the column that should say "Fan speed" said "FanSpeed"
    /// - a name written for the compiler, shown to the user. Only the compound names need help;
    /// the rest are already the English word for the thing.
    /// </remarks>
    public string KindText { get; } = Describe(descriptor.Kind);

    /// <summary>Where it lives, shown when two sensors are otherwise indistinguishable.</summary>
    public string HardwarePath { get; } = descriptor.HardwarePath;

    /// <summary>The latest reading, formatted with its unit.</summary>
    [ObservableProperty]
    public partial string ValueText { get; private set; } = Format(descriptor.Kind, descriptor.Value);

    /// <summary>Takes a new reading.</summary>
    public void Apply(float? value) => ValueText = Format(Kind, value);

    /// <summary>One kind of measurement, named the way a person would name it.</summary>
    private static string Describe(SensorKind kind) => kind switch
    {
        SensorKind.FanSpeed => "Fan speed",

        // "Control" alone is the shell's own word for it. What the row is, to the person reading,
        // is the header a fan plugs into.
        SensorKind.Control => "Fan header",
        SensorKind.Unknown => "Unrecognised",
        _ => kind.ToString(),
    };

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

    /// <summary>Whether its sensors are showing.</summary>
    /// <remarks>
    /// Closed to begin with. This machine reports 193 sensors and every one of them used to be on
    /// screen at once, which is not a list anyone reads - it is a wall to scroll past on the way to
    /// the search box. Closed, the page opens as what it actually is: the hardware Impeller found,
    /// with the readings a click away.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
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

    /// <summary>The groups the user opened, by key, so a rebuild does not shut them again.</summary>
    /// <remarks>
    /// Every rebuild replaces the group objects, so their state has to live somewhere that outlives
    /// them. Keyed on the hardware key rather than the name for the reason the grouping is: two
    /// pieces of hardware are allowed to share a name.
    /// </remarks>
    private readonly HashSet<string> _opened = new(StringComparer.Ordinal);

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

    /// <summary>
    /// How a row asks for a sensor to be renamed, or null when this tree does not offer it.
    /// </summary>
    /// <remarks>
    /// Supplied rather than reached for, so the tree stays a view over descriptors and the curve
    /// picker - which uses the same tree - does not quietly become an editor.
    /// </remarks>
    public Func<SensorId, string?, Task>? Rename { get; set; }

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
    public void Select(SensorId id)
    {
        Selected = _items.GetValueOrDefault(id);

        if (Selected is { } row)
        {
            Reveal(row);
        }
    }

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

    /// <summary>Keeps the rows' own flags agreeing with the one selection.</summary>
    partial void OnSelectedChanged(SensorItemViewModel? oldValue, SensorItemViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    private void Rebuild()
    {
        var previous = Selected?.Id;

        foreach (var group in Groups)
        {
            group.PropertyChanged -= OnGroupChanged;
        }

        Groups.Clear();
        _items.Clear();

        // A search opens what it finds, and closing the search closes them again - except the ones
        // the user opened themselves, which were their answer to a question and outlast the search.
        var searching = !string.IsNullOrWhiteSpace(Search);
        var matching = _all.Where(Matches).ToList();

        // Grouped, then each group's rows ordered by kind so a card's temperatures, speeds and
        // controls arrive in the same order on every piece of hardware.
        foreach (var group in matching.GroupBy(SensorNaming.HardwareKey))
        {
            var view = new SensorGroupViewModel(group.Key, SensorNaming.HardwareName(group))
            {
                IsExpanded = searching || _opened.Contains(group.Key),
            };

            foreach (var descriptor in group.OrderBy(sensor => sensor.Kind).ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase))
            {
                var item = new SensorItemViewModel(descriptor, Rename);
                view.Sensors.Add(item);
                _items[descriptor.Id] = item;
            }

            // Subscribed after the initial state is set, so opening a group to show a search
            // result is not mistaken for the user having asked for it.
            view.PropertyChanged += OnGroupChanged;
            Groups.Add(view);
        }

        VisibleCount = _items.Count;

        // A selection that is still in the tree survives a search; one that has been filtered out
        // is cleared rather than left pointing at a row nobody can see.
        Selected = previous is { } id ? _items.GetValueOrDefault(id) : null;

        // The rows above are new objects, so the flag has to be set on the replacement.
        if (Selected is { } selected)
        {
            selected.IsSelected = true;
            Reveal(selected);
        }
    }

    /// <summary>
    /// Opens the group a row is in, so a chosen sensor is not hidden inside a closed one.
    /// </summary>
    /// <remarks>
    /// The picker opens on the sensor a curve already reads. With the groups closed by default that
    /// selection would be somewhere behind one of them, which looks exactly like no selection at
    /// all - the bug <see cref="SensorItemViewModel.IsSelected"/> exists to have fixed once.
    /// </remarks>
    private void Reveal(SensorItemViewModel row)
    {
        foreach (var group in Groups)
        {
            if (group.Sensors.Contains(row))
            {
                group.IsExpanded = true;
                return;
            }
        }
    }

    /// <summary>Records the groups the user opens and closes, so their choice survives a rebuild.</summary>
    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SensorGroupViewModel.IsExpanded)
            || sender is not SensorGroupViewModel group)
        {
            return;
        }

        if (group.IsExpanded)
        {
            _opened.Add(group.Key);
        }
        else
        {
            _opened.Remove(group.Key);
        }
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

        // The hardware name is searched too, so typing "nuvoton" still finds the motherboard's
        // sensors now that it is no longer glued onto the front of every one of their names.
        return string.IsNullOrWhiteSpace(Search)
            || sensor.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || sensor.HardwareName.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || sensor.HardwarePath.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Turns provider-supplied paths into something to group by.</summary>
internal static class SensorNaming
{
    /// <summary>
    /// The hardware part of a sensor's path.
    /// </summary>
    /// <remarks>
    /// The rendered fingerprint ends in its kind and channel, so dropping the last two segments
    /// leaves exactly the hardware. Structural, so two identical cards stay two groups.
    /// </remarks>
    public static string HardwareKey(SensorDescriptor sensor)
    {
        // A computed sensor is not on hardware, and its fingerprint says so by carrying the
        // sensor's own id where the others carry a chip. Keyed structurally it would land in a
        // group of one, headed by a GUID, so they are grouped by the thing they do share.
        if (string.Equals(sensor.ProviderId, ProviderIds.Derived, StringComparison.Ordinal))
        {
            return sensor.ProviderId;
        }

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
    /// What to head a group with: the hardware name its members carry, or the path.
    /// </summary>
    /// <remarks>
    /// Read from the descriptor rather than sliced off the front of a name. A user looking for
    /// their motherboard's fan headers is looking for "Nuvoton NCT6687D", not
    /// "lhm/lpc/nct6687d/0" - and the provider now says which it is instead of leaving the shell
    /// to find a separator.
    /// </remarks>
    public static string HardwareName(IEnumerable<SensorDescriptor> group)
    {
        foreach (var sensor in group)
        {
            if (!string.IsNullOrWhiteSpace(sensor.HardwareName))
            {
                return sensor.HardwareName;
            }
        }

        return group.Select(HardwareKey).FirstOrDefault() ?? "Hardware";
    }
}
