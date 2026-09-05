using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Impeller.App.ViewModels.Curves;
using Impeller.App.ViewModels.Engine;
using Impeller.App.ViewModels.Notifications;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels;

/// <summary>One curve, as a row on the page.</summary>
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

    /// <summary>What it reads, or what it is built from.</summary>
    [ObservableProperty]
    public partial string ReadsText { get; set; } = string.Empty;

    /// <summary>
    /// What it is asking for right now.
    /// </summary>
    /// <remarks>
    /// On the row rather than only inside the editor, so the page answers "which of these is
    /// actually doing anything" without opening seven of them in turn.
    /// </remarks>
    [ObservableProperty]
    public partial string OutputText { get; set; } = "—";

    /// <summary>The kind and the fans it drives, on one line under the name.</summary>
    public string Summary => Users switch
    {
        0 => $"{Kind} · not used",
        1 => $"{Kind} · drives 1 fan",
        var count => $"{Kind} · drives {count} fans",
    };

    /// <summary>Keeps the line under the name current when the count changes.</summary>
    partial void OnUsersChanged(int value) => OnPropertyChanged(nameof(Summary));
}

/// <summary>
/// Fan curves, their inputs, and which controls they drive.
/// </summary>
/// <remarks>
/// <para>
/// The page is the list; a curve is edited in a panel that opens over it, the way a computed sensor
/// is. That is not only for consistency. The editor used to sit beside the list and be driven by
/// the selection, which meant a curve added and never saved had to be taken back out of the list
/// when the selection moved off it — and removing the item a list is in the middle of changing
/// selection on makes WinUI ask for an index that no longer exists and close the window. Composing
/// in a panel means an unsaved curve is never in the list at all, so there is nothing to withdraw.
/// </para>
/// <para>
/// Nothing reaches a fan until the panel is accepted. Sending each change as it happens sounds more
/// responsive and means dragging a point across a canvas applies twenty intermediate configurations
/// to real hardware, most of them shapes the user was passing through rather than choosing.
/// </para>
/// </remarks>
public sealed partial class CurvesViewModel(EngineConnection connection, NotificationCenter notifications)
    : EnginePageViewModel(connection, notifications)
{
    private CurveEditorViewModel? _open;
    private CurveDefinition? _built;

    /// <inheritdoc />
    public override string Title => "Curves";

    /// <summary>Every curve in the configuration.</summary>
    public ObservableCollection<CurveListItemViewModel> Curves { get; } = [];

    /// <summary>
    /// How the page puts a curve's panel in front of the user, and what they answered.
    /// </summary>
    /// <remarks>
    /// Supplied by the view rather than reached for, because a dialog is a UI-framework thing and
    /// this project deliberately has none.
    /// </remarks>
    public Func<CurveEditorViewModel, Task<bool>>? Compose { get; set; }

    /// <summary>How the page asks the user to confirm deleting a curve.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Whether there is anything in the list.</summary>
    public bool IsEmpty => Curves.Count == 0;

    /// <summary>
    /// Makes a curve of one kind and offers it for editing.
    /// </summary>
    /// <remarks>
    /// Nothing is added to the configuration unless the panel is accepted, so a mis-click leaves
    /// nothing behind and nothing reaches a fan.
    /// </remarks>
    [RelayCommand]
    private async Task AddAsync(CurveEditorKind kind)
    {
        if (Snapshot is not { } snapshot || Compose is null)
        {
            return;
        }

        if (!await OpenAsync(Blank(kind, UnusedName(kind.ToString())), snapshot, isNew: true).ConfigureAwait(true))
        {
            return;
        }

        await SaveAsync(snapshot, _built!, $"Added '{_built!.Name}'.").ConfigureAwait(true);
    }

    /// <summary>Reopens an existing curve.</summary>
    [RelayCommand]
    private async Task EditAsync(CurveId id)
    {
        if (Snapshot is not { } snapshot
            || Compose is null
            || snapshot.Configuration.Curves.FirstOrDefault(curve => curve.Id == id) is not { } definition)
        {
            return;
        }

        if (!await OpenAsync(definition, snapshot, isNew: false).ConfigureAwait(true))
        {
            return;
        }

        await SaveAsync(snapshot, _built!, "Saved.").ConfigureAwait(true);
    }

    /// <summary>
    /// Deletes a curve, and unbinds anything that was using it.
    /// </summary>
    /// <remarks>
    /// The unbinding is the point. A control left pointing at a curve that no longer exists makes
    /// the whole configuration fail validation, so deleting one curve would otherwise refuse to
    /// save until the user had found every control that referenced it.
    /// </remarks>
    [RelayCommand]
    private async Task DeleteAsync(CurveId id)
    {
        if (Snapshot is not { } snapshot
            || Curves.FirstOrDefault(curve => curve.Id == id) is not { } item)
        {
            return;
        }

        if (Confirm is { } confirm
            && !await confirm($"Delete '{item.Name}'?", DeletionCost(item)).ConfigureAwait(true))
        {
            return;
        }

        var configuration = snapshot.Configuration with
        {
            Curves = [.. snapshot.Configuration.Curves.Where(curve => curve.Id != id)],
            Controls =
            [
                .. snapshot.Configuration.Controls.Select(binding =>
                    binding.CurveId == id ? binding with { CurveId = CurveId.None, Enabled = false } : binding),
            ],
        };

        await ApplyAsync(configuration, $"Deleted '{item.Name}'.").ConfigureAwait(true);
    }

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
        var users = snapshot.Configuration.Controls
            .GroupBy(binding => binding.CurveId)
            .ToDictionary(group => group.Key, group => group.Count());

        var sensors = snapshot.Sensors.ToDictionary(sensor => sensor.Id);

        Curves.Clear();

        foreach (var definition in snapshot.Configuration.Curves)
        {
            Curves.Add(new CurveListItemViewModel(definition)
            {
                Users = users.GetValueOrDefault(definition.Id),
                ReadsText = Describe(definition, sensors, snapshot.Configuration),
            });
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The live read-out is why this page follows ticks. It turns a curve from a form into
    /// something you can watch respond, which is the only way to tell a badly shaped curve from a
    /// well shaped one without waiting for the machine to get hot.
    /// </remarks>
    protected override void OnTick(TickSnapshot tick)
    {
        foreach (var row in Curves)
        {
            row.OutputText = "—";
        }

        foreach (var reading in tick.Curves)
        {
            foreach (var row in Curves)
            {
                if (row.Id == reading.Id)
                {
                    row.OutputText = reading.Output is { } duty ? $"{duty.Percent:0.#} %" : "—";
                    break;
                }
            }
        }

        if (_open is not { } editor)
        {
            return;
        }

        // The panel open over the page wants both the readings behind its picker and its own
        // output.
        editor.Apply(tick);

        foreach (var reading in tick.Curves)
        {
            if (reading.Id == editor.Id)
            {
                editor.LiveOutput = reading.Output?.Percent;
                return;
            }
        }

        // Not in the tick at all: a curve the engine has never seen. Saying "no output" is honest,
        // and saying nothing would leave another curve's number under a new one.
        editor.LiveOutput = null;
    }

    /// <summary>
    /// What deleting this curve costs, in the terms the user cares about.
    /// </summary>
    /// <remarks>
    /// The fans are the point. Deleting a curve switches off everything it was driving, and a
    /// confirmation that did not say so would be a speed bump rather than a warning.
    /// </remarks>
    internal static string DeletionCost(CurveListItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.Users switch
        {
            0 => "Nothing is using it, so nothing else changes.",
            1 => "The fan it drives will be switched off. This cannot be undone.",
            var count => $"The {count} fans it drives will be switched off. This cannot be undone.",
        };
    }

    /// <summary>
    /// What a curve reads, for the line under its name.
    /// </summary>
    /// <remarks>
    /// Named rather than left to the editor, because "which of my curves is watching the GPU" is a
    /// question this page ought to answer without opening every one of them in turn.
    /// </remarks>
    internal static string Describe(
        CurveDefinition curve,
        IReadOnlyDictionary<SensorId, SensorDescriptor> sensors,
        ImpellerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(sensors);
        ArgumentNullException.ThrowIfNull(configuration);

        return curve switch
        {
            FlatCurveDefinition flat => $"Always {flat.Duty.Percent:0.#} %",
            LinearCurveDefinition linear => Reads(linear.Source, sensors),
            GraphCurveDefinition graph => Reads(graph.Source, sensors),
            TriggerCurveDefinition trigger => Reads(trigger.Source, sensors),
            AutoCurveDefinition auto => Reads(auto.Source, sensors),
            MixCurveDefinition mix => mix.Sources.Count switch
            {
                0 => "No curves chosen yet",
                1 => "Combines 1 curve",
                var count => $"Combines {count} curves",
            },
            SyncCurveDefinition sync => Follows(sync, configuration),
            _ => string.Empty,
        };
    }

    private static string Reads(SensorId id, IReadOnlyDictionary<SensorId, SensorDescriptor> sensors) =>
        id.IsNone ? "No sensor chosen yet"
        : sensors.TryGetValue(id, out var sensor) ? $"Reads {sensor.DisplayName}"
        : "Reads a sensor that is not here";

    private static string Follows(SyncCurveDefinition sync, ImpellerConfiguration configuration) =>
        sync.SourceKind switch
        {
            SyncSourceKind.Curve =>
                configuration.Curves.FirstOrDefault(curve => curve.Id == sync.SourceCurve) is { } other
                    ? $"Follows '{other.Name}'"
                    : "Follows a curve that is not here",
            SyncSourceKind.Control => "Follows a fan",
            _ => "Nothing chosen yet",
        };

    /// <summary>Puts a curve's panel in front of the user and keeps what they built.</summary>
    private async Task<bool> OpenAsync(CurveDefinition definition, EngineSnapshot snapshot, bool isNew)
    {
        var editor = new CurveEditorViewModel(definition, OptionsFor(definition, snapshot)) { IsNew = isNew };

        // Held only while the panel is up, so ticks reach it. Cleared in the finally, because a
        // panel that was cancelled must not go on being fed readings.
        _open = editor;

        try
        {
            if (!await Compose!(editor).ConfigureAwait(true))
            {
                return false;
            }
        }
        finally
        {
            _open = null;
        }

        _built = editor.Build();
        return true;
    }

    /// <summary>Puts a built curve into the configuration, replacing it or adding it.</summary>
    private async Task SaveAsync(EngineSnapshot snapshot, CurveDefinition definition, string success)
    {
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

        await ApplyAsync(snapshot.Configuration with { Curves = [.. curves] }, success).ConfigureAwait(true);
    }

    /// <summary>
    /// Everything the open curve could point at.
    /// </summary>
    /// <remarks>
    /// Built fresh for each editor rather than kept and handed round: a mix ticks the boxes on
    /// these, so a shared set would carry one curve's choices into the next one opened.
    /// </remarks>
    private static CurveEditorOptions OptionsFor(CurveDefinition definition, EngineSnapshot snapshot)
    {
        var curves = snapshot.Configuration.Curves
            .Where(curve => curve.Id != definition.Id)
            .Select(curve => new CurveChoiceViewModel(curve.Id, curve.Name))
            .ToArray();

        var controls = snapshot.Controls
            .Select(control => new ControlChoice(control.Id, control.DisplayName))
            .ToArray();

        return new CurveEditorOptions(curves, controls) { Sensors = [.. snapshot.Sensors] };
    }

    /// <summary>A curve of the chosen kind, opened with values rather than an empty form.</summary>
    private static CurveDefinition Blank(CurveEditorKind kind, string name)
    {
        var id = CurveId.New();

        return kind switch
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
    }

    private async Task ApplyAsync(ImpellerConfiguration configuration, string success)
    {
        if (Connection.Engine is not { } engine)
        {
            Notify.Error("Not connected to the engine.", "Nothing was saved.");
            return;
        }

        try
        {
            var result = await engine.ApplyConfigurationAsync(configuration).ConfigureAwait(true);

            // Warnings are said as well as errors. A curve reading hardware that is not here right
            // now applies perfectly well and is still worth mentioning before the user walks away,
            // so the severity follows whether it applied rather than whether it was quiet.
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

                return;
            }

            if (result.Applied)
            {
                Notify.Success(success);
            }
            else
            {
                Notify.Error("The change was refused.", "Nothing was saved.");
            }
        }
        catch (Exception ex)
        {
            Notify.Error("The curve could not be saved.", ex);
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
