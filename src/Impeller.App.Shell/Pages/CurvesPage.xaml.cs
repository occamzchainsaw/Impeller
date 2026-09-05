using Impeller.App.Shell.Controls;
using Impeller.App.ViewModels;
using Impeller.App.ViewModels.Curves;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Pages;

/// <summary>
/// The curves in the configuration, and what each of them is asking for.
/// </summary>
/// <remarks>
/// A list, with the editing done on a panel that opens over it — the shape the Sensors page already
/// uses for computed sensors. It used to be a list beside an editor driven by the selection, which
/// made a curve that had been added and never saved something the page had to take back out of the
/// list when the selection moved; doing that mid-selection closed the window.
/// </remarks>
public sealed partial class CurvesPage : Page
{
    public CurvesPage()
    {
        InitializeComponent();

        ViewModel.Compose = ComposeAsync;
        ViewModel.Confirm = ConfirmAsync;

        Loaded += async (_, _) => await ViewModel.LoadAsync().ConfigureAwait(true);
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    /// <summary>The page's view model, pulled from the container because WinUI builds pages itself.</summary>
    public CurvesViewModel ViewModel { get; } = App.GetService<CurvesViewModel>();

    /// <summary>Shown when the condition holds.</summary>
    public static Visibility When(bool condition) =>
        condition ? Visibility.Visible : Visibility.Collapsed;

    private async void OnAddGraph(object sender, RoutedEventArgs e) => await Add(CurveEditorKind.Graph);

    private async void OnAddLinear(object sender, RoutedEventArgs e) => await Add(CurveEditorKind.Linear);

    private async void OnAddAuto(object sender, RoutedEventArgs e) => await Add(CurveEditorKind.Auto);

    private async void OnAddTrigger(object sender, RoutedEventArgs e) => await Add(CurveEditorKind.Trigger);

    private async void OnAddFlat(object sender, RoutedEventArgs e) => await Add(CurveEditorKind.Flat);

    private async void OnAddMix(object sender, RoutedEventArgs e) => await Add(CurveEditorKind.Mix);

    private async void OnAddSync(object sender, RoutedEventArgs e) => await Add(CurveEditorKind.Sync);

    private Task Add(CurveEditorKind kind) => ViewModel.AddCommand.ExecuteAsync(kind);

    private async void OnEditCurve(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CurveListItemViewModel item })
        {
            await ViewModel.EditCommand.ExecuteAsync(item.Id);
        }
    }

    private async void OnDeleteCurve(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CurveListItemViewModel item })
        {
            await ViewModel.DeleteCommand.ExecuteAsync(item.Id);
        }
    }

    /// <summary>
    /// Puts a curve's panel in front of the user and reports whether they kept it.
    /// </summary>
    /// <remarks>
    /// The dialog's own width cap is 548, which is not enough for a canvas worth dragging points
    /// on, so it is raised for this one. Overriding the resource on the dialog rather than in the
    /// application's dictionary keeps every other dialog the size it should be.
    /// </remarks>
    private async Task<bool> ComposeAsync(CurveEditorViewModel editor)
    {
        var panel = new CurveEditorPanel(editor);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = editor.IsNew ? "New curve" : "Edit curve",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.Resources["ContentDialogMaxWidth"] = 900d;

        return await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Asks before doing something that cannot be undone.
    /// </summary>
    /// <remarks>
    /// The default button is Cancel. A confirmation whose dangerous answer is one Enter away is a
    /// confirmation that trains people to press Enter.
    /// </remarks>
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Keep it",
            DefaultButton = ContentDialogButton.Close,
        };

        return await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}
