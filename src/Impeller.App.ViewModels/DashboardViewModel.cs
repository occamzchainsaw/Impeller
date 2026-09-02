using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Impeller.App.ViewModels.Controls;
using Impeller.App.ViewModels.Engine;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels;

/// <summary>A curve, as an entry in the picker on a control's card.</summary>
/// <param name="Id">Which curve.</param>
/// <param name="Name">What the user calls it.</param>
public readonly record struct CurveChoice(CurveId Id, string Name)
{
    /// <summary>The entry meaning "no curve", which is how a control is left alone.</summary>
    public static CurveChoice None => new(CurveId.None, "No curve");

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// The fan overview: the page the app opens on and the one people leave open.
/// </summary>
/// <remarks>
/// Reads from the engine and never touches hardware itself. The shell holds no engine types at
/// all — everything here arrives over the channel, which is what lets the engine keep running when
/// this window is closed, and what would let a different front end replace it.
/// </remarks>
public sealed partial class DashboardViewModel(EngineConnection connection)
    : EnginePageViewModel(connection)
{
    private readonly Dictionary<SensorId, ControlCardViewModel> _cards = [];
    private readonly Dictionary<SensorId, SensorId> _tachometers = [];

    /// <inheritdoc />
    public override string Title => "Dashboard";

    /// <summary>How many sensors the engine can see.</summary>
    [ObservableProperty]
    public partial int SensorCount { get; private set; }

    /// <summary>The hottest temperature currently reported, formatted for display.</summary>
    [ObservableProperty]
    public partial string HottestTemperature { get; private set; } = "—";

    /// <summary>Whatever the last edit from this page turned up, or null.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>Every writable control, with what is driving it.</summary>
    public ObservableCollection<ControlCardViewModel> Controls { get; } = [];

    /// <summary>The curves a control can be pointed at.</summary>
    public ObservableCollection<CurveChoice> Curves { get; } = [];

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
        SensorCount = snapshot.Sensors.Count;

        Curves.Clear();
        Curves.Add(CurveChoice.None);

        foreach (var curve in snapshot.Configuration.Curves)
        {
            Curves.Add(new CurveChoice(curve.Id, curve.Name));
        }

        _tachometers.Clear();

        foreach (var binding in snapshot.Configuration.Controls)
        {
            if (!binding.PairedFanSensorId.IsNone)
            {
                _tachometers[binding.ControlId] = binding.PairedFanSensorId;
            }
        }

        Rebuild(snapshot);
    }

    /// <inheritdoc />
    protected override void OnTick(TickSnapshot tick)
    {
        // Matched by id and written into the existing cards rather than rebuilt, so the list does
        // not flicker once a second and whatever the user has open stays open.
        var speeds = tick.Sensors.ToDictionary(reading => reading.Id, reading => reading.Value);

        foreach (var reading in tick.Controls)
        {
            if (!_cards.TryGetValue(reading.Id, out var card))
            {
                continue;
            }

            var rpm = _tachometers.TryGetValue(reading.Id, out var tachometer)
                ? speeds.GetValueOrDefault(tachometer)
                : null;

            card.Apply(reading, rpm);
        }

        HottestTemperature = Hottest(tick);
    }

    /// <summary>
    /// Rebuilds the list of cards, keeping the ones that are still here.
    /// </summary>
    /// <remarks>
    /// Reused rather than recreated. A card holds the position of its manual slider and whatever
    /// went wrong the last time it was pressed, and throwing that away every time a configuration
    /// is saved would take the user's place from under them mid-adjustment.
    /// </remarks>
    private void Rebuild(EngineSnapshot snapshot)
    {
        var bindings = snapshot.Configuration.Controls.ToDictionary(binding => binding.ControlId);
        var present = new HashSet<SensorId>();

        Controls.Clear();

        foreach (var descriptor in snapshot.Controls)
        {
            present.Add(descriptor.Id);

            var binding = bindings.GetValueOrDefault(descriptor.Id)
                ?? new ControlBindingDefinition { ControlId = descriptor.Id };

            var curveName = NameOf(binding.CurveId);

            if (_cards.TryGetValue(descriptor.Id, out var existing))
            {
                existing.Rebind(binding, curveName);
                existing.Apply(
                    new ControlReading(descriptor.Id, descriptor.CommandedDuty, descriptor.Owner),
                    null);

                Controls.Add(existing);
                continue;
            }

            var card = new ControlCardViewModel(descriptor, binding, curveName, Connection, SaveAsync);
            _cards[descriptor.Id] = card;
            Controls.Add(card);
        }

        // A control the engine no longer reports is dropped rather than left as a stale card for a
        // fan that is not there.
        foreach (var id in _cards.Keys.Where(id => !present.Contains(id)).ToList())
        {
            _cards.Remove(id);
        }
    }

    /// <summary>
    /// Sends one changed binding as a whole configuration.
    /// </summary>
    /// <remarks>
    /// All or nothing, because that is what the engine offers and what makes an edit safe: a
    /// configuration with errors never reaches the tick loop, so a bad change leaves the fans on
    /// the last good one rather than half-switching into a broken state.
    /// </remarks>
    private async Task SaveAsync(ControlBindingDefinition binding)
    {
        if (Connection.Engine is not { } engine || Snapshot is not { } snapshot)
        {
            Problem = "Not connected to the engine.";
            return;
        }

        var controls = snapshot.Configuration.Controls.ToArray();
        var index = Array.FindIndex(controls, control => control.ControlId == binding.ControlId);

        if (index < 0)
        {
            controls = [.. controls, binding];
        }
        else
        {
            controls[index] = binding;
        }

        var configuration = snapshot.Configuration with { Controls = [.. controls] };

        try
        {
            var result = await engine.ApplyConfigurationAsync(configuration).ConfigureAwait(true);

            Problem = result.Applied
                ? null
                : string.Join(" ", result.Validation.Issues.Select(issue => issue.Message));
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
        }
    }

    private string NameOf(CurveId id)
    {
        if (id.IsNone)
        {
            return "No curve";
        }

        foreach (var curve in Curves)
        {
            if (curve.Id == id)
            {
                return curve.Name;
            }
        }

        // The configuration names a curve that is not in it. The validator reports this as an
        // error, so the card says so rather than showing a blank where a name should be.
        return "Missing curve";
    }

    /// <summary>
    /// The hottest temperature this tick.
    /// </summary>
    /// <remarks>
    /// Temperatures only. A maximum across every reading would report a fan's RPM as the hottest
    /// thing in the machine, which is both wrong and briefly alarming.
    /// </remarks>
    private string Hottest(TickSnapshot tick)
    {
        if (Snapshot is not { } snapshot)
        {
            return "—";
        }

        var temperatures = snapshot.Sensors
            .Where(sensor => sensor.Kind == SensorKind.Temperature)
            .Select(sensor => sensor.Id)
            .ToHashSet();

        var readings = tick.Sensors
            .Where(reading => reading.Value is not null && temperatures.Contains(reading.Id))
            .Select(reading => reading.Value!.Value)
            .ToList();

        return readings.Count == 0 ? "—" : $"{readings.Max():0.#} °C";
    }
}
