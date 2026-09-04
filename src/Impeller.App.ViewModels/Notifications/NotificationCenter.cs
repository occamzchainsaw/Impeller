using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Impeller.App.ViewModels.Notifications;

/// <summary>
/// Everything the shell has to say, in one place, kept for as long as it is worth reading.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the per-page <c>InfoBar</c>. An always-open bar is status, not news: it cannot say
/// a second thing without losing the first, it says nothing at all when the user is on another
/// page, and it takes a strip of every page for a line that is usually empty. What a fan controller
/// actually needs to report — a save refused, a claim taken, a plugin approved — is transient, and
/// the question it has to answer afterwards is "what was that message I just missed".
/// </para>
/// <para>
/// So: a toast when it happens, and a history for after. Fifty entries, which is far more than
/// anybody will read and small enough never to matter.
/// </para>
/// <para>
/// Nothing here is a substitute for the status strip. Connection state and the loaded configuration
/// are conditions rather than events, and posting a toast every time the engine reconnects would be
/// exactly the noise this is meant to remove.
/// </para>
/// </remarks>
public sealed partial class NotificationCenter : ObservableObject
{
    /// <summary>How many entries are kept.</summary>
    public const int Capacity = 50;

    /// <summary>
    /// How long an identical message is folded into the one before it.
    /// </summary>
    /// <remarks>
    /// The engine ticks once a second and several failures repeat with it. Without this, one
    /// stuck condition fills the whole history and pushes out everything that was worth keeping.
    /// </remarks>
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(20);

    private readonly TimeProvider _time;

    public NotificationCenter(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Where entries are added and events raised. Set by the shell at startup.</summary>
    /// <remarks>
    /// A view model can post from wherever it happens to be — a continuation on a transport thread,
    /// most often — and the collection below is bound to a list that throws if it is touched from
    /// anywhere but the UI thread. Marshalled once, here, rather than at every call site.
    /// </remarks>
    public IUiDispatcher Dispatcher { get; set; } = ImmediateDispatcher.Instance;

    /// <summary>The history, newest first.</summary>
    public ObservableCollection<ShellNotification> History { get; } = [];

    /// <summary>How many have arrived since the user last looked.</summary>
    public int UnreadCount => _unread;

    /// <summary>Whether there is anything at all to show.</summary>
    public bool IsEmpty => History.Count == 0;

    private int _unread;

    /// <summary>Raised for each post, on the UI thread, for whatever shows toasts.</summary>
    /// <remarks>
    /// Carries the entry rather than a copy, so a repeat arrives as the same instance a toast may
    /// already be showing — which is what lets the toast reset its own timer instead of stacking.
    /// </remarks>
    public event EventHandler<ShellNotification>? Posted;

    /// <summary>Says that something the user asked for happened.</summary>
    public void Success(string title, string? message = null) =>
        Post(NotificationSeverity.Success, title, message);

    /// <summary>Says something worth knowing that is not a problem.</summary>
    public void Inform(string title, string? message = null) =>
        Post(NotificationSeverity.Informational, title, message);

    /// <summary>Says it went through, but not the way they expected.</summary>
    public void Warn(string title, string? message = null) =>
        Post(NotificationSeverity.Warning, title, message);

    /// <summary>Says it did not go through.</summary>
    public void Error(string title, string? message = null) =>
        Post(NotificationSeverity.Error, title, message);

    /// <summary>Reports a caught exception without letting its type reach the user.</summary>
    /// <remarks>
    /// The message alone. A stack trace belongs in the log, and a user reading "the operation was
    /// cancelled" learns more than one reading a type name.
    /// </remarks>
    public void Error(string title, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Post(NotificationSeverity.Error, title, exception.Message);
    }

    /// <summary>Records something that happened.</summary>
    public void Post(NotificationSeverity severity, string title, string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        Dispatcher.Post(() => Add(severity, title, message));
    }

    /// <summary>The user has seen them.</summary>
    public void MarkAllRead()
    {
        if (_unread == 0)
        {
            return;
        }

        _unread = 0;
        OnPropertyChanged(nameof(UnreadCount));
    }

    /// <summary>Throws the history away.</summary>
    public void Clear()
    {
        History.Clear();
        _unread = 0;

        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Add(NotificationSeverity severity, string title, string? message)
    {
        var now = _time.GetUtcNow();

        // Folded into the newest entry only, not searched for anywhere in the list. Two conditions
        // alternating are two things happening, and collapsing them would hide that.
        if (History.Count > 0
            && History[0] is { } newest
            && newest.Severity == severity
            && string.Equals(newest.Title, title, StringComparison.Ordinal)
            && string.Equals(newest.Message, message, StringComparison.Ordinal)
            && now - newest.At <= RepeatWindow)
        {
            newest.Repeats++;
            newest.At = now;

            Posted?.Invoke(this, newest);
            return;
        }

        var entry = new ShellNotification(severity, title, message, now);

        History.Insert(0, entry);

        while (History.Count > Capacity)
        {
            History.RemoveAt(History.Count - 1);
        }

        _unread++;

        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(IsEmpty));

        Posted?.Invoke(this, entry);
    }
}
