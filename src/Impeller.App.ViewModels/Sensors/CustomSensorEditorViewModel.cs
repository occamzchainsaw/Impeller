using CommunityToolkit.Mvvm.ComponentModel;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels.Sensors;

/// <summary>One way of combining sensors, as something to put in a menu.</summary>
/// <param name="Kind">Which shape of computed sensor it makes.</param>
/// <param name="Label">What the menu says.</param>
public readonly record struct CustomSensorChoice(CustomSensorKind Kind, string Label);

/// <summary>One way of folding several readings into one, as something to put in a list.</summary>
/// <param name="Function">The function itself.</param>
/// <param name="Label">What it does, said rather than named.</param>
public readonly record struct MixChoice(MixFunction Function, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>What a computed sensor can be said to measure.</summary>
/// <param name="Kind">The unit.</param>
/// <param name="Label">Its name in words.</param>
public readonly record struct MeasureChoice(SensorKind Kind, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// A computed sensor being written: one derived from readings the machine already reports.
/// </summary>
/// <remarks>
/// <para>
/// The engine has computed these since Phase 0 and the importer brings FanControl's across, so a
/// machine could arrive with one and no way to make a second, change the first, or see what it was
/// built from. This is that missing half.
/// </para>
/// <para>
/// Shaped like <c>CurveEditorViewModel</c>: the kind is chosen when the sensor is made and fixed
/// afterwards, every kind's fields live here together, and the panel shows the ones that belong to
/// the kind in hand. A sensor that changed shape underneath the user would be a different sensor
/// wearing the same name.
/// </para>
/// </remarks>
public sealed partial class CustomSensorEditorViewModel : ObservableObject
{
    /// <summary>The kinds offered, in the order a menu should list them.</summary>
    /// <remarks>
    /// Mix first because it is the one people come looking for, and the one FanControl has.
    /// </remarks>
    public static IReadOnlyList<CustomSensorChoice> Kinds { get; } =
    [
        new(CustomSensorKind.Mix, "Mix — several sensors combined"),
        new(CustomSensorKind.Offset, "Offset — one sensor, shifted"),
        new(CustomSensorKind.TimeAverage, "Average — one sensor, smoothed over time"),
        new(CustomSensorKind.File, "File — a number read from a file"),
    ];

    /// <summary>How a mix can fold its sources together.</summary>
    public static IReadOnlyList<MixChoice> Functions { get; } =
    [
        new(MixFunction.Maximum, "The highest of them"),
        new(MixFunction.Minimum, "The lowest of them"),
        new(MixFunction.Average, "The average of them"),
        new(MixFunction.Sum, "All of them added up"),
        new(MixFunction.Difference, "The first, minus the rest"),
    ];

    /// <summary>What a file sensor can be declared to hold.</summary>
    /// <remarks>
    /// Only a file sensor is asked. Every other kind derives from sensors that already know what
    /// they measure, and asking the user to restate it invites them to answer wrongly.
    /// </remarks>
    public static IReadOnlyList<MeasureChoice> Measures { get; } =
    [
        new(SensorKind.Temperature, "Temperature (°C)"),
        new(SensorKind.FanSpeed, "Fan speed (RPM)"),
        new(SensorKind.Load, "Load (%)"),
        new(SensorKind.Power, "Power (W)"),
        new(SensorKind.Flow, "Flow (L/h)"),
        new(SensorKind.Level, "Level (%)"),
    ];

    private readonly SensorId _id;
    private readonly SensorKind _sourceKind;

    private bool _settling;

    /// <param name="definition">The sensor being edited, or a fresh one being made.</param>
    /// <param name="sensors">Everything the engine can see, for the source picker.</param>
    /// <param name="isNew">Whether this sensor does not exist yet, which only affects the wording.</param>
    public CustomSensorEditorViewModel(
        CustomSensorDefinition definition,
        IEnumerable<SensorDescriptor> sensors,
        bool isNew)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(sensors);

        _id = definition.Id;
        IsNew = isNew;
        Kind = definition.Kind;
        Name = definition.Name;
        Function = Choice(definition.Function);
        Measure = MeasureFor(definition.Measures);
        AllowMissingSource = definition.AllowMissingSource;
        Offset = definition.Offset;
        Proportional = definition.Proportional;
        WindowSeconds = definition.Window.TotalSeconds;
        Path = definition.Path ?? string.Empty;

        // Temperatures only, as the curve picker does and for the same reason: offering the
        // machine's 194 sensors to be averaged together is offering a great many wrong answers
        // beside the right one. It is also what a mix means in the app this replaces.
        _sourceKind = SensorKind.Temperature;

        Sources = new SensorTreeViewModel
        {
            OnlyKind = _sourceKind,
            IncludeControls = false,

            // A sensor cannot be offered itself. The validator catches every longer loop; this
            // removes the shortest one from the page rather than reporting it afterwards.
            Excluded = [definition.Id],
        };

        Sources.Load(sensors);
        Sources.SetChecked(definition.Sources);
        Sources.CheckedChanged += OnSourcesChanged;
    }

    /// <summary>Whether this sensor is being made rather than changed.</summary>
    public bool IsNew { get; }

    /// <summary>How the value is derived. Fixed once the sensor exists.</summary>
    public CustomSensorKind Kind { get; }

    /// <summary>The sensors it reads, ticked in the order they should be taken.</summary>
    public SensorTreeViewModel Sources { get; }

    /// <summary>What the user calls it.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>How a mix folds its sources together.</summary>
    [ObservableProperty]
    public partial MixChoice Function { get; set; }

    /// <summary>What a file sensor holds.</summary>
    [ObservableProperty]
    public partial MeasureChoice Measure { get; set; }

    /// <summary>Whether a mix still reports when one of its sources stops answering.</summary>
    [ObservableProperty]
    public partial bool AllowMissingSource { get; set; }

    /// <summary>How far an offset sensor shifts its source.</summary>
    [ObservableProperty]
    public partial double Offset { get; set; }

    /// <summary>Whether that shift is a percentage of the source rather than an amount added to it.</summary>
    [ObservableProperty]
    public partial bool Proportional { get; set; }

    /// <summary>How long an averaging sensor looks back.</summary>
    [ObservableProperty]
    public partial double WindowSeconds { get; set; } = 10d;

    /// <summary>The file a file sensor reads.</summary>
    [ObservableProperty]
    public partial string Path { get; set; } = string.Empty;

    /// <summary>Which panel the editor shows.</summary>
    public bool IsMix => Kind == CustomSensorKind.Mix;

    /// <summary>See <see cref="IsMix"/>.</summary>
    public bool IsOffset => Kind == CustomSensorKind.Offset;

    /// <summary>See <see cref="IsMix"/>.</summary>
    public bool IsTimeAverage => Kind == CustomSensorKind.TimeAverage;

    /// <summary>See <see cref="IsMix"/>.</summary>
    public bool IsFile => Kind == CustomSensorKind.File;

    /// <summary>Whether this kind reads sensors at all.</summary>
    public bool ReadsSensors => Kind != CustomSensorKind.File;

    /// <summary>Whether it reads more than one, which is what a mix is.</summary>
    public bool ReadsSeveral => Kind == CustomSensorKind.Mix;

    /// <summary>How many sensors are ticked.</summary>
    public int SourceCount => Sources.Checked.Count;

    /// <summary>What it is reading, for a line above the picker.</summary>
    public string SourcesText => Kind switch
    {
        CustomSensorKind.File => string.IsNullOrWhiteSpace(Path) ? "No file chosen yet" : Path,
        _ => SourceCount switch
        {
            0 => "Nothing chosen yet",
            1 => "1 sensor",
            var count => $"{count} sensors",
        },
    };

    /// <summary>The title of the panel, which says what is about to happen.</summary>
    public string Title => IsNew ? "New computed sensor" : "Edit computed sensor";

    /// <summary>
    /// Whether there is enough here to save.
    /// </summary>
    /// <remarks>
    /// Deliberately the same threshold the engine warns at, not a stricter one. A mix of a single
    /// sensor is pointless rather than wrong, and refusing to save it would be this page inventing
    /// a rule the engine does not have.
    /// </remarks>
    public bool CanSave =>
        !string.IsNullOrWhiteSpace(Name)
        && (IsFile ? !string.IsNullOrWhiteSpace(Path) : SourceCount > 0);

    /// <summary>Turns what is on screen back into something the engine can be given.</summary>
    public CustomSensorDefinition Build() => new()
    {
        Id = _id,
        Name = Name.Trim(),
        Kind = Kind,

        // A derived reading measures whatever it derives from. Only a file sensor has nothing to
        // take that from, so only a file sensor is asked.
        Measures = IsFile ? Measure.Kind : _sourceKind,

        // One kind takes several sources; the rest take the first and would silently ignore any
        // others. Trimming here rather than at the far end means what was saved is what was shown.
        Sources = [.. Taken()],
        Function = Function.Function,
        AllowMissingSource = AllowMissingSource,
        Offset = double.IsFinite(Offset) ? (float)Offset : 0f,
        Proportional = Proportional,
        Window = double.IsFinite(WindowSeconds) && WindowSeconds > 0d
            ? TimeSpan.FromSeconds(WindowSeconds)
            : TimeSpan.FromSeconds(10),
        Path = IsFile ? Path.Trim() : null,
    };

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnPathChanged(string value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SourcesText));
    }

    /// <summary>
    /// Keeps a single-source kind to a single source.
    /// </summary>
    /// <remarks>
    /// An offset or an average reads one sensor. Rather than let several be ticked and quietly use
    /// the first, ticking a second replaces the first - so what is on screen is what will be saved.
    /// The guard is because putting that right raises this event again.
    /// </remarks>
    private void OnSourcesChanged(object? sender, EventArgs e)
    {
        if (!_settling && !ReadsSeveral && Sources.Checked.Count > 1)
        {
            _settling = true;

            try
            {
                Sources.SetChecked([Sources.Checked[^1]]);
            }
            finally
            {
                _settling = false;
            }
        }

        OnPropertyChanged(nameof(SourceCount));
        OnPropertyChanged(nameof(SourcesText));
        OnPropertyChanged(nameof(CanSave));
    }

    private IEnumerable<SensorId> Taken() =>
        !ReadsSensors ? []
        : ReadsSeveral ? Sources.Checked
        : Sources.Checked.Take(1);

    private static MixChoice Choice(MixFunction function)
    {
        foreach (var choice in Functions)
        {
            if (choice.Function == function)
            {
                return choice;
            }
        }

        return Functions[0];
    }

    private static MeasureChoice MeasureFor(SensorKind kind)
    {
        foreach (var choice in Measures)
        {
            if (choice.Kind == kind)
            {
                return choice;
            }
        }

        return Measures[0];
    }
}
