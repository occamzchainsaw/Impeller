using System.Runtime.InteropServices;

namespace Impeller.Platform.Windows;

/// <summary>
/// How large the display a window is on draws things.
/// </summary>
/// <remarks>
/// Here rather than in the shell because it is a Windows API call, and because the shell would have
/// to switch on unsafe code for the one generated marshalling stub it needs. Everything that talks
/// to the operating system directly lives in this project.
/// </remarks>
public static partial class DisplayScaling
{
    /// <summary>The DPI a window is treated as being at when nothing else is known.</summary>
    private const double DefaultDpi = 96d;

    /// <summary>
    /// The scale factor of the display a window is on, where 1.0 is 96 DPI.
    /// </summary>
    /// <param name="window">The window handle.</param>
    /// <remarks>
    /// Per window rather than per system: a machine with a 4K laptop screen at 150% and an external
    /// monitor at 100% has two right answers at once, and the one that matters is the display this
    /// window is on. A failure answers 1.0, which is wrong by at most a scale factor and never
    /// stops anything.
    /// </remarks>
    public static double ForWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return 1d;
        }

        var dpi = GetDpiForWindow(window);
        return dpi == 0 ? 1d : dpi / DefaultDpi;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr window);
}
