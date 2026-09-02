using Impeller.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Pages;

/// <summary>
/// Every sensor the engine can see, grouped and searchable.
/// </summary>
/// <remarks>
/// A diagnostic page, and a useful one: "does the engine see my water temperature" sits behind half
/// of what goes wrong, and it is answerable here in one search.
/// </remarks>
public sealed partial class SensorsPage : Page
{
    public SensorsPage()
    {
        InitializeComponent();

        Loaded += async (_, _) => await ViewModel.LoadAsync().ConfigureAwait(true);
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    /// <summary>The page's view model, pulled from the container because WinUI builds pages itself.</summary>
    public SensorsViewModel ViewModel { get; } = App.GetService<SensorsViewModel>();

    /// <summary>How many rows survived the search, so an empty page says why it is empty.</summary>
    public static string Showing(int count) =>
        count == 1 ? "1 sensor" : $"{count} sensors";
}
