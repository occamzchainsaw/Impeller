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

/// <summary>
/// One curve, opened for editing.
/// </summary>
/// <remarks>
/// <para>
/// A single editor for all seven kinds rather than seven view models, because six of the seven are
/// a handful of numbers and only the graph has an interaction worth building. What the view needs
/// to know is which panel to show, which is what <see cref="Kind"/> is for.
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
    private readonly CurveDefinition _original;

    public CurveEditorViewModel(CurveDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        _original = definition;
        Id = definition.Id;
        Name = definition.Name;

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
                Hysteresis = linear.Hysteresis;
                break;

            case GraphCurveDefinition graph:
                Kind = CurveEditorKind.Graph;
                Source = graph.Source;
                Hysteresis = graph.Hysteresis;

                foreach (var point in graph.Points.OrderBy(point => point.Input))
                {
                    Points.Add(new CurvePointViewModel(point.Input, point.Duty.Percent));
                }

                break;

            case MixCurveDefinition mix:
                Kind = CurveEditorKind.Mix;
                Function = mix.Function;

                foreach (var source in mix.Sources)
                {
                    SourceCurves.Add(source);
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
                ResponseUp = trigger.ResponseUp;
                ResponseDown = trigger.ResponseDown;
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
                ResponseUp = auto.ResponseTime;
                break;

            default:
                Kind = CurveEditorKind.Flat;
                break;
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

    /// <summary>The curves a mix combines.</summary>
    public ObservableCollection<CurveId> SourceCurves { get; } = [];

    /// <summary>The lower reading: a ramp's start, a trigger's idle threshold, an auto curve's idle.</summary>
    [ObservableProperty]
    public partial float LowInput { get; set; }

    /// <summary>The upper reading.</summary>
    [ObservableProperty]
    public partial float HighInput { get; set; } = 70f;

    /// <summary>The floor of the duty range, and a flat curve's only value.</summary>
    [ObservableProperty]
    public partial float MinimumDuty { get; set; }

    /// <summary>The ceiling.</summary>
    [ObservableProperty]
    public partial float MaximumDuty { get; set; } = 100f;

    /// <summary>How a mix combines its inputs.</summary>
    [ObservableProperty]
    public partial MixFunction Function { get; set; }

    /// <summary>Whether a sync curve follows a curve or a control.</summary>
    [ObservableProperty]
    public partial SyncSourceKind SyncSourceKind { get; set; }

    /// <summary>The curve a sync follows.</summary>
    [ObservableProperty]
    public partial CurveId SourceCurve { get; set; } = CurveId.None;

    /// <summary>The control a sync follows.</summary>
    [ObservableProperty]
    public partial SensorId SourceControl { get; set; } = SensorId.None;

    /// <summary>A sync curve's shift.</summary>
    [ObservableProperty]
    public partial float Offset { get; set; }

    /// <summary>Whether that shift scales rather than adds.</summary>
    [ObservableProperty]
    public partial bool Proportional { get; set; }

    /// <summary>An auto curve's adjustment size, in percentage points.</summary>
    [ObservableProperty]
    public partial float Step { get; set; } = 2f;

    /// <summary>How far below the target still counts as on target.</summary>
    [ObservableProperty]
    public partial float Deadband { get; set; } = 3f;

    /// <summary>How long a rise must hold before it is acted on.</summary>
    [ObservableProperty]
    public partial TimeSpan ResponseUp { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a fall must hold.</summary>
    [ObservableProperty]
    public partial TimeSpan ResponseDown { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Change suppression on the input, for the kinds that carry it.</summary>
    public HysteresisDefinition Hysteresis { get; set; }

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
            Duty = new Duty(MinimumDuty),
        },

        CurveEditorKind.Linear => new LinearCurveDefinition
        {
            Id = Id,
            Name = Name,
            Source = Source,
            MinimumInput = LowInput,
            MaximumInput = HighInput,
            MinimumDuty = new Duty(MinimumDuty),
            MaximumDuty = new Duty(MaximumDuty),
            Hysteresis = Hysteresis,
        },

        CurveEditorKind.Graph => new GraphCurveDefinition
        {
            Id = Id,
            Name = Name,
            Source = Source,
            Points = [.. Points.Select(point => new CurvePointDefinition(point.Input, new Duty(point.Duty)))],
            Hysteresis = Hysteresis,
        },

        CurveEditorKind.Mix => new MixCurveDefinition
        {
            Id = Id,
            Name = Name,
            Function = Function,
            Sources = [.. SourceCurves],
        },

        CurveEditorKind.Sync => new SyncCurveDefinition
        {
            Id = Id,
            Name = Name,
            SourceKind = SyncSourceKind,
            SourceCurve = SourceCurve,
            SourceControl = SourceControl,
            Offset = Offset,
            Proportional = Proportional,
        },

        CurveEditorKind.Trigger => new TriggerCurveDefinition
        {
            Id = Id,
            Name = Name,
            Source = Source,
            IdleInput = LowInput,
            LoadInput = HighInput,
            IdleDuty = new Duty(MinimumDuty),
            LoadDuty = new Duty(MaximumDuty),
            ResponseUp = ResponseUp,
            ResponseDown = ResponseDown,
        },

        CurveEditorKind.Auto => new AutoCurveDefinition
        {
            Id = Id,
            Name = Name,
            Source = Source,
            IdleTemperature = LowInput,
            LoadTemperature = HighInput,
            MinimumDuty = new Duty(MinimumDuty),
            MaximumDuty = new Duty(MaximumDuty),
            Step = Step,
            Deadband = Deadband,
            ResponseTime = ResponseUp,
        },

        _ => _original,
    };

    private static float ClampDuty(float duty) =>
        Math.Clamp(duty, Duty.MinPercent, Duty.MaxPercent);
}
