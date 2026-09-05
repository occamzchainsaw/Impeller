using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell;

/// <summary>
/// Shows a dialog, or declines to, rather than throwing.
/// </summary>
/// <remarks>
/// <para>
/// WinUI permits one <see cref="ContentDialog"/> at a time and answers a second one by throwing
/// <c>0x80000019</c>. Every one of these is raised from a <c>Click</c> handler, which is
/// <c>async void</c>, so that exception has no caller to catch it and takes the window down.
/// </para>
/// <para>
/// Found by driving the UI: an automated click reached a Delete button while its own confirmation
/// was already open, and the shell closed. A pointer cannot normally do that, because the dialog
/// blocks input behind it - but "normally" is doing a lot of work in that sentence, and the failure
/// is not a misdrawn panel, it is the application ending. Declining costs a click that does
/// nothing, which is what the user already expects from a button behind a modal dialog.
/// </para>
/// </remarks>
internal static class Dialogs
{
    private static bool _open;

    /// <summary>Whether a dialog is on screen.</summary>
    internal static bool IsOpen => _open;

    /// <summary>
    /// Shows the dialog and waits for the answer, or answers <see cref="ContentDialogResult.None"/>
    /// if one is already up.
    /// </summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        if (_open)
        {
            return ContentDialogResult.None;
        }

        _open = true;

        try
        {
            return await dialog.ShowAsync();
        }
        catch (COMException)
        {
            // The flag above is the guard; this is the belt to its braces. A dialog raised from
            // somewhere that does not go through here, or a window torn down mid-prompt, would
            // otherwise arrive at the same fatal place.
            return ContentDialogResult.None;
        }
        finally
        {
            _open = false;
        }
    }
}
