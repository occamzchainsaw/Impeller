using Impeller.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Pages;

/// <summary>The Curves section.</summary>
public sealed partial class CurvesPage : Page
{
    /// <summary>The page's view model, bound to from XAML.</summary>
    public CurvesViewModel ViewModel { get; } = App.GetService<CurvesViewModel>();

    public CurvesPage() => InitializeComponent();
}
