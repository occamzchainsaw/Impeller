using H.NotifyIcon;
using Impeller.App.ViewModels.Engine;
using Impeller.Platform.Windows;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell;

/// <summary>
/// The notification-area icon, and the window's relationship with it.
/// </summary>
/// <remarks>
/// <para>
/// This is the visible half of the two-process split. The engine is a service and keeps controlling
/// fans whether or not this window exists — but a window that vanishes when closed leaves no way to
/// tell that from an app that quit, and the second reading is the one people reach for. An icon that
/// stays is the answer.
/// </para>
/// <para>
/// Closing hides rather than exits, so the icon outlives the window. Exiting is a deliberate choice
/// from the icon's own menu, and it stops the shell, not the fans.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly Window _window;
    private readonly EngineConnection _connection;

    private bool _exiting;

    public TrayIcon(Window window, EngineConnection connection)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(connection);

        _window = window;
        _connection = connection;

        // Commands, not Click handlers. H.NotifyIcon's default menu mode does not show this
        // MenuFlyout: it reads the items and builds a native Win32 menu, whose selection handler
        // does exactly one thing - execute the item's Command. A Click handler on a MenuFlyoutItem
        // is never raised by anything, so both of these did nothing at all.
        var show = new MenuFlyoutItem { Text = "Show Impeller", Command = new ShowCommand(Show) };
        var exit = new MenuFlyoutItem { Text = "Exit", Command = new ShowCommand(Exit) };

        _icon = new TaskbarIcon
        {
            ToolTipText = "Impeller",
            ContextFlyout = new MenuFlyout { Items = { show, new MenuFlyoutSeparator(), exit } },
            Icon = LoadIcon(),
        };

        _icon.LeftClickCommand = new ShowCommand(Show);
        _icon.ForceCreate();

        _connection.PropertyChanged += OnConnectionChanged;
        _window.AppWindow.Closing += OnWindowClosing;

        UpdateToolTip();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.PropertyChanged -= OnConnectionChanged;
        _window.AppWindow.Closing -= OnWindowClosing;
        _icon.Dispose();
    }

    private void Show()
    {
        _window.AppWindow.Show();
        _window.Activate();
    }

    private void Exit()
    {
        _exiting = true;
        _window.Close();
    }

    /// <summary>
    /// Hides the window instead of letting it close, unless the user asked to exit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine is untouched either way. What this preserves is the icon, and with it the only
    /// visible evidence that the fans are still being managed.
    /// </para>
    /// <para>
    /// <c>AppWindow.Closing</c>, not <c>Window.Closed</c>. This was written against Closed and
    /// setting <c>WindowEventArgs.Handled</c>, which reads exactly like a cancel and is not one:
    /// Closed is raised to say the window has already gone, and nothing consults that flag. So the
    /// close went through, the process ended, and the tray icon this class exists to keep alive
    /// went with it - the one behaviour the whole file is written to prevent.
    /// </para>
    /// <para>
    /// Closing is the cancellable one, and it is raised for a real exit too, which is what
    /// <see cref="_exiting"/> is for.
    /// </para>
    /// </remarks>
    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting)
        {
            return;
        }

        args.Cancel = true;
        _window.AppWindow.Hide();
    }

    private void OnConnectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        UpdateToolTip();

    /// <summary>
    /// Puts the engine's state where hovering finds it.
    /// </summary>
    /// <remarks>
    /// The tooltip is the whole point of the icon while the window is hidden. "Impeller" alone
    /// answers nothing; whether the engine is reachable is the one thing worth knowing from here.
    /// </remarks>
    private void UpdateToolTip() =>
        _icon.ToolTipText = $"Impeller — {_connection.StatusMessage}";

    /// <summary>
    /// The mark, at whatever size this desktop wants it.
    /// </summary>
    /// <remarks>
    /// Without an icon the notification area draws the placeholder glyph - a square with a circle
    /// and a cross through it - which is what a program shows when it has no image at all. The
    /// tooltip was doing the entire job of identifying this app.
    /// </remarks>
    private static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            var (width, height) = NotificationArea.IconSize;
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

            return new System.Drawing.Icon(path, width, height);
        }
        catch (Exception ex) when (ex is System.IO.IOException or ArgumentException)
        {
            // A missing or unreadable icon file is a cosmetic failure. Losing the tray icon
            // entirely over it would not be.
            return null;
        }
    }

    /// <summary>A command with no state, for the icon's own click.</summary>
    private sealed class ShowCommand(Action show) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => show();
    }
}
