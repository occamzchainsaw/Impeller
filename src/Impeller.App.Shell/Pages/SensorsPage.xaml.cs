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
            if (!MoveFocusOut(box))
            {
                await item.RenameCommand.ExecuteAsync(null);
            }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            item.IsRenaming = false;
            box.Text = item.Name;

            // Leaves the field too, for the same reason Enter does: abandoning an edit and being
            // left with the caret still blinking in it says nothing happened.
            MoveFocusOut(box);
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
