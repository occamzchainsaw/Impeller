using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Impeller.App.ViewModels.Engine;
using Impeller.App.ViewModels.Notifications;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels;

/// <summary>
/// Base for a page that watches the engine.
/// </summary>
/// <remarks>
/// Every page does the same four things — attach to the connection, take the snapshot it may have
/// missed, follow ticks, and detach when it goes away — and getting the last of those wrong leaks a
/// page into the connection's event list for the life of the window. Once, here, rather than five
/// times.
/// </remarks>
public abstract partial class EnginePageViewModel : PageViewModel, IDisposable
{
    private bool _disposed;

    protected EnginePageViewModel(EngineConnection connection, NotificationCenter notifications)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(notifications);

        Connection = connection;
        Notify = notifications;

        connection.PropertyChanged += OnConnectionPropertyChanged;
        connection.SnapshotReceived += OnSnapshotReceived;
        connection.Ticked += OnTickReceived;
        connection.ConfigurationChanged += OnConfigurationResult;

        IsConnected = connection.State == EngineConnectionState.Connected;
    }

    /// <summary>The shell's end of the channel.</summary>
    protected EngineConnection Connection { get; }

    /// <summary>
    /// Where a page says what just happened.
    /// </summary>
    /// <remarks>
    /// Every page used to carry its own <c>InfoBar</c> and a <c>Problem</c> string behind it, which
    /// meant a failure was only visible while the user stayed on the page that caused it and only
    /// until the next one overwrote it. One centre for the window instead, so a message outlives
    /// both the page and the moment.
    /// </remarks>
    protected NotificationCenter Notify { get; }

    /// <summary>The most recent full snapshot, or null before the first connection.</summary>
    protected EngineSnapshot? Snapshot { get; private set; }

    /// <summary>Whether the engine is reachable, so a page can say so rather than showing nothing.</summary>
    [ObservableProperty]
    public partial bool IsConnected { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// A page opened after the connection was already up gets the state it missed, rather than
    /// sitting empty until the next event happens to arrive.
    /// </remarks>
    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Connection.Snapshot is { } snapshot)
        {
            Snapshot = snapshot;
            OnSnapshot(snapshot);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Connection.PropertyChanged -= OnConnectionPropertyChanged;
        Connection.SnapshotReceived -= OnSnapshotReceived;
        Connection.Ticked -= OnTickReceived;
        Connection.ConfigurationChanged -= OnConfigurationResult;

        OnDisposing();
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases anything a derived page holds. The default does nothing.</summary>
    protected virtual void OnDisposing()
    {
    }

    /// <summary>A fresh snapshot arrived. The default does nothing.</summary>
    protected virtual void OnSnapshot(EngineSnapshot snapshot)
    {
    }

    /// <summary>A tick arrived. The default does nothing.</summary>
    protected virtual void OnTick(TickSnapshot tick)
    {
    }

    /// <summary>The configuration in force changed, from any source. The default does nothing.</summary>
    protected virtual void OnConfigurationChanged(ConfigurationResult result)
    {
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        IsConnected = Connection.State == EngineConnectionState.Connected;
    }

    private void OnSnapshotReceived(object? sender, EngineSnapshot snapshot)
    {
        Snapshot = snapshot;
        OnSnapshot(snapshot);
    }

    private void OnTickReceived(object? sender, TickSnapshot tick) => OnTick(tick);

    private void OnConfigurationResult(object? sender, ConfigurationResult result) =>
        OnConfigurationChanged(result);
}
