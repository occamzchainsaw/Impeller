namespace Impeller.App.ViewModels;

/// <summary>
/// Moves work onto whatever thread the user interface insists on.
/// </summary>
/// <remarks>
/// An interface rather than a direct reference to a dispatcher because this project deliberately
/// has no UI-framework dependency: the same view models are meant to be drivable by a different
/// shell, and by a test with no message loop at all.
/// </remarks>
public interface IUiDispatcher
{
    /// <summary>Queues work for the UI thread, or runs it now if that is where we already are.</summary>
    void Post(Action action);
}

/// <summary>
/// Runs the work where it stands.
/// </summary>
/// <remarks>
/// The default, and what tests use. It is the right answer whenever there is no UI thread to be
/// wrong about — and being explicit about that beats a nullable dispatcher checked at every call.
/// </remarks>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    /// <summary>The one instance. It holds no state.</summary>
    public static readonly ImmediateDispatcher Instance = new();

    private ImmediateDispatcher()
    {
    }

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
