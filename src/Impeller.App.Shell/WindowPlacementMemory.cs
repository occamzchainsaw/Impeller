using Impeller.App.ViewModels.Shell;
using Impeller.Platform.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Impeller.App.Shell;

/// <summary>
/// Opens the window where it was left, and writes down where it is now.
/// </summary>
/// <remarks>
/// <para>
/// The rules about what is a sensible size and whether a remembered position still exists live in
/// <see cref="WindowPlacementPolicy"/>, where they can be tested. This is the part that cannot be:
/// asking WinUI where the window is and telling it where to go.
/// </para>
/// <para>
/// Saved as the window settles rather than only on the way out. A shell that is killed, or signed
/// out from underneath, never reaches its own shutdown — and the tray icon means the ordinary way
/// to stop looking at this app is to close it, which does not close it. Remembering only at exit
/// would mean remembering almost never.
/// </para>
/// </remarks>
internal sealed class WindowPlacementMemory
{
    /// <summary>How long the window must sit still before its position is written down.</summary>
    /// <remarks>
    /// Dragging a window raises a change per frame. Without this the file would be rewritten
    /// hundreds of times across one gesture, and the only one of those writes that matters is the
    /// last.
    /// </remarks>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly WindowPlacementStore _store;
    private readonly DispatcherQueueTimer _settle;

    private WindowPlacement _placement;

    public WindowPlacementMemory(Window window, WindowPlacementStore store)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(store);

        _window = window;
        _appWindow = window.AppWindow;
        _store = store;

        _settle = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _settle.Interval = Settle;
        _settle.IsRepeating = false;
        _settle.Tick += (_, _) => Save();
    }

    /// <summary>
    /// Places the window, then starts watching it.
    /// </summary>
    /// <remarks>
    /// Called before the window is activated, so it appears where it belongs rather than appearing
    /// somewhere and jumping.
    /// </remarks>
    public void Restore()
    {
        var scale = ScaleFor(_window);

        // Indexed rather than iterated. FindAll hands back a WinRT vector view whose projection
        // cannot be enumerated - a foreach over it throws an InvalidCastException from deep inside
        // the interop layer, which here means the window never opens at all.
        var all = DisplayArea.FindAll();
        var displays = new List<PixelRect>(all.Count);

        for (var index = 0; index < all.Count; index++)
        {
            displays.Add(ToPixelRect(all[index].WorkArea));
        }

        // Nearest, not Primary: on a machine that opened the window on the second monitor last time,
        // the fallback for an unusable stored position should be the screen the window is heading
        // for, not whichever one Windows calls first.
        var here = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
        var fallback = here is null ? new PixelRect(0, 0, 1920, 1080) : ToPixelRect(here.WorkArea);

        _placement = _store.Read() is { } remembered
            ? WindowPlacementPolicy.Restore(remembered, displays, fallback, scale)
            : WindowPlacementPolicy.Default(fallback, scale);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            // A floor on dragging as well as on restoring. The two want the same number: a size the
            // window is still usable at.
            presenter.PreferredMinimumWidth = Scale(WindowPlacementPolicy.MinimumWidth, scale);
            presenter.PreferredMinimumHeight = Scale(WindowPlacementPolicy.MinimumHeight, scale);
        }

        _appWindow.MoveAndResize(new RectInt32(
            _placement.X,
            _placement.Y,
            _placement.Width,
            _placement.Height));

        // After the move, so the restored bounds underneath are the ones just set rather than
        // whatever Windows would hand back on unmaximising.
        if (_placement.Maximized && _appWindow.Presenter is OverlappedPresenter maximisable)
        {
            maximisable.Maximize();
        }

        // Never unsubscribed. Both events belong to the window this follows, so they die with it —
        // and the obvious place to let go, Closed, is the one place that must not, because closing
        // this window hides it to the tray rather than ending it.
        _appWindow.Changed += OnChanged;
        _window.Closed += OnClosed;
    }

    private void OnChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange)
        {
            return;
        }

        Capture();

        _settle.Stop();
        _settle.Start();
    }

    /// <summary>
    /// Writes down where the window is on the way out, without waiting to settle.
    /// </summary>
    /// <remarks>
    /// Runs on an ordinary close as well as a real exit. The tray icon turns the first into hiding
    /// the window, and where it was when it was hidden is exactly where it should come back.
    /// </remarks>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _settle.Stop();
        Capture();
        Save();
    }

    /// <summary>
    /// Notes the window's current bounds, if they are worth keeping.
    /// </summary>
    /// <remarks>
    /// A hidden or minimised window has a position, and it is not one to reopen at. Both keep the
    /// last ordinary bounds instead — as does a maximised window, whose own rectangle is the
    /// monitor and says nothing about where to put it once it is unmaximised.
    /// </remarks>
    private void Capture()
    {
        if (!_appWindow.IsVisible)
        {
            return;
        }

        var state = (_appWindow.Presenter as OverlappedPresenter)?.State;

        if (state == OverlappedPresenterState.Minimized)
        {
            return;
        }

        if (state == OverlappedPresenterState.Maximized)
        {
            _placement = _placement with { Maximized = true };
            return;
        }

        _placement = new WindowPlacement(
            _appWindow.Position.X,
            _appWindow.Position.Y,
            _appWindow.Size.Width,
            _appWindow.Size.Height,
            Maximized: false);
    }

    private void Save()
    {
        if (_placement.Width > 0 && _placement.Height > 0)
        {
            _store.Write(_placement);
        }
    }

    private static PixelRect ToPixelRect(RectInt32 rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);

    private static int Scale(int logical, double scale) => (int)Math.Round(logical * scale);

    /// <summary>
    /// The window's display scaling, as a factor on 96 DPI.
    /// </summary>
    /// <remarks>
    /// From the window handle rather than <c>XamlRoot.RasterizationScale</c>, which is the tidier
    /// property and is null until the content has loaded — by which time the window has already
    /// been on screen at the wrong size.
    /// </remarks>
    private static double ScaleFor(Window window)
    {
        try
        {
            return DisplayScaling.ForWindow(WindowNative.GetWindowHandle(window));
        }
        catch (Exception)
        {
            // A window placed at 100% on a 150% display is wrong by half. A window that failed to
            // open is wrong entirely.
            return 1d;
        }
    }
}
