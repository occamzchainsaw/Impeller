using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.App.ViewModels.Curves;

/// <summary>Which editor a curve needs, so the view can show the right panel.</summary>
public enum CurveEditorKind
{
    /// <summary>One duty, and nothing else.</summary>
    Flat = 0,

    /// <summary>A ramp between two readings.</summary>
    Linear,

    /// <summary>Points the user places by hand.</summary>
    Graph,

    /// <summary>Several curves combined.</summary>
    Mix,

    /// <summary>Another curve or control mirrored.</summary>
    Sync,

    /// <summary>Two speeds with a band between them.</summary>
    Trigger,

    /// <summary>A controller that seeks whatever duty holds a target temperature.</summary>
    Auto,
}

/// <summary>One vertex of a graph curve, as the canvas drags it.</summary>
public sealed partial class CurvePointViewModel(float input, float duty) : ObservableObject
{
    /// <summary>The sensor reading this point sits at.</summary>
    [ObservableProperty]
    public partial float Input { get; set; } = input;

    /// <summary>The duty to command there.</summary>
    [ObservableProperty]
    public partial float Duty { get; set; } = duty;
}

/// <summary>Another curve, as something this one could point at.</summary>
public sealed partial class CurveChoiceViewModel(CurveId id, string name) : ObservableObject
{
    /// <summary>Which curve.</summary>
    public CurveId Id { get; } = id;

    /// <summary>What the user calls it.</summary>
    public string Name { get; } = name;

