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
            if (!MoveFocusOut(box))
            {
                await card.RenameCommand.ExecuteAsync(null);
            }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            card.IsRenaming = false;
            box.Text = card.Name;

            // Leaves the field too, for the same reason Enter does: abandoning an edit and being
            // left with the caret still blinking in it says nothing happened.
            MoveFocusOut(box);
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
    /// Asks before taking a fan off the page, because its settings go with it.
    /// </summary>
    /// <remarks>
    /// The default button is Cancel. A confirmation whose destructive answer is one Enter away is a
    /// confirmation that trains people to press Enter.
    /// </remarks>
    private async void OnRemoveFan(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ControlCardViewModel card })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Remove {card.Name}?",
            Content = "Its curve, its limits and anything calibration measured for it are removed "
                + "with it, and the fan goes back to the board's own control. You can add it again "
                + "at any time, but it will come back with none of that.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            await card.RemoveCommand.ExecuteAsync(null);
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
    /// <para>
    /// Handing the job to something in the same row rather than asking the focus manager to find
    /// the next element. <c>TryMoveFocus</c> answered false from inside the repeater and moved
    /// nothing, so the rename went through and the caret stayed sitting in the box — which looks
    /// exactly like Enter having done nothing at all, and was reported as such twice.
    /// </para>
    /// <para>
    /// Focused as though by the pointer, which is the one focus state that draws no focus visual.
    /// Programmatic still rings the target, and lighting up the Identify button because somebody
    /// finished renaming a fan points at the wrong thing entirely.
    /// </para>
    /// </remarks>
    private static bool MoveFocusOut(FrameworkElement box)
    {
        try
        {
            if (box.Parent is Panel row)
            {
                foreach (var sibling in row.Children)
                {
                    if (!ReferenceEquals(sibling, box)
                        && sibling is Control control
                        && control.Focus(FocusState.Pointer))
                    {
                        return true;
                    }
                }
            }

            return box.XamlRoot?.Content is DependencyObject root
                && FocusManager.TryMoveFocus(
                    FocusNavigationDirection.Next,
                    new FindNextElementOptions { SearchRoot = root });
        }
        catch (Exception)
        {
            // Failing to move focus is a cosmetic disappointment. Throwing out of a keystroke
            // handler is a closed application, and this shell has already done that once.
            return false;
        }
    }
}
