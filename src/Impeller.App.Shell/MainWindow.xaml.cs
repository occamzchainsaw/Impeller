using Impeller.App.Shell.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Impeller.App.Shell;

/// <summary>
/// The main window: a title bar and a navigation frame, and nothing else.
/// </summary>
/// <remarks>
/// Navigation is resolved from a tag on each item rather than by holding page types in code,
/// so adding a section is a XAML change plus one dictionary entry.
/// </remarks>
public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new(StringComparer.Ordinal)
    {
        ["dashboard"] = typeof(DashboardPage),
        ["curves"] = typeof(CurvesPage),
        ["sensors"] = typeof(SensorsPage),
        ["plugins"] = typeof(PluginsPage),
        ["settings"] = typeof(SettingsPage),
    };

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
        }
    }

    private void NavView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        // An unrecognised tag means a XAML item with no matching page. Ignoring it keeps a typo
        // from taking down the window; the section simply does not navigate.
        if (!Pages.TryGetValue(tag, out var page) || NavFrame.CurrentSourcePageType == page)
        {
            return;
        }

        NavFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }
}