    /// <summary>Whether this curve is one of the inputs. Only a mix uses this.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A control, as something a sync curve could follow.</summary>
public sealed record ControlChoice(SensorId Id, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A way of combining curves, in words rather than an enum name.</summary>
public sealed record MixFunctionChoice(MixFunction Function, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// What a curve could point at: every other curve, and every control.
/// </summary>
/// <remarks>
/// Supplied rather than reached for, because the editor holds no connection and knows nothing about
/// the configuration it came out of. A mix with nothing to mix and a sync with nothing to follow are
/// both real states, and both look like an empty list.
/// </remarks>
public sealed record CurveEditorOptions(
    IReadOnlyList<CurveChoiceViewModel> Curves,
    IReadOnlyList<ControlChoice> Controls)
{
    /// <summary>Nothing to point at, for a curve opened without a configuration behind it.</summary>
    public static CurveEditorOptions Empty { get; } = new([], []);
}

/// <summary>
/// One curve, opened for editing.
/// </summary>
/// <remarks>
/// <para>
/// One editor for all seven kinds rather than seven view models: they share identity, a name and
/// most of their numbers, and the differences are which of those numbers mean anything. What the
/// view needs to know is which panel to show, which is what <see cref="Kind"/> and the
/// <c>Is…</c> predicates below are for. The panel a kind gets shows only its own fields — the
/// four-box grid that served all seven at once offered a mix curve a temperature range it could not
/// use, gave a flat curve a lower and an upper value, and called an auto curve's target an "upper
/// temperature".
/// </para>
/// <para>
/// Nothing here reaches the engine. The editor produces a <see cref="CurveDefinition"/> and the page
/// above it sends a whole configuration to be validated and applied — so a half-finished edit never
/// reaches a fan, and an edit that would not validate is refused as one piece rather than partly
/// applied.
/// </para>
/// </remarks>
public sealed partial class CurveEditorViewModel : ObservableObject
{
    /// <summary>The ways a mix can combine its inputs, in the order they are worth trying.</summary>
    public static IReadOnlyList<MixFunctionChoice> MixFunctions { get; } =
    [
        new(MixFunction.Maximum, "Whichever is highest"),
        new(MixFunction.Minimum, "Whichever is lowest"),
        new(MixFunction.Average, "The average of them"),
        new(MixFunction.Sum, "All of them added up"),
        new(MixFunction.Difference, "The first, minus the rest"),
    ];

    private readonly CurveDefinition _original;

    public CurveEditorViewModel(CurveDefinition definition)
        : this(definition, CurveEditorOptions.Empty)
    {
    }

    public CurveEditorViewModel(CurveDefinition definition, CurveEditorOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

        _original = definition;
        Id = definition.Id;
        Name = definition.Name;

        foreach (var control in options.Controls)
        {
            ControlChoices.Add(control);
        }

        // A curve may not be one of its own inputs: a mix that includes itself is a cycle the
        // engine refuses, and offering it is offering a way to break the configuration.
        foreach (var curve in options.Curves.Where(curve => curve.Id != definition.Id))
        {
            CurveChoices.Add(curve);
        }

        switch (definition)
        {
            case FlatCurveDefinition flat:
                Kind = CurveEditorKind.Flat;
                MinimumDuty = flat.Duty.Percent;
                break;

            case LinearCurveDefinition linear:
                Kind = CurveEditorKind.Linear;
                Source = linear.Source;
                LowInput = linear.MinimumInput;
                HighInput = linear.MaximumInput;
                MinimumDuty = linear.MinimumDuty.Percent;
                MaximumDuty = linear.MaximumDuty.Percent;
                TakeHysteresis(linear.Hysteresis);
                break;

            case GraphCurveDefinition graph:
                Kind = CurveEditorKind.Graph;
                Source = graph.Source;
                TakeHysteresis(graph.Hysteresis);

                foreach (var point in graph.Points.OrderBy(point => point.Input))
                {
                    Points.Add(new CurvePointViewModel(point.Input, point.Duty.Percent));
                }

                break;

            case MixCurveDefinition mix:
                Kind = CurveEditorKind.Mix;
                Function = mix.Function;

                foreach (var choice in CurveChoices)
                {
                    choice.IsSelected = mix.Sources.Contains(choice.Id);
                }

                break;

            case SyncCurveDefinition sync:
                Kind = CurveEditorKind.Sync;
                SyncSourceKind = sync.SourceKind;
                SourceCurve = sync.SourceCurve;
                SourceControl = sync.SourceControl;
                Offset = sync.Offset;
                Proportional = sync.Proportional;
                break;

            case TriggerCurveDefinition trigger:
                Kind = CurveEditorKind.Trigger;
                Source = trigger.Source;
                LowInput = trigger.IdleInput;
                HighInput = trigger.LoadInput;
                MinimumDuty = trigger.IdleDuty.Percent;
                MaximumDuty = trigger.LoadDuty.Percent;
                ResponseUpSeconds = trigger.ResponseUp.TotalSeconds;
                ResponseDownSeconds = trigger.ResponseDown.TotalSeconds;
                break;

            case AutoCurveDefinition auto:
                Kind = CurveEditorKind.Auto;
                Source = auto.Source;
                LowInput = auto.IdleTemperature;
                HighInput = auto.LoadTemperature;
                MinimumDuty = auto.MinimumDuty.Percent;
                MaximumDuty = auto.MaximumDuty.Percent;
                Step = auto.Step;
                Deadband = auto.Deadband;
                ResponseUpSeconds = auto.ResponseTime.TotalSeconds;
                break;

            default:
                Kind = CurveEditorKind.Flat;
                break;
        }

        SelectedCurve = CurveChoices.FirstOrDefault(curve => curve.Id == SourceCurve);
        SelectedControl = ControlChoices.FirstOrDefault(control => control.Id == SourceControl);

        // Ticking a mix input changes the row, not this object, and the page above watches this
        // object to know the curve is unsaved. Forwarded rather than left for the view to notice.
        foreach (var choice in CurveChoices)
        {
            choice.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CurveChoices));
        }
    }

    /// <summary>Which curve this is. Unchanged by editing — a curve keeps its identity through a rename.</summary>
    public CurveId Id { get; }

    /// <summary>Which editor the view should show.</summary>
    public CurveEditorKind Kind { get; }

    /// <summary>What the user calls it.</summary>
    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>The sensor driving it, for the kinds that read one.</summary>
    [ObservableProperty]
    public partial SensorId Source { get; set; } = SensorId.None;

    /// <summary>The vertices of a graph curve, always in ascending input order.</summary>
    public ObservableCollection<CurvePointViewModel> Points { get; } = [];

    /// <summary>Every other curve, for a mix to tick and a sync to pick from.</summary>
    public ObservableCollection<CurveChoiceViewModel> CurveChoices { get; } = [];

    /// <summary>Every control, for a sync to follow.</summary>
    public ObservableCollection<ControlChoice> ControlChoices { get; } = [];

    /// <summary>Which of the two things a sync curve can follow is chosen: 0 a curve, 1 a control.</summary>
    /// <remarks>
    /// An index because that is what a <c>RadioButtons</c> holds. The enum has a third value, None,
    /// which is a state and not a choice: a curve that has picked neither shows the curve picker,
    /// because that is much the commoner of the two and an empty panel looks broken.
    /// </remarks>
    public int SyncSourceIndex
    {
        get => SyncSourceKind == SyncSourceKind.Control ? 1 : 0;
        set => SyncSourceKind = value == 1 ? SyncSourceKind.Control : SyncSourceKind.Curve;
    }

    /// <summary>Whether there is anything for a mix or a sync to point at.</summary>
    /// <remarks>
    /// A mix in a configuration with one curve is not broken, it is premature — and an empty list
    /// with no sentence beside it looks like the former.
    /// </remarks>
    public bool HasOtherCurves => CurveChoices.Count > 0;

    /// <summary>
    /// The lower reading: a ramp's start, a trigger's idle threshold, an auto curve's idle.
    /// </summary>
    /// <remarks>
    /// Doubles throughout, here and below, because that is what a <c>NumberBox</c> and a
    /// <c>Slider</c> hold and two-way binding wants the types to match exactly. The alternative is a
    /// page of hand-written fill-and-write-back code, which is what this replaced — and which had
    /// already produced one bug where switching curves wrote the old one's numbers into the new one.
    /// </remarks>
    [ObservableProperty]
    public partial double LowInput { get; set; }

    /// <summary>The upper reading.</summary>
    [ObservableProperty]
    public partial double HighInput { get; set; } = 70d;

    /// <summary>The floor of the duty range, and a flat curve's only value.</summary>
    [ObservableProperty]
    public partial double MinimumDuty { get; set; }

    /// <summary>The ceiling.</summary>
    [ObservableProperty]
    public partial double MaximumDuty { get; set; } = 100d;

    /// <summary>How a mix combines its inputs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFunction))]
    public partial MixFunction Function { get; set; }

    /// <summary>The same choice, as the item a combo box holds.</summary>
    public MixFunctionChoice? SelectedFunction
    {
        get => MixFunctions.FirstOrDefault(choice => choice.Function == Function);
        set
        {
            if (value is not null)
            {
                Function = value.Function;
            }
        }
    }

    /// <summary>Whether a sync curve follows a curve or a control.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowsCurve))]
    [NotifyPropertyChangedFor(nameof(FollowsControl))]
    [NotifyPropertyChangedFor(nameof(SyncSourceIndex))]
    public partial SyncSourceKind SyncSourceKind { get; set; }

    /// <summary>
    /// Whether the sync panel should offer curves.
    /// </summary>
    /// <remarks>
    /// The default for a curve that has chosen neither. A brand new sync curve showing no picker at
    /// all looks broken, and following another curve is much the commoner of the two.
    /// </remarks>
    public bool FollowsCurve
    {
        get => SyncSourceKind != SyncSourceKind.Control;
        set
        {
            if (value)
            {
                SyncSourceKind = SyncSourceKind.Curve;
            }
        }
    }

    /// <summary>Whether it should offer controls instead.</summary>
    public bool FollowsControl
    {
        get => SyncSourceKind == SyncSourceKind.Control;
        set
        {
            if (value)
            {
                SyncSourceKind = SyncSourceKind.Control;
            }
        }
    }

    /// <summary>The curve a sync follows.</summary>
    [ObservableProperty]
    public partial CurveId SourceCurve { get; set; } = CurveId.None;

    /// <summary>The control a sync follows.</summary>
    [ObservableProperty]
    public partial SensorId SourceControl { get; set; } = SensorId.None;

    /// <summary>The same curve, as the item a combo box holds.</summary>
    [ObservableProperty]
    public partial CurveChoiceViewModel? SelectedCurve { get; set; }

    /// <summary>The same control.</summary>
    [ObservableProperty]
    public partial ControlChoice? SelectedControl { get; set; }

    /// <summary>A sync curve's shift.</summary>
    [ObservableProperty]
    public partial double Offset { get; set; }

    /// <summary>Whether that shift scales the source rather than adding to it.</summary>
    [ObservableProperty]
    public partial bool Proportional { get; set; }

    /// <summary>An auto curve's adjustment size, in percentage points.</summary>
    [ObservableProperty]
    public partial double Step { get; set; } = 2d;

    /// <summary>How far below the target still counts as on target.</summary>
    [ObservableProperty]
    public partial double Deadband { get; set; } = 3d;

    /// <summary>
    /// How long a rise must hold before it is acted on, in seconds.
    /// </summary>
    /// <remarks>
    /// Seconds rather than a <see cref="TimeSpan"/> because that is what the box on screen accepts,
    /// and a view model that made the view do the conversion would be making the view do arithmetic.
    /// </remarks>
    [ObservableProperty]
    public partial double ResponseUpSeconds { get; set; } = 2d;

    /// <summary>How long a fall must hold.</summary>
    [ObservableProperty]
    public partial double ResponseDownSeconds { get; set; } = 4d;

    /// <summary>How far the reading must rise before a rise counts, in the sensor's own units.</summary>
    [ObservableProperty]
    public partial double DeadbandUp { get; set; }

    /// <summary>How far it must fall.</summary>
    [ObservableProperty]
    public partial double DeadbandDown { get; set; }

    /// <summary>How long a rise must persist before the curve acts on it, in seconds.</summary>
    [ObservableProperty]
    public partial double HysteresisUpSeconds { get; set; }

    /// <summary>How long a fall must persist.</summary>
    [ObservableProperty]
    public partial double HysteresisDownSeconds { get; set; }

    /// <summary>
    /// What this curve is asking for right now, or null when it has no answer.
    /// </summary>
    /// <remarks>
    /// Pushed in from the tick rather than worked out here. Four of the seven kinds carry state — a
    /// trigger latches, an auto curve integrates, a sync follows something that does — so the only
    /// honest answer is the engine's own.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputText))]
    public partial float? LiveOutput { get; set; }

    /// <summary>That output, in words.</summary>
    public string OutputText => LiveOutput is { } duty ? $"{duty:0.#} %" : "no output";

    /// <summary>Whether it is a flat curve, which is a constant and needs one control.</summary>
    public bool IsFlat => Kind == CurveEditorKind.Flat;

    /// <summary>Whether it is a straight ramp.</summary>
    public bool IsLinear => Kind == CurveEditorKind.Linear;

    /// <summary>Whether it is the one kind with a canvas.</summary>
    public bool IsGraph => Kind == CurveEditorKind.Graph;

    /// <summary>Whether it combines other curves.</summary>
    public bool IsMix => Kind == CurveEditorKind.Mix;

    /// <summary>Whether it mirrors something else.</summary>
    public bool IsSync => Kind == CurveEditorKind.Sync;

    /// <summary>Whether it is two speeds with a band between them.</summary>
    public bool IsTrigger => Kind == CurveEditorKind.Trigger;

    /// <summary>Whether it seeks a target temperature.</summary>
    public bool IsAuto => Kind == CurveEditorKind.Auto;

    /// <summary>
    /// Whether it reads a sensor at all.
    /// </summary>
    /// <remarks>
    /// Mix and sync read other curves, and flat reads nothing. Offering any of them a temperature is
    /// offering a setting that does nothing.
    /// </remarks>
    public bool ReadsASensor => Kind
        is CurveEditorKind.Linear or CurveEditorKind.Graph
        or CurveEditorKind.Trigger or CurveEditorKind.Auto;

    /// <summary>
    /// Whether change suppression applies to it.
    /// </summary>
    /// <remarks>
    /// Only the two kinds that map a reading straight onto a duty. A trigger already has thresholds
    /// and hold times of its own, and an auto curve a deadband — giving either a second set would be
    /// two mechanisms arguing over the same decision.
    /// </remarks>
    public bool HasHysteresis => Kind is CurveEditorKind.Linear or CurveEditorKind.Graph;

    /// <summary>The lowest reading the graph canvas draws.</summary>
    public float AxisMinimum { get; set; }

    /// <summary>The highest.</summary>
    public float AxisMaximum { get; set; } = 100f;

    /// <summary>
    /// Adds a vertex, keeping the collection sorted.
    /// </summary>
    /// <returns>The new point, or the existing one when there is already a point at that reading.</returns>
    /// <remarks>
    /// Two points at the same input make the curve ambiguous at exactly that reading, so a click on
    /// top of an existing point selects it rather than stacking a second one behind it.
    /// </remarks>
    public CurvePointViewModel AddPoint(float input, float duty)
    {
        var clampedInput = Math.Clamp(input, AxisMinimum, AxisMaximum);

        if (Points.FirstOrDefault(point => Math.Abs(point.Input - clampedInput) < 0.001f) is { } existing)
        {
            return existing;
        }

        var added = new CurvePointViewModel(clampedInput, ClampDuty(duty));
        var index = 0;

        while (index < Points.Count && Points[index].Input < clampedInput)
        {
            index++;
        }

        Points.Insert(index, added);
        return added;
    }

    /// <summary>
    /// Removes a vertex, unless it is one of the last two.
    /// </summary>
    /// <returns>Whether it was removed.</returns>
    /// <remarks>
    /// A curve with one point is a flat curve wearing a graph's clothes, and one with none has
    /// nothing to say at all — at which point the engine holds the last duty and the fan sits
    /// wherever it was when the user deleted the second-to-last point.
    /// </remarks>
    public bool RemovePoint(CurvePointViewModel point) =>
        Points.Count > 2 && Points.Remove(point);

    /// <summary>
    /// Moves a vertex, keeping it between its neighbours.
    /// </summary>
    /// <remarks>
    /// Clamping the reading rather than re-sorting is what makes the drag feel like a curve editor:
    /// the point the user grabbed stays the point they are moving, and the curve cannot fold back
    /// on itself and become two duties for one temperature. Duty is free to go anywhere in range,
    /// because a curve that falls as it heats is unusual but perfectly legitimate.
    /// </remarks>
    public void MovePoint(CurvePointViewModel point, float input, float duty)
    {
        ArgumentNullException.ThrowIfNull(point);

        var index = Points.IndexOf(point);

        if (index < 0)
        {
            return;
        }

        var lower = index == 0 ? AxisMinimum : Points[index - 1].Input;
        var upper = index == Points.Count - 1 ? AxisMaximum : Points[index + 1].Input;

        point.Input = Math.Clamp(input, lower, upper);
        point.Duty = ClampDuty(duty);
    }

    /// <summary>Builds the definition this editor now describes.</summary>
    public CurveDefinition Build() => Kind switch
    {
        CurveEditorKind.Flat => new FlatCurveDefinition
        {
            Id = Id,
            Name = Name,
            Duty = new Duty(Percent(MinimumDuty)),
        },

        CurveEditorKind.Linear => new LinearCurveDefinition
        {
            Id = Id,
            Name = Name,
            Source = Source,
            MinimumInput = Number(LowInput),
            MaximumInput = Number(HighInput),
            MinimumDuty = new Duty(Percent(MinimumDuty)),
            MaximumDuty = new Duty(Percent(MaximumDuty)),
            Hysteresis = BuildHysteresis(),
        },

        CurveEditorKind.Graph => new GraphCurveDefinition
        {
            Id = Id,
            Name = Name,
            Source = Source,
            Points = [.. Points.Select(point => new CurvePointDefinition(point.Input, new Duty(point.Duty)))],
            Hysteresis = BuildHysteresis(),
        },

        CurveEditorKind.Mix => new MixCurveDefinition
        {
            Id = Id,
            Name = Name,
            Function = Function,

            // In the order they are listed, which for Difference is the order that decides which one
            // everything else is subtracted from.
            Sources = [.. CurveChoices.Where(curve => curve.IsSelected).Select(curve => curve.Id)],
        },

        CurveEditorKind.Sync => new SyncCurveDefinition
        {
            Id = Id,
            Name = Name,
            SourceKind = SyncSourceKind,
            SourceCurve = SelectedCurve?.Id ?? CurveId.None,
            SourceControl = SelectedControl?.Id ?? SensorId.None,
            Offset = Number(Offset),
            Proportional = Proportional,
        },

        CurveEditorKind.Trigger => new TriggerCurveDefinition
        {
            Id = Id,
            Name = Name,
            Source = Source,
            IdleInput = Number(LowInput),
            LoadInput = Number(HighInput),
            IdleDuty = new Duty(Percent(MinimumDuty)),
            LoadDuty = new Duty(Percent(MaximumDuty)),
            ResponseUp = Seconds(ResponseUpSeconds),
            ResponseDown = Seconds(ResponseDownSeconds),
        },

        CurveEditorKind.Auto => new AutoCurveDefinition
        {
            Id = Id,
            Name = Name,
            Source = Source,
            IdleTemperature = Number(LowInput),
            LoadTemperature = Number(HighInput),
            MinimumDuty = new Duty(Percent(MinimumDuty)),
            MaximumDuty = new Duty(Percent(MaximumDuty)),
            Step = Number(Step),
            Deadband = Number(Deadband),
            ResponseTime = Seconds(ResponseUpSeconds),
        },

        _ => _original,
    };

    private void TakeHysteresis(HysteresisDefinition hysteresis)
    {
        DeadbandUp = hysteresis.DeadbandUp;
        DeadbandDown = hysteresis.DeadbandDown;
        HysteresisUpSeconds = hysteresis.ResponseUp.TotalSeconds;
        HysteresisDownSeconds = hysteresis.ResponseDown.TotalSeconds;
    }

    private HysteresisDefinition BuildHysteresis() => new(
        Number(DeadbandUp),
        Number(DeadbandDown),
        Seconds(HysteresisUpSeconds),
        Seconds(HysteresisDownSeconds));

    /// <summary>
    /// A box's value, or zero when it is empty.
    /// </summary>
    /// <remarks>
    /// An emptied <c>NumberBox</c> reports NaN, and writing that through would put a threshold at a
    /// value nothing compares true against — a curve that silently never fires.
    /// </remarks>
    private static float Number(double value) => double.IsFinite(value) ? (float)value : 0f;

    /// <summary>The same, held inside a duty's range.</summary>
    private static float Percent(double value) => ClampDuty(Number(value));

    /// <summary>
    /// Seconds as a span, treating nonsense as none.
    /// </summary>
    /// <remarks>
    /// An emptied number box reports NaN, and <see cref="TimeSpan.FromSeconds(double)"/> throws on
    /// it. A hold time of zero is a perfectly good answer; an exception out of a text box is not.
    /// </remarks>
    private static TimeSpan Seconds(double value) =>
        double.IsFinite(value) && value > 0d ? TimeSpan.FromSeconds(value) : TimeSpan.Zero;

    private static float ClampDuty(float duty) =>
        Math.Clamp(duty, Duty.MinPercent, Duty.MaxPercent);

    partial void OnSelectedCurveChanged(CurveChoiceViewModel? value) =>
        SourceCurve = value?.Id ?? CurveId.None;

    partial void OnSelectedControlChanged(ControlChoice? value) =>
        SourceControl = value?.Id ?? SensorId.None;
}
