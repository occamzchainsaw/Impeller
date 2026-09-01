using Impeller.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Pages;

/// <summary>The fan and sensor overview.</summary>
public sealed partial class DashboardPage : Page
{
    /// <summary>The page's view model, bound to from XAML.</summary>
    public DashboardViewModel ViewModel { get; } = App.GetService<DashboardViewModel>();

    public DashboardPage() => InitializeComponent();
}
