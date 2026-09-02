using Impeller.App.ViewModels;
using Microsoft.UI.Dispatching;

namespace Impeller.App.Shell;

/// <summary>
/// The shell's UI thread, as the view models see it.
/// </summary>
/// <remarks>
/// <para>
/// Engine events arrive on the transport's thread, once a second, and everything downstream of them
/// is bound to XAML — which does not merely dislike being touched from elsewhere, it throws. This is
/// the one place the two meet.
/// </para>
/// <para>
/// The queue is taken from the window's dispatcher at startup rather than resolved per call, so it
/// is the UI thread's queue even when the first tick arrives before anyone has looked at a page.
/// </para>
/// </remarks>
public sealed class ShellDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    private readonly DispatcherQueue _queue = queue;

    /// <inheritdoc />
    /// <remarks>
    /// Already on the UI thread means running it now. Queueing regardless would work and would also
    /// mean a click handler's own updates arriving a frame after the click.
    /// </remarks>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_queue.HasThreadAccess)
        {
            action();
            return;
        }

        // A false result means the queue is shutting down, which is the window closing. There is
        // nothing left to update and nothing worth reporting about not updating it.
        _queue.TryEnqueue(() => action());
    }
}
