using Impeller.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Pages;

/// <summary>The Settings section.</summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>The page's view model, bound to from XAML.</summary>
    public SettingsViewModel ViewModel { get; } = App.GetService<SettingsViewModel>();

    public SettingsPage() => InitializeComponent();
}
