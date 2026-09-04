using Impeller.App.ViewModels;
using Impeller.App.ViewModels.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;
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

    /// <summary>Reads better than a bare number bound next to a label.</summary>
    public static string SensorSummary(int count) =>
        count == 1 ? "1 sensor" : $"{count} sensors";

    /// <summary>Whether a card has something to say, for the one bar that is still a card's own.</summary>
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
        if (sender is TextBox { Tag: ControlCardViewModel card })
        {
            card.IsRenaming = true;
        }
    }

    /// <summary>Commits on Enter, and abandons on Escape.</summary>
    private async void OnNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: ControlCardViewModel card } box)
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;

            // Moving focus out raises LostFocus, which is the commit. Doing both would send the
            // rename twice, so this only commits directly when the focus did not move.
            //
            // The overload with a search root is not optional: the one-argument TryMoveFocus is
            // unsupported in a WinUI Desktop app and throws a catastrophic-failure COMException
            // out of an async void handler, which takes the whole shell down. It did.
            if (!MoveFocusOut())
            {
                await card.RenameCommand.ExecuteAsync(null);
            }
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
        if (sender is TextBox { Tag: ControlCardViewModel card })
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
    /// Moves focus off the field being edited, which is what commits it.
    /// </summary>
    /// <returns>Whether focus actually moved.</returns>
    /// <remarks>
    /// Guarded, because failing to move focus is a cosmetic disappointment and throwing out of a
    /// keystroke handler is a closed application.
    /// </remarks>
    private bool MoveFocusOut()
    {
        try
        {
            return Content is DependencyObject root
                && FocusManager.TryMoveFocus(
                    FocusNavigationDirection.Next,
                    new FindNextElementOptions { SearchRoot = root });
        }
        catch (Exception)
        {
            return false;
        }
    }
}
