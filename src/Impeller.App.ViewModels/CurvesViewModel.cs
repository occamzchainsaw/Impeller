using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Impeller.App.ViewModels.Curves;
using Impeller.App.ViewModels.Engine;
using Impeller.App.ViewModels.Notifications;
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
public sealed partial class CurvesViewModel(EngineConnection connection, NotificationCenter notifications)
    : EnginePageViewModel(connection, notifications)
{
    /// <inheritdoc />
    public override string Title => "Curves";

    /// <summary>Every curve in the configuration.</summary>
    public ObservableCollection<CurveListItemViewModel> Curves { get; } = [];

    /// <summary>
    /// How the page asks the user to confirm deleting a curve, or null to delete without asking.
    /// </summary>
    /// <remarks>
    /// Supplied by the view rather than reached for, because a dialog is a UI-framework thing and
    /// this project deliberately has none. The message is composed here, where the facts are: which
    /// curve it is, and how many fans stop being driven by it.
    /// </remarks>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

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

    /// <summary>
    /// The sensor the open curve reads, and what it says right now.
    /// </summary>
    /// <remarks>
    /// The line above the picker, so the picker itself can be folded away. A curve editor that
    /// showed a hundred and ninety-three radio buttons and never said which one was chosen made the
    /// most important fact on the panel the hardest one to find.
    /// </remarks>
    public string ReadsText => SensorPicker.Selected is { } sensor
        ? $"{sensor.Name} — {sensor.ValueText}"
        : "No sensor chosen yet";

    /// <summary>Whether a sensor has been chosen at all.</summary>
    public bool HasSource => SensorPicker.Selected is not null;

    /// <summary>
    /// Follows the open editor, so any change to it marks the curve unsaved.
    /// </summary>
    /// <remarks>
    /// One handler rather than an event wired to each of thirty controls. The page binds two-way and
    /// says nothing about dirtiness; this notices. The two exclusions are the live read-out, which
    /// changes once a second on its own and would otherwise mark every open curve unsaved within a
    /// second of opening it.
    /// </remarks>
    partial void OnEditorChanged(CurveEditorViewModel? oldValue, CurveEditorViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnEditorEdited;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnEditorEdited;
        }
    }

    private void OnEditorEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CurveEditorViewModel editor
            || e.PropertyName is nameof(CurveEditorViewModel.LiveOutput)
                or nameof(CurveEditorViewModel.OutputText))
        {
            return;
        }

        // The list on the left carries the name, and a rename that only showed up after saving
        // would leave the user looking at two different names for one curve.
        if (e.PropertyName == nameof(CurveEditorViewModel.Name) && Selected is { } selected)
        {
            selected.Name = editor.Name;
        }

        IsDirty = true;
    }

    /// <inheritdoc />
    protected override void OnDisposing()
    {
        if (Editor is { } editor)
        {
            editor.PropertyChanged -= OnEditorEdited;
        }
    }

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
    /// <remarks>
    /// The live read-out is the reason this page follows ticks at all now. It turns the editor from
    /// a form into something you can watch respond, which is the only way to tell a badly shaped
    /// curve from a well shaped one without waiting for the machine to get hot.
    /// </remarks>
    protected override void OnTick(TickSnapshot tick)
    {
        SensorPicker.Apply(tick);

        // The reading inside it moved even though the selection did not.
        OnPropertyChanged(nameof(ReadsText));

        if (Editor is not { } editor)
        {
            return;
        }

        foreach (var reading in tick.Curves)
        {
            if (reading.Id == editor.Id)
            {
                editor.LiveOutput = reading.Output?.Percent;
                return;
            }
        }

        // Not in the tick at all: an unsaved curve the engine has never seen. Saying "no output" is
        // honest, and saying nothing would leave the last saved curve's number under a new one.
        editor.LiveOutput = null;
    }

    /// <summary>
    /// Records which sensor the open curve should read.
    /// </summary>
    /// <remarks>
    /// Here rather than on the tree, because choosing one is an edit: it has to mark the curve
    /// unsaved and refresh the line the picker is folded behind.
    /// </remarks>
    public void ChooseSensor(SensorItemViewModel sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        SensorPicker.Selected = sensor;

        if (Editor is { } editor)
        {
            editor.Source = sensor.Id;
        }

        OnPropertyChanged(nameof(ReadsText));
        OnPropertyChanged(nameof(HasSource));
        IsDirty = true;
    }

    partial void OnSelectedChanged(CurveListItemViewModel? oldValue, CurveListItemViewModel? newValue)
    {
        // A curve added and never saved is not in the configuration, so leaving its row behind
        // gives the user something to click that opens a blank panel. Add's own promise is that a
        // mis-click leaves nothing behind; this is what makes that true.
        if (oldValue is not null && !IsSaved(oldValue))
        {
            Curves.Remove(oldValue);
        }

        if (newValue is null
            || Snapshot is not { } snapshot
            || snapshot.Configuration.Curves.FirstOrDefault(curve => curve.Id == newValue.Id) is not { } definition)
        {
            Editor = null;
            return;
        }

        Editor = new CurveEditorViewModel(definition, OptionsFor(definition));
        SensorPicker.Select(Editor.Source);
        IsDirty = false;

        OnPropertyChanged(nameof(ReadsText));
        OnPropertyChanged(nameof(HasSource));
    }

    /// <summary>
    /// Everything the open curve could point at.
    /// </summary>
    /// <remarks>
    /// Built fresh for each editor rather than kept and handed round: a mix ticks the boxes on
    /// these, so a shared set would carry one curve's choices into the next one opened.
    /// </remarks>
    private CurveEditorOptions OptionsFor(CurveDefinition definition)
    {
        if (Snapshot is not { } snapshot)
        {
            return CurveEditorOptions.Empty;
        }

        var curves = snapshot.Configuration.Curves
            .Where(curve => curve.Id != definition.Id)
            .Select(curve => new CurveChoiceViewModel(curve.Id, curve.Name))
            .ToArray();

        var controls = snapshot.Controls
            .Select(control => new ControlChoice(control.Id, control.DisplayName))
            .ToArray();

        return new CurveEditorOptions(curves, controls);
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
        Editor = new CurveEditorViewModel(definition, OptionsFor(definition));
        IsDirty = true;

        // Cleared rather than left. Setting Selected above cannot open the editor - the curve is
        // not in the configuration yet - so the picker still holds the last curve's sensor, and a
        // brand new curve would show a Reads line naming a sensor it does not actually read.
        SensorPicker.Select(Editor.Source);

        OnPropertyChanged(nameof(ReadsText));
        OnPropertyChanged(nameof(HasSource));
    }

    /// <summary>Saves the open curve into the configuration and applies it.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Editor is not { } editor || Snapshot is not { } snapshot)
        {
            return;
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

        // A curve that was never saved has nothing to lose, so asking about it would be a dialog
        // for a keystroke. Everything else is asked about: Delete sits next to Save and Revert, one
        // click from each, and the curves it removes are the only thing on this page that cannot be
        // got back.
        if (IsSaved(selected)
            && Confirm is { } confirm
            && !await confirm($"Delete '{selected.Name}'?", DeletionCost(selected)).ConfigureAwait(true))
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

    /// <summary>
    /// Throws away the open curve's unsaved changes.
    /// </summary>
    /// <remarks>
    /// For a curve that was never saved this discards the curve itself, which is the only thing
    /// reverting it could mean.
    /// </remarks>
    [RelayCommand]
    private void Revert()
    {
        IsDirty = false;

        var selected = Selected;
        Selected = null;

        // Null unless it survived being deselected, which a never-saved curve does not.
        Selected = selected is not null && Curves.Contains(selected) ? selected : null;
    }

    /// <summary>Whether the engine has this curve, as opposed to it only existing on this page.</summary>
    private bool IsSaved(CurveListItemViewModel item) =>
        Snapshot is { } snapshot && snapshot.Configuration.Curves.Any(curve => curve.Id == item.Id);

    /// <summary>
    /// What deleting this curve costs, in the terms the user cares about.
    /// </summary>
    /// <remarks>
    /// The fans are the point. Deleting a curve switches off everything it was driving, and a
    /// confirmation that did not say so would be a speed bump rather than a warning.
    /// </remarks>
    private static string DeletionCost(CurveListItemViewModel item) => item.Users switch
    {
        0 => "Nothing is using it, so nothing else changes.",
        1 => "The fan it drives will be switched off. This cannot be undone.",
        var count => $"The {count} fans it drives will be switched off. This cannot be undone.",
    };

    private async Task<bool> ApplyAsync(ImpellerConfiguration configuration)
    {
        if (Connection.Engine is not { } engine)
        {
            Notify.Error("Not connected to the engine.", "Nothing was saved.");
            return false;
        }

        try
        {
            var result = await engine.ApplyConfigurationAsync(configuration).ConfigureAwait(true);

            // Warnings are said as well as errors. A curve reading hardware that is not here
            // right now applies perfectly well and is still worth mentioning before the user
            // walks away - so the severity follows whether it applied, not whether it was quiet.
            if (result.Validation.Issues.Count > 0)
            {
                var detail = string.Join(" ", result.Validation.Issues.Select(issue => issue.Message));

                if (result.Applied)
                {
                    Notify.Warn("Saved, with something worth knowing.", detail);
                }
                else
                {
                    Notify.Error("The curve was refused.", detail);
                }
            }
            else if (result.Applied)
            {
                Notify.Success("Curves saved.");
            }

            return result.Applied;
        }
        catch (Exception ex)
        {
            Notify.Error("The curve could not be saved.", ex);
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
