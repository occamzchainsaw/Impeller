using Impeller.App.ViewModels;
using Impeller.App.ViewModels.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Impeller.App.Shell.Pages;

/// <summary>The overview page: engine state, and every configured fan with what is driving it.</summary>
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

    /// <summary>Shown when the condition holds.</summary>
    public static Visibility When(bool condition) =>
        condition ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shown when it does not.</summary>
    public static Visibility WhenNot(bool condition) =>
        condition ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Shown only when there is something to say.</summary>
    public static Visibility WhenText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The slider's position as a label.</summary>
    public static string Percent(float value) => $"{value:0}%";

    /// <summary>
    /// What the hardware calls this fan, and where it lives, for a tooltip.
    /// </summary>
    /// <remarks>
    /// Carries the provider's own name as well, because once a fan has been called "seat blower"
    /// the only route back to "System Fan #4" is for something to still say it.
    /// </remarks>
    public static string Where(string providerName, string hardware, string path)
    {
        var lines = new[] { providerName, hardware, path }
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return string.Join('\n', lines);
    }

    /// <summary>Notes that the user is typing, so an arriving snapshot does not overwrite them.</summary>
    private void OnNameFocused(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ControlCardViewModel card })
        {
            card.IsRenaming = true;
        }
    }

    /// <summary>Commits on Enter, and abandons on Escape.</summary>
    private async void OnNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: ControlCardViewModel card } box)
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await card.RenameCommand.ExecuteAsync(null);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            card.IsRenaming = false;
            box.Text = card.Name;
        }
    }

    /// <summary>Commits when the field is left, which is how most people finish typing.</summary>
    private async void OnNameCommitted(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ControlCardViewModel card })
        {
            await card.RenameCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// Fills the Add a fan menu at the moment it opens.
    /// </summary>
    /// <remarks>
    /// Built on opening rather than bound, because a MenuFlyout has no ItemsSource — and because
    /// the set changes whenever a fan is added, which is exactly when this next opens.
    /// </remarks>
    private void OnAvailableOpening(object? sender, object e)
    {
        AvailableFans.Items.Clear();

        if (ViewModel.Available.Count == 0)
        {
            AvailableFans.Items.Add(new MenuFlyoutItem
            {
                Text = "Every fan the engine can see is already here",
                IsEnabled = false,
            });

            return;
        }

        foreach (var fan in ViewModel.Available)
        {
            var item = new MenuFlyoutItem { Text = fan.ToString() };
            var chosen = fan;

            item.Click += async (_, _) => await ViewModel.AddFanCommand.ExecuteAsync(chosen);
            AvailableFans.Items.Add(item);
        }
    }

    /// <summary>
    /// Takes a fan by hand, or hands it back.
    /// </summary>
    /// <remarks>
    /// A handler rather than a two-way binding, because the switch is bound one-way to what the
    /// engine says is actually holding the fan: a claim the engine refuses must leave the switch
    /// showing the truth rather than what was clicked.
    /// </remarks>
    private async void OnModeToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { DataContext: ControlCardViewModel card } toggle
            && toggle.IsOn != card.IsPinned)
        {
            await card.SetModeCommand.ExecuteAsync(toggle.IsOn);
        }
    }
}
