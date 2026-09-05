namespace Impeller.App.ViewModels.Shell;

/// <summary>
/// A rectangle in physical pixels, which is the unit windows are actually placed in.
/// </summary>
/// <remarks>
/// Deliberately not a WinUI <c>RectInt32</c>. The rules below are arithmetic and belong where they
/// can be tested without a window; the shell converts at the boundary.
/// </remarks>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>One past the rightmost pixel.</summary>
    public int Right => X + Width;

    /// <summary>One past the bottom pixel.</summary>
    public int Bottom => Y + Height;

    /// <summary>Whether this rectangle encloses nothing.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>The overlap between two rectangles, empty when they do not touch.</summary>
    public PixelRect Intersect(PixelRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);

        return right <= x || bottom <= y
            ? new PixelRect(0, 0, 0, 0)
            : new PixelRect(x, y, right - x, bottom - y);
    }
}

/// <summary>
/// Where the window was, and whether it was maximised.
/// </summary>
/// <remarks>
/// The bounds are always the <em>restored</em> bounds, even when <see cref="Maximized"/> is set.
/// A maximised window's own rectangle is the monitor, which says nothing about where to put it when
/// the user unmaximises it — so the last ordinary size is what gets kept.
/// </remarks>
public readonly record struct WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>
/// Decides how big the window opens and whether a remembered position is still usable.
/// </summary>
/// <remarks>
/// <para>
/// The default matters more than it looks. Windows sizes a window given no instructions from the
/// display, so on an ultrawide the app opened roughly half the desktop wide and half as tall — a
/// letterbox with the cards in one long row and nothing beneath them. A fixed, sensible size scaled
/// for DPI is right on every monitor; a proportion of the screen is right on none of them.
/// </para>
/// <para>
/// Restoring is the other half. A position is only worth keeping while the monitor it names still
/// exists: undocking a laptop, unplugging a second screen or dropping the resolution all leave a
/// remembered rectangle in space no display covers, and a window restored there is invisible with
/// no way to reach it. Every one of those gets reported as "the app will not open".
/// </para>
/// </remarks>
public static class WindowPlacementPolicy
{
    /// <summary>The size the window opens at on a first run, in logical pixels.</summary>
    public const int PreferredWidth = 1200;

    /// <summary>See <see cref="PreferredWidth"/>.</summary>
    public const int PreferredHeight = 820;

    /// <summary>The smallest the window may be, in logical pixels.</summary>
    /// <remarks>
    /// Below roughly this the navigation pane collapses over the content and the dashboard cards
    /// stop fitting side by side. It is a floor on what is restored as much as on what is dragged:
    /// a size remembered from a smaller screen must not open unusably small on this one.
    /// </remarks>
    public const int MinimumWidth = 760;

    /// <summary>See <see cref="MinimumWidth"/>.</summary>
    public const int MinimumHeight = 560;

    /// <summary>The most of a display the window will claim on a first run.</summary>
    private const double MostOfTheScreen = 0.9;

    /// <summary>How much of the title bar must be on a display for the window to count as reachable.</summary>
    /// <remarks>
    /// Physical pixels, and small on purpose. The question is not whether the window looks tidy but
    /// whether it can be grabbed and dragged back, and a couple of centimetres of title bar is
    /// enough for that. Anything stricter re-centres windows people deliberately parked half off
    /// the edge of a screen.
    /// </remarks>
    private const int ReachableWidth = 120;

    /// <summary>See <see cref="ReachableWidth"/>.</summary>
    private const int ReachableHeight = 24;

    /// <summary>The band at the top of a window that carries its title bar.</summary>
    private const int TitleBarBand = 40;

    /// <summary>
    /// The size and position to open at when nothing has been remembered.
    /// </summary>
    /// <param name="workArea">The usable area of the display to open on, excluding the taskbar.</param>
    /// <param name="scale">That display's scale factor, where 1.0 is 96 DPI.</param>
    public static WindowPlacement Default(PixelRect workArea, double scale)
    {
        var width = Fit(PreferredWidth, MinimumWidth, workArea.Width, scale);
        var height = Fit(PreferredHeight, MinimumHeight, workArea.Height, scale);

        return new WindowPlacement(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height,
            Maximized: false);
    }

    /// <summary>
    /// Turns a remembered placement into one that will actually be on screen.
    /// </summary>
    /// <param name="stored">What was last written down, from a run that may have had other monitors.</param>
    /// <param name="displays">The work areas of every display attached now.</param>
    /// <param name="fallback">The display to open on if the remembered position is nowhere.</param>
    /// <param name="scale">The scale factor of that fallback display.</param>
    /// <remarks>
    /// A placement that is still reachable comes back with its position untouched, including one
    /// straddling two monitors or hanging off an edge. Someone who put a window there meant to, and
    /// tidying it up for them on every launch would be its own bug.
    /// </remarks>
    public static WindowPlacement Restore(
        WindowPlacement stored,
        IReadOnlyList<PixelRect> displays,
        PixelRect fallback,
        double scale)
    {
        ArgumentNullException.ThrowIfNull(displays);

        if (stored.Width <= 0 || stored.Height <= 0)
        {
            return Default(fallback, scale);
        }

        // Grown to the minimum before the reachability test, not after, because the rectangle that
        // has to be reachable is the one the window will actually occupy.
        var sized = stored with
        {
            Width = Math.Max(stored.Width, Scale(MinimumWidth, scale)),
            Height = Math.Max(stored.Height, Scale(MinimumHeight, scale)),
        };

        if (IsReachable(sized, displays))
        {
            return sized;
        }

        // The monitor it was on is gone. Open centred instead — but keep the maximised state, which
        // was a preference about how to use a screen rather than a fact about which one.
        return Default(fallback, scale) with { Maximized = stored.Maximized };
    }

    /// <summary>
    /// Whether enough of the window's title bar lands on a display to be grabbed.
    /// </summary>
    /// <remarks>
    /// The title bar rather than the window, because a window whose body is visible but whose title
    /// bar sits above the top of the screen cannot be moved with the mouse at all.
    /// </remarks>
    internal static bool IsReachable(WindowPlacement placement, IReadOnlyList<PixelRect> displays)
    {
        var titleBar = new PixelRect(
            placement.X,
            placement.Y,
            placement.Width,
            Math.Min(placement.Height, TitleBarBand));

        foreach (var display in displays)
        {
            var visible = display.Intersect(titleBar);

            if (visible.Width >= ReachableWidth && visible.Height >= ReachableHeight)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One dimension of the default size: preferred, but never off the end of the display.</summary>
    private static int Fit(int preferred, int minimum, int available, double scale)
    {
        var wanted = Math.Min(Scale(preferred, scale), (int)(available * MostOfTheScreen));

        // The minimum outranks the display only as far as the display itself. On a screen too small
        // for the minimum, filling it is the best answer available.
        return Math.Min(Math.Max(wanted, Scale(minimum, scale)), Math.Max(available, 1));
    }

    /// <summary>Logical pixels as physical ones, which is what a window is placed in.</summary>
    private static int Scale(int logical, double scale) =>
        (int)Math.Round(logical * (double.IsFinite(scale) && scale > 0d ? scale : 1d));
}
