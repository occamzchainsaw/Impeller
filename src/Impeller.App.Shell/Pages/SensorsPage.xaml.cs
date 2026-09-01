using Impeller.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Pages;

/// <summary>The Sensors section.</summary>
public sealed partial class SensorsPage : Page
{
    /// <summary>The page's view model, bound to from XAML.</summary>
    public SensorsViewModel ViewModel { get; } = App.GetService<SensorsViewModel>();

    public SensorsPage() => InitializeComponent();
}
