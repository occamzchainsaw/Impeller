using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Impeller.App.ViewModels.Curves;
using Impeller.App.ViewModels.Engine;
using Impeller.App.ViewModels.Sensors;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels;

/// <summary>A curve in the list beside the editor.</summary>
public sealed partial class CurveListItemViewModel(CurveDefinition definition) : ObservableObject
{
    /// <summary>Which curve.</summary>
    public CurveId Id { get; } = definition.Id;

    /// <summary>What the user calls it.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = definition.Name;

    /// <summary>Which kind it is, in a word.</summary>
    public string Kind { get; } = definition switch
    {
        FlatCurveDefinition => "Flat",
        LinearCurveDefinition => "Linear",
        GraphCurveDefinition => "Graph",
        MixCurveDefinition => "Mix",
        SyncCurveDefinition => "Sync",
        TriggerCurveDefinition => "Trigger",
        AutoCurveDefinition => "Auto",
        _ => "Curve",
    };

    /// <summary>How many controls this curve drives, so deleting one says what it costs.</summary>
    [ObservableProperty]
    public partial int Users { get; set; }
}

/// <summary>
/// Fan curves, their inputs, and which controls they drive.
/// </summary>
/// <remarks>
/// Edits are held here until saved. Sending each change as it happens sounds more responsive and
/// means dragging a point across a canvas applies twenty intermediate configurations to real fans,
/// most of them shapes the user was passing through rather than choosing.
/// </remarks>
public sealed partial class CurvesViewModel(EngineConnection connection)
    : EnginePageViewModel(connection)
{
    /// <inheritdoc />
    public override string Title => "Curves";

    /// <summary>Every curve in the configuration.</summary>
    public ObservableCollection<CurveListItemViewModel> Curves { get; } = [];

    /// <summary>The sensors a curve can read, restricted to temperatures.</summary>
    public SensorTreeViewModel SensorPicker { get; } = new()
    {
        OnlyKind = SensorKind.Temperature,
        IncludeControls = false,
    };

    /// <summary>The curve currently open, or null.</summary>
    [ObservableProperty]
    public partial CurveListItemViewModel? Selected { get; set; }

    /// <summary>The editor for it.</summary>
    [ObservableProperty]
    public partial CurveEditorViewModel? Editor { get; private set; }

    /// <summary>Whether the open curve has unsaved changes.</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    /// <summary>Whatever the last save turned up, or null.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
        SensorPicker.Load(snapshot.Sensors);

        var previous = Selected?.Id;

        var users = snapshot.Configuration.Controls
            .GroupBy(binding => binding.CurveId)
            .ToDictionary(group => group.Key, group => group.Count());

        Curves.Clear();

        foreach (var definition in snapshot.Configuration.Curves)
        {
            Curves.Add(new CurveListItemViewModel(definition)
            {
                Users = users.GetValueOrDefault(definition.Id),
            });
        }

        // An unsaved edit survives a snapshot arriving. One turns up whenever anything else changes
        // the configuration, and losing a half-drawn curve because a fan was pinned in another
        // window would be its own kind of bug.
        if (IsDirty && Editor is not null)
        {
            return;
        }

        Selected = previous is { } id
            ? Curves.FirstOrDefault(curve => curve.Id == id)
            : Curves.FirstOrDefault();
    }

    /// <inheritdoc />
    protected override void OnTick(TickSnapshot tick) => SensorPicker.Apply(tick);

    partial void OnSelectedChanged(CurveListItemViewModel? value)
    {
        if (value is null
            || Snapshot is not { } snapshot
            || snapshot.Configuration.Curves.FirstOrDefault(curve => curve.Id == value.Id) is not { } definition)
        {
            Editor = null;
            return;
        }

        Editor = new CurveEditorViewModel(definition);
        SensorPicker.Select(Editor.Source);
        IsDirty = false;
        Problem = null;
    }

    /// <summary>
    /// Adds a curve of one kind and opens it.
    /// </summary>
    /// <remarks>
    /// Created here and not saved until the user says so, so a mis-click leaves nothing behind and
    /// nothing reaches a fan.
    /// </remarks>
    [RelayCommand]
    private void Add(CurveEditorKind kind)
    {
        var id = CurveId.New();
        var name = UnusedName(kind.ToString());

        CurveDefinition definition = kind switch
        {
            CurveEditorKind.Linear => new LinearCurveDefinition
            {
                Id = id,
                Name = name,
                MinimumInput = 35f,
                MaximumInput = 70f,
            },

            CurveEditorKind.Graph => new GraphCurveDefinition
            {
                Id = id,
                Name = name,

                // Two points, because that is the fewest a graph can be and still be a graph, and
                // an empty canvas gives the user nothing to take hold of.
                Points =
                [
                    new CurvePointDefinition(30f, new Duty(20f)),
                    new CurvePointDefinition(70f, Duty.Full),
                ],
            },

            CurveEditorKind.Mix => new MixCurveDefinition { Id = id, Name = name },
            CurveEditorKind.Sync => new SyncCurveDefinition { Id = id, Name = name },

            CurveEditorKind.Trigger => new TriggerCurveDefinition
            {
                Id = id,
                Name = name,
                IdleInput = 40f,
                LoadInput = 60f,
            },

            CurveEditorKind.Auto => new AutoCurveDefinition { Id = id, Name = name },
            _ => new FlatCurveDefinition { Id = id, Name = name, Duty = new Duty(50f) },
        };

        var item = new CurveListItemViewModel(definition);
        Curves.Add(item);

        Selected = item;
        Editor = new CurveEditorViewModel(definition);
        IsDirty = true;
    }

    /// <summary>Saves the open curve into the configuration and applies it.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Editor is not { } editor || Snapshot is not { } snapshot)
        {
            return;
        }

        if (SensorPicker.Selected is { } sensor)
        {
            editor.Source = sensor.Id;
        }

        var definition = editor.Build();
        var curves = snapshot.Configuration.Curves.ToArray();
        var index = Array.FindIndex(curves, curve => curve.Id == definition.Id);

        if (index < 0)
        {
            curves = [.. curves, definition];
        }
        else
        {
            curves[index] = definition;
        }

        if (await ApplyAsync(snapshot.Configuration with { Curves = [.. curves] }).ConfigureAwait(true))
        {
            IsDirty = false;
        }
    }

    /// <summary>
    /// Deletes the open curve, and unbinds anything that was using it.
    /// </summary>
    /// <remarks>
    /// The unbinding is the point. A control left pointing at a curve that no longer exists makes
    /// the whole configuration fail validation, so deleting one curve would otherwise refuse to
    /// save until the user had found every control that referenced it.
    /// </remarks>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } selected || Snapshot is not { } snapshot)
        {
            return;
        }

        var curves = snapshot.Configuration.Curves
            .Where(curve => curve.Id != selected.Id)
            .ToArray();

        var controls = snapshot.Configuration.Controls
            .Select(binding => binding.CurveId == selected.Id
                ? binding with { CurveId = CurveId.None, Enabled = false }
                : binding)
            .ToArray();

        IsDirty = false;

        await ApplyAsync(snapshot.Configuration with
        {
            Curves = [.. curves],
            Controls = [.. controls],
        }).ConfigureAwait(true);
    }

    /// <summary>Throws away the open curve's unsaved changes.</summary>
    [RelayCommand]
    private void Revert()
    {
        IsDirty = false;

        var selected = Selected;
        Selected = null;
        Selected = selected;
    }

    private async Task<bool> ApplyAsync(ImpellerConfiguration configuration)
    {
        if (Connection.Engine is not { } engine)
        {
            Problem = "Not connected to the engine.";
            return false;
        }

        try
        {
            var result = await engine.ApplyConfigurationAsync(configuration).ConfigureAwait(true);

            // Warnings are shown as well as errors. A curve reading hardware that is not here right
            // now applies perfectly well and is still worth mentioning before the user walks away.
            Problem = result.Validation.Issues.Count == 0
                ? null
                : string.Join(" ", result.Validation.Issues.Select(issue => issue.Message));

            return result.Applied;
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            return false;
        }
    }

    private string UnusedName(string kind)
    {
        var candidate = $"{kind} curve";
        var suffix = 2;

        while (Curves.Any(curve => string.Equals(curve.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{kind} curve {suffix++}";
        }

        return candidate;
    }
}
