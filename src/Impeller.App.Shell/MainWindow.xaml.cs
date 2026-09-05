using System.Collections.ObjectModel;
using Impeller.App.Shell.Pages;
using Impeller.App.ViewModels;
using Impeller.App.ViewModels.Notifications;
using Impeller.App.ViewModels.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Impeller.App.Shell;

/// <summary>
/// The main window: a title bar, a navigation frame, and a status strip.
/// </summary>
/// <remarks>
/// <para>
/// Navigation is resolved from a tag on each item rather than by holding page types in code,
/// so adding a section is a XAML change plus one dictionary entry.
/// </para>
/// <para>
/// The window also owns the two things that must outlive a page: the status strip, because
/// connection state is identical everywhere and belongs outside the frame, and the toasts, because
/// a message about something that just happened has to survive navigating away from the page that
/// caused it.
/// </para>
/// </remarks>
public sealed partial class MainWindow : Window
{
    /// <summary>How many toasts are on screen at once before the oldest gives way.</summary>
    /// <remarks>
    /// Three. A stack tall enough to cover the page is not a notification, it is an obstruction —
    /// and everything pushed off it is still one click away behind the bell.
    /// </remarks>
    private const int MaxToasts = 3;

    private static readonly Dictionary<string, Type> Pages = new(StringComparer.Ordinal)
    {
        ["dashboard"] = typeof(DashboardPage),
        ["curves"] = typeof(CurvesPage),
        ["sensors"] = typeof(SensorsPage),
        ["plugins"] = typeof(PluginsPage),
        ["settings"] = typeof(SettingsPage),
    };

    private readonly Dictionary<ShellNotification, DispatcherQueueTimer> _dismissals = [];

    public MainWindow()
    {
        Notifications = App.GetService<NotificationCenter>();
        Status = App.GetService<ShellStatusViewModel>();

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Before the window is activated, so it opens where it belongs instead of appearing at
        // whatever size Windows picked and then jumping.
        // Not held: it follows the window through its own events, and outliving this constructor is
        // the whole job. Closing the window hides it to the tray, so there is nothing to let go of.
        new WindowPlacementMemory(this, new WindowPlacementStore(ShellState.WindowFile)).Restore();

        Notifications.Posted += OnPosted;
        Closed += (_, _) => Notifications.Posted -= OnPosted;
    }

    /// <summary>Everything the shell has had to say, and the bell that shows it.</summary>
    public NotificationCenter Notifications { get; }

    /// <summary>Where the engine stands, for the strip at the foot.</summary>
    public ShellStatusViewModel Status { get; }

    /// <summary>The toasts currently on screen, newest first.</summary>
    public ObservableCollection<ShellNotification> Toasts { get; } = [];

    /// <summary>Shown when the condition holds.</summary>
    public static Visibility When(bool condition) =>
        condition ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shown when there is a number worth showing.</summary>
    public static Visibility WhenAny(int count) =>
        count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shown only when there is something to say.</summary>
    public static Visibility WhenText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Which configuration is driving the fans, spelled out.</summary>
    public static string RunningLabel(string name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : $"Running '{name}'";

    /// <summary>The shell's own severity as the one an InfoBar understands.</summary>
    public static InfoBarSeverity SeverityFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Success => InfoBarSeverity.Success,
        NotificationSeverity.Warning => InfoBarSeverity.Warning,
        NotificationSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    /// <summary>The history list's icon, which carries the severity where there is no InfoBar to.</summary>
    public static string GlyphFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Success => "\uE73E",
        NotificationSeverity.Warning => "\uE7BA",
        NotificationSeverity.Error => "\uE783",
        _ => "\uE946",
    };

    /// <summary>That icon's colour, taken from the system palette rather than invented.</summary>
    public static Brush BrushFor(NotificationSeverity severity) => Resource(severity switch
    {
        NotificationSeverity.Success => "SystemFillColorSuccessBrush",
        NotificationSeverity.Warning => "SystemFillColorCautionBrush",
        NotificationSeverity.Error => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorNeutralBrush",
    });

    /// <summary>
    /// The status dot: green while readings are arriving, amber when they have stopped, red when
    /// there is no engine at all.
    /// </summary>
    /// <remarks>
    /// The amber case is the one worth having. A shell still attached to an engine that has stopped
    /// ticking looks completely normal otherwise, and this is the only thing on screen that says so.
    /// </remarks>
    public static Brush DotFor(bool connected, bool stale) => Resource(
        !connected ? "SystemFillColorCriticalBrush"
        : stale ? "SystemFillColorCautionBrush"
        : "SystemFillColorSuccessBrush");

    private static Brush Resource(string key) =>
        Application.Current.Resources[key] as Brush ?? new SolidColorBrush();

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
        }
    }

    private void NavView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        // An unrecognised tag means a XAML item with no matching page. Ignoring it keeps a typo
        // from taking down the window; the section simply does not navigate.
        if (!Pages.TryGetValue(tag, out var page) || NavFrame.CurrentSourcePageType == page)
        {
            return;
        }

        NavFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }

    /// <summary>
    /// Puts a notification on screen for as long as it is worth reading.
    /// </summary>
    /// <remarks>
    /// A repeat arrives as the same instance it repeats, which is what lets it restart the timer of
    /// the toast already showing rather than stacking a second identical card.
    /// </remarks>
    private void OnPosted(object? sender, ShellNotification notification)
    {
        if (!_dismissals.TryGetValue(notification, out var timer))
        {
            timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = false;
            timer.Tick += (_, _) => Dismiss(notification);

            _dismissals[notification] = timer;
            Toasts.Insert(0, notification);

            while (Toasts.Count > MaxToasts)
            {
                Dismiss(Toasts[^1]);
            }
        }

        timer.Interval = LifetimeFor(notification.Severity);
        timer.Start();
    }

    /// <summary>
    /// How long a toast stays.
    /// </summary>
    /// <remarks>
    /// A failure is given longer than a confirmation, and none of them stays for ever. A toast that
    /// waited to be dismissed would be the InfoBar this replaced, in a worse position — and the
    /// bell keeps every one of them anyway.
    /// </remarks>
    private static TimeSpan LifetimeFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => TimeSpan.FromSeconds(12),
        NotificationSeverity.Warning => TimeSpan.FromSeconds(9),
        _ => TimeSpan.FromSeconds(5),
    };

    private void Dismiss(ShellNotification notification)
    {
        if (_dismissals.Remove(notification, out var timer))
        {
            timer.Stop();
        }

        Toasts.Remove(notification);
    }

    private void OnToastClosed(InfoBar sender, object args)
    {
        if (sender.DataContext is ShellNotification notification)
        {
            Dismiss(notification);
        }
    }

    /// <summary>Opening the bell is reading them, so the badge clears.</summary>
    private void OnHistoryOpening(object sender, object e) => Notifications.MarkAllRead();

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        Notifications.Clear();
        HistoryButton.Flyout?.Hide();
    }
}
