using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Impeller.App.ViewModels.Engine;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels;

/// <summary>
/// Where the engine stands, for the strip at the foot of the window.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of the old <c>InfoBar</c> that was never news. Connection state, the loaded
/// configuration and how long ago the engine last spoke are <em>conditions</em>: they are true
/// continuously, they are as relevant on the Curves page as on the Dashboard, and none of them is
/// an event worth a toast. So they live once, outside the navigation frame, where they neither
/// scroll away nor repeat on every page.
/// </para>
/// <para>
/// The tick age is the part that earns the strip. A shell that has lost the engine says so loudly
/// already; a shell still connected to an engine that has stopped ticking looks completely normal,
/// and the only visible difference is a number that stops changing.
/// </para>
/// </remarks>
public sealed partial class ShellStatusViewModel : ObservableObject, IDisposable
{
    /// <summary>How stale a reading has to be before the strip says so.</summary>
    /// <remarks>
    /// The engine ticks once a second, so anything past five is several missed in a row rather than
    /// one late one — slow enough not to flicker on an ordinary hiccup.
    /// </remarks>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    private readonly EngineConnection _connection;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;

    private DateTimeOffset? _lastTickAt;
    private bool _disposed;

    public ShellStatusViewModel(EngineConnection connection, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _time = time ?? TimeProvider.System;

        connection.PropertyChanged += OnConnectionChanged;
        connection.Ticked += OnTicked;
        connection.SnapshotReceived += OnSnapshot;

        if (connection.Snapshot is { } snapshot)
        {
            ConfigurationName = snapshot.ConfigurationName;
        }

        // Ticked once a second whether or not anything arrives, because an age that only updates
        // when a reading lands is exactly the age that cannot report a reading not landing.
        _timer = _time.CreateTimer(
            _ => _connection.Dispatcher.Post(Refresh),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Where the engine stands, in two or three words.
    /// </summary>
    /// <remarks>
    /// The connection's own message is a whole sentence carrying the sensor and control counts and
    /// the configuration name, which is right for a bug report and far too much for a strip that
    /// also names the configuration in the next slot along. The sentence is still there, as the
    /// tooltip.
    /// </remarks>
    public string StateText => _connection.State switch
    {
        EngineConnectionState.Connected => "Connected",
        EngineConnectionState.Connecting => "Connecting",
        EngineConnectionState.NotInstalled => "Engine not installed",
        _ => "Engine not running",
    };

    /// <summary>The whole sentence, for the tooltip on the line above.</summary>
    public string ConnectionText => _connection.StatusMessage;

    /// <summary>Whether the engine is reachable.</summary>
    public bool IsConnected => _connection.State == EngineConnectionState.Connected;

    /// <summary>Which configuration is driving the fans.</summary>
    [ObservableProperty]
    public partial string ConfigurationName { get; private set; } = string.Empty;

    /// <summary>Whether there is a configuration name worth a slot in the strip.</summary>
    public bool HasConfiguration => !string.IsNullOrWhiteSpace(ConfigurationName);

    /// <summary>How long since the engine last reported, or null when it never has.</summary>
    internal TimeSpan? SinceLastReading =>
        _lastTickAt is { } last ? _time.GetUtcNow() - last : null;

    /// <summary>How long since the engine last reported, in words.</summary>
    /// <remarks>
    /// Empty when there is no engine, because "last reading 4 h ago" underneath "not connected to
    /// the engine service" is one fault described twice, and the second description reads like a
    /// separate one.
    /// </remarks>
    public string TickAgeText => IsConnected ? Describe(SinceLastReading) : string.Empty;

    /// <summary>
    /// Whether the engine is connected but has stopped reporting.
    /// </summary>
    /// <remarks>
    /// The one state the connection itself cannot describe, and the reason the age is on screen at
    /// all: a live pipe attached to an engine that is no longer ticking.
    /// </remarks>
    public bool IsStale => IsConnected && IsStaleAge(SinceLastReading);

    /// <summary>An age in words.</summary>
    /// <remarks>
    /// Not a number while readings are arriving. The engine ticks once a second, so an exact age
    /// would read "1 s ago" for ever and teach the user to ignore the one thing on screen that can
    /// report it having stopped.
    /// </remarks>
    internal static string Describe(TimeSpan? age) => age switch
    {
        null => "waiting for the first reading",
        { TotalSeconds: < 3 } => "updating",
        { TotalSeconds: < 60 } => $"last reading {age.Value.TotalSeconds:F0} s ago",
        { TotalMinutes: < 60 } => $"last reading {age.Value.TotalMinutes:F0} min ago",
        _ => "no readings for over an hour",
    };

    /// <summary>Whether an age is long enough to be a fault rather than a late tick.</summary>
    internal static bool IsStaleAge(TimeSpan? age) => age is not { } value || value > StaleAfter;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _connection.PropertyChanged -= OnConnectionChanged;
        _connection.Ticked -= OnTicked;
        _connection.SnapshotReceived -= OnSnapshot;

        _timer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Re-reads the clock. Called on the timer, and after anything that could change it.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(TickAgeText));
        OnPropertyChanged(nameof(IsStale));
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(IsConnected));
        Refresh();
    }

    private void OnTicked(object? sender, TickSnapshot tick) => NoteTick();

    /// <summary>Records that the engine has just spoken.</summary>
    /// <remarks>
    /// The local clock, not the engine's. The question this answers is how long since we heard from
    /// it, and a timestamp minted on the other side cannot answer that.
    /// </remarks>
    internal void NoteTick()
    {
        _lastTickAt = _time.GetUtcNow();
        Refresh();
    }

    private void OnSnapshot(object? sender, EngineSnapshot snapshot) =>
        ConfigurationName = snapshot.ConfigurationName;

    partial void OnConfigurationNameChanged(string value) => OnPropertyChanged(nameof(HasConfiguration));
}
