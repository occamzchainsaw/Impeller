using Impeller.App.ViewModels;
using Impeller.App.ViewModels.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Pages;

/// <summary>The overview page: engine state, and every control with what is driving it.</summary>
public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();

        Loaded += async (_, _) => await ViewModel.LoadAsync().ConfigureAwait(true);

        // Unhooked when the page goes away. A view model left subscribed to the connection stays
        // alive for the life of the window and keeps handling ticks for a page nobody is looking at.
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    /// <summary>The page's view model, pulled from the container because WinUI builds pages itself.</summary>
    public DashboardViewModel ViewModel { get; } = App.GetService<DashboardViewModel>();

    /// <summary>
    /// An unreachable engine is a warning, not a note. Fans left to the firmware is a state worth
    /// noticing rather than one to report in the same tone as everything being fine.
    /// </summary>
    public static InfoBarSeverity SeverityFor(bool connected) =>
        connected ? InfoBarSeverity.Success : InfoBarSeverity.Warning;

    /// <summary>Reads better than a bare number bound next to a label.</summary>
    public static string SensorSummary(int count) =>
        count == 1 ? "1 sensor" : $"{count} sensors";

    /// <summary>Whether there is anything to show, for a bar that appears only when there is.</summary>
    public static bool HasText(string? text) => !string.IsNullOrWhiteSpace(text);

    /// <summary>Inverts a flag, which x:Bind cannot do on its own.</summary>
    public static bool Not(bool value) => !value;

    /// <summary>
    /// Turns engine control of one fan on or off.
    /// </summary>
    /// <remarks>
    /// A handler rather than a two-way binding, because the switch is bound one-way to the saved
    /// state: a save that the engine refuses must leave the switch showing what is actually in
    /// force, not what was clicked.
    /// </remarks>
    private async void OnDrivenToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { DataContext: ControlCardViewModel card } toggle
            && toggle.IsOn != card.IsDriven)
        {
            await card.SetDrivenCommand.ExecuteAsync(toggle.IsOn);
        }
    }
}
