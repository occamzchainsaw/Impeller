using CommunityToolkit.Mvvm.ComponentModel;

namespace Impeller.App.ViewModels;

/// <summary>
/// Base for anything hosted in the shell's navigation frame.
/// </summary>
/// <remarks>
/// Pages are navigated to far more often than they are created, so loading is a separate
/// awaitable step rather than constructor work: a view model can be resolved cheaply and only
/// pay for its data when it is actually shown.
/// </remarks>
public abstract partial class PageViewModel : ObservableObject
{
    /// <summary>The heading shown at the top of the page.</summary>
    public abstract string Title { get; }

    /// <summary>Whether the page is currently fetching its data.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>
    /// Called when the page is navigated to. The default does nothing; pages that need data
    /// override it.
    /// </summary>
    public virtual Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
