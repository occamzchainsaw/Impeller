using Impeller.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Pages;

/// <summary>The overview page: engine state, and every control with what is driving it.</summary>
public sealed partial class DashboardPage : Page
{
    public DashboardPage() => InitializeComponent();

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
}
