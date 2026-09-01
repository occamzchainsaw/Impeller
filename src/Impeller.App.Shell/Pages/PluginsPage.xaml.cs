using Impeller.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Pages;

/// <summary>The Plugins section.</summary>
public sealed partial class PluginsPage : Page
{
    /// <summary>The page's view model, bound to from XAML.</summary>
    public PluginsViewModel ViewModel { get; } = App.GetService<PluginsViewModel>();

    public PluginsPage() => InitializeComponent();
}
