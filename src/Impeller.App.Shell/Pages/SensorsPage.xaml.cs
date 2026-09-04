using Impeller.App.ViewModels;
using Impeller.App.ViewModels.Sensors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Impeller.App.Shell.Pages;

/// <summary>
/// Every sensor the engine can see, grouped and searchable.
/// </summary>
/// <remarks>
/// A diagnostic page, and a useful one: "does the engine see my water temperature" sits behind half
/// of what goes wrong, and it is answerable here in one search.
/// </remarks>
public sealed partial class SensorsPage : Page
{
    public SensorsPage()
    {
        InitializeComponent();

        Loaded += async (_, _) => await ViewModel.LoadAsync().ConfigureAwait(true);
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    /// <summary>The page's view model, pulled from the container because WinUI builds pages itself.</summary>
    public SensorsViewModel ViewModel { get; } = App.GetService<SensorsViewModel>();

    /// <summary>What the hardware calls it and where it lives, for a tooltip.</summary>
    public static string Where(string fullName, string path) =>
        fullName + System.Environment.NewLine + path;

    /// <summary>Notes that the user is typing, so a refresh does not overwrite them.</summary>
    private void OnNameFocused(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SensorItemViewModel item })
        {
            item.IsRenaming = true;
        }
    }

    /// <summary>Commits on Enter, abandons on Escape.</summary>
    private async void OnNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: SensorItemViewModel item } box)
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
                await item.RenameCommand.ExecuteAsync(null);
            }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            item.IsRenaming = false;
            box.Text = item.Name;
        }
    }

    /// <summary>Commits when the field is left.</summary>
    private async void OnNameCommitted(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SensorItemViewModel item })
        {
            await item.RenameCommand.ExecuteAsync(null);
        }
    }

    /// <summary>How many rows survived the search, so an empty page says why it is empty.</summary>
    public static string Showing(int count) =>
        count == 1 ? "1 sensor" : $"{count} sensors";

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
