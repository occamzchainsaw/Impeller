using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Impeller.App.ViewModels.Engine;
using Impeller.Core.Abstractions;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels;

/// <summary>One control as the dashboard shows it, updated in place on every tick.</summary>
public sealed partial class ControlTileViewModel(SensorId id, string name) : ObservableObject
{
    /// <summary>Which control this is.</summary>
    public SensorId Id { get; } = id;

    /// <summary>What the hardware calls it.</summary>
    public string Name { get; } = name;

    /// <summary>The duty currently standing at it.</summary>
    [ObservableProperty]
    public partial string Duty { get; private set; } = "—";

    /// <summary>What is driving it right now.</summary>
    [ObservableProperty]
    public partial string Owner { get; private set; } = "Curve";

    /// <summary>Takes a reading from a tick.</summary>
    public void Update(ControlReading reading)
    {
        Duty = reading.CommandedDuty?.ToString() ?? "not driven";
        Owner = reading.Owner.ToString();
    }
}

/// <summary>
/// The fan and sensor overview: the page the app opens on and the one people leave open.
/// </summary>
/// <remarks>
/// Reads from the engine and never touches hardware itself. The shell holds no engine types at
/// all — everything here arrives over the channel, which is what lets the engine keep running when
/// this window is closed, and what would let a different front end replace it.
/// </remarks>
public sealed partial class DashboardViewModel : PageViewModel, IDisposable
{
    private readonly EngineConnection _connection;

    public DashboardViewModel(EngineConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;

        _connection.PropertyChanged += OnConnectionChanged;
        _connection.SnapshotReceived += OnSnapshot;
        _connection.Ticked += OnTicked;

        EngineStatus = _connection.StatusMessage;

        // A page opened after the connection was already established gets the state it missed.
        if (_connection.Snapshot is { } snapshot)
        {
            Populate(snapshot);
        }
    }

    /// <inheritdoc />
    public override string Title => "Dashboard";

    /// <summary>A plain statement of where the engine stands, whether or not that is good news.</summary>
    [ObservableProperty]
    public partial string EngineStatus { get; private set; } = "Not connected to the engine service.";

    /// <summary>Whether the engine is reachable, so the page can say so rather than showing nothing.</summary>
    [ObservableProperty]
    public partial bool IsConnected { get; private set; }

    /// <summary>How many sensors the engine can see.</summary>
    [ObservableProperty]
    public partial int SensorCount { get; private set; }

    /// <summary>The highest reading currently reported, formatted for display.</summary>
    [ObservableProperty]
    public partial string HottestTemperature { get; private set; } = "—";

    /// <summary>Every writable control, with what is driving it.</summary>
    public ObservableCollection<ControlTileViewModel> Controls { get; } = [];

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.PropertyChanged -= OnConnectionChanged;
        _connection.SnapshotReceived -= OnSnapshot;
        _connection.Ticked -= OnTicked;
    }

    private void OnConnectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        EngineStatus = _connection.StatusMessage;
        IsConnected = _connection.State == EngineConnectionState.Connected;
    }

    private void OnSnapshot(object? sender, EngineSnapshot snapshot) => Populate(snapshot);

    private void Populate(EngineSnapshot snapshot)
    {
        SensorCount = snapshot.Sensors.Count;
        IsConnected = true;

        Controls.Clear();

        foreach (var control in snapshot.Controls)
        {
            var tile = new ControlTileViewModel(control.Id, control.Name);
            tile.Update(new ControlReading(control.Id, control.CommandedDuty, control.Owner));
            Controls.Add(tile);
        }
    }

    private void OnTicked(object? sender, TickSnapshot tick)
    {
        // Matched by id and updated in place rather than rebuilt, so the list does not flicker once
        // a second and anything the user has selected survives the update.
        foreach (var reading in tick.Controls)
        {
            foreach (var tile in Controls)
            {
                if (tile.Id == reading.Id)
                {
                    tile.Update(reading);
                }
            }
        }

        var readings = tick.Sensors
            .Where(sensor => sensor.Value is not null)
            .Select(sensor => sensor.Value!.Value)
            .ToList();

        HottestTemperature = readings.Count == 0 ? "—" : $"{readings.Max():0.#}";
    }
}
