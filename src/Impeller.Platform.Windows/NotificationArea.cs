using System.Runtime.InteropServices;

namespace Impeller.Platform.Windows;

/// <summary>
/// What the notification area expects of an icon.
/// </summary>
/// <remarks>
/// Here rather than in the shell for the same reason as <see cref="DisplayScaling"/>: it is a
/// Windows API call, and the shell would have to switch on unsafe code for the one generated
/// marshalling stub it needs.
/// </remarks>
public static partial class NotificationArea
{
    /// <summary>SM_CXSMICON.</summary>
    private const int SmallIconWidth = 49;

    /// <summary>SM_CYSMICON.</summary>
    private const int SmallIconHeight = 50;

    /// <summary>
    /// The size, in physical pixels, that a notification-area icon should be.
    /// </summary>
    /// <remarks>
    /// Asked rather than assumed, because it is not always 16. Windows scales it with the display,
    /// so a 150% desktop wants 24 and a 200% one wants 32 - and handing the tray a 16-pixel image
    /// on either gets it stretched, which is the blurry-tray-icon everyone recognises and nobody
    /// can place. A multi-resolution .ico has the right frame already; this is what picks it.
    /// </remarks>
    public static (int Width, int Height) IconSize
    {
        get
        {
            var width = GetSystemMetrics(SmallIconWidth);
            var height = GetSystemMetrics(SmallIconHeight);

            return width > 0 && height > 0 ? (width, height) : (16, 16);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
