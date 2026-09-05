using CommunityToolkit.Mvvm.Input;
using Impeller.App.ViewModels.Engine;
using Impeller.App.ViewModels.Notifications;
using Impeller.App.ViewModels.Sensors;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels;

/// <summary>
/// Every sensor the engine can see, across all providers.
/// </summary>
/// <remarks>
/// Mostly a diagnostic page, and a useful one: "does the engine see my water temperature" is the
/// first question behind half of what goes wrong, and it is answerable here in one search.
/// </remarks>
public sealed partial class SensorsViewModel : EnginePageViewModel
{
    public SensorsViewModel(EngineConnection connection, NotificationCenter notifications)
        : base(connection, notifications) =>
        Tree.Rename = RenameAsync;

    /// <inheritdoc />
    public override string Title => "Sensors";

    /// <summary>The sensors, grouped by hardware and filtered by the search box.</summary>
    public SensorTreeViewModel Tree { get; } = new();

    /// <summary>The kinds of computed sensor that can be made, for the button's menu.</summary>
    public static IReadOnlyList<CustomSensorChoice> Kinds => CustomSensorEditorViewModel.Kinds;

    /// <summary>
    /// How the page puts a computed sensor's panel in front of the user, and what they answered.
    /// </summary>
    /// <remarks>
    /// Supplied by the view rather than reached for, exactly as the curve page's confirmation is:
    /// a dialog is a UI-framework thing and this project deliberately has none of those.
    /// </remarks>
    public Func<CustomSensorEditorViewModel, Task<bool>>? Compose { get; set; }

    /// <summary>How the page asks the user to confirm a deletion.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>
    /// Opens a blank panel for a new computed sensor, and saves it if the user fills it in.
    /// </summary>
    [RelayCommand]
    private async Task AddAsync(CustomSensorKind kind)
    {
        if (Snapshot is not { } snapshot || Compose is null)
        {
            return;
        }

        var editor = new CustomSensorEditorViewModel(
            new CustomSensorDefinition { Id = SensorId.New(), Kind = kind },
            snapshot.Sensors,
            isNew: true);

        if (!await Compose(editor).ConfigureAwait(true))
        {
            return;
        }

        var definition = editor.Build();

        await ApplyAsync(
            snapshot.Configuration with { CustomSensors = [.. snapshot.Configuration.CustomSensors, definition] },
            $"Added '{definition.Name}'.").ConfigureAwait(true);
    }

    /// <summary>Reopens an existing computed sensor for editing.</summary>
    [RelayCommand]
    private async Task EditAsync(SensorId id)
    {
        if (Snapshot is not { } snapshot
            || Compose is null
            || Find(snapshot, id) is not { } existing)
        {
            return;
        }

        var editor = new CustomSensorEditorViewModel(existing, snapshot.Sensors, isNew: false);

        if (!await Compose(editor).ConfigureAwait(true))
        {
            return;
        }

        var definitions = snapshot.Configuration.CustomSensors.ToArray();
        definitions[Array.FindIndex(definitions, sensor => sensor.Id == id)] = editor.Build();

        await ApplyAsync(
            snapshot.Configuration with { CustomSensors = [.. definitions] },
            "Saved.").ConfigureAwait(true);
    }

    /// <summary>
    /// Deletes a computed sensor, and stops anything reading it.
    /// </summary>
    /// <remarks>
    /// The unreading is the point, and it is the same argument curve deletion makes: a curve left
    /// pointing at a sensor that no longer exists is a defined state only because something took
    /// the trouble to define it. Cleared, the curve says "no sensor chosen yet", which is true and
    /// visible; left dangling it would read nothing and explain nothing.
    /// </remarks>
    [RelayCommand]
    private async Task DeleteAsync(SensorId id)
    {
        if (Snapshot is not { } snapshot || Find(snapshot, id) is not { } existing)
        {
            return;
        }

        var readers = snapshot.Configuration.Curves.Count(curve => Reads(curve, id))
            + snapshot.Configuration.CustomSensors.Count(
                sensor => sensor.Id != id && sensor.Sources.Contains(id));

        if (Confirm is not null
            && !await Confirm($"Delete '{existing.Name}'?", Cost(readers)).ConfigureAwait(true))
        {
            return;
        }

        var configuration = snapshot.Configuration with
        {
            CustomSensors = [.. snapshot.Configuration.CustomSensors.Where(sensor => sensor.Id != id)],
            Curves = [.. snapshot.Configuration.Curves.Select(curve => Unread(curve, id))],
        };

        await ApplyAsync(configuration, $"Deleted '{existing.Name}'.").ConfigureAwait(true);
    }

    /// <summary>
    /// Whether a curve reads this sensor.
    /// </summary>
    /// <remarks>
    /// Asked of each shape in turn because only four of the seven read a sensor at all: a flat
    /// curve is a constant, a mix combines other curves, and a sync follows another fan.
    /// </remarks>
    internal static bool Reads(CurveDefinition curve, SensorId id) => curve switch
    {
        LinearCurveDefinition linear => linear.Source == id,
        GraphCurveDefinition graph => graph.Source == id,
        TriggerCurveDefinition trigger => trigger.Source == id,
        AutoCurveDefinition auto => auto.Source == id,
        _ => false,
    };

    /// <summary>Leaves a curve with no sensor chosen, rather than one that no longer exists.</summary>
    internal static CurveDefinition Unread(CurveDefinition curve, SensorId id) => curve switch
    {
        LinearCurveDefinition linear when linear.Source == id => linear with { Source = SensorId.None },
        GraphCurveDefinition graph when graph.Source == id => graph with { Source = SensorId.None },
        TriggerCurveDefinition trigger when trigger.Source == id => trigger with { Source = SensorId.None },
        AutoCurveDefinition auto when auto.Source == id => auto with { Source = SensorId.None },
        _ => curve,
    };

    /// <summary>What deleting it costs, said before it happens rather than discovered after.</summary>
    internal static string Cost(int readers) => readers switch
    {
        0 => "Nothing reads it, so nothing else changes.",
        1 => "One curve reads it, and will be left with no sensor chosen.",
        var count => $"{count} things read it, and will be left with no sensor chosen.",
    };

    private static CustomSensorDefinition? Find(EngineSnapshot snapshot, SensorId id) =>
        snapshot.Configuration.CustomSensors.FirstOrDefault(sensor => sensor.Id == id);

    /// <summary>Sends a changed configuration to the engine and says how it went.</summary>
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

            if (result.Validation.Issues.Count > 0)
            {
                var detail = string.Join(" ", result.Validation.Issues.Select(issue => issue.Message));

                if (result.Applied)
                {
                    Notify.Warn("Saved, with something worth knowing.", detail);
                }
                else
                {
                    Notify.Error("The sensor was refused.", detail);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notify.Error("The change could not be saved.", ex.Message);
        }
    }

    /// <summary>
    /// Gives a sensor the user's own name.
    /// </summary>
    /// <remarks>
    /// The engine stores it and announces the change, so the tree reloads from the fresh snapshot
    /// rather than this page guessing at what it now looks like.
    /// </remarks>
    private async Task RenameAsync(SensorId id, string? name)
    {
        if (Connection.Engine is { } engine)
        {
            try
            {
                await engine.RenameAsync(id, name).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // The reconnect loop reports a lost connection properly; a second account of the
                // same fact on this page would help nobody.
            }
        }
    }

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot) => Tree.Load(snapshot.Sensors);

    /// <inheritdoc />
    protected override void OnTick(TickSnapshot tick) => Tree.Apply(tick);
}
