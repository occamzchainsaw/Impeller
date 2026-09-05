using System.ComponentModel;
using Impeller.App.ViewModels;
using Impeller.App.ViewModels.Curves;
using Impeller.App.ViewModels.Sensors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Pages;

/// <summary>
/// The curve editor: a list on the left, and whichever panel the selected curve needs on the right.
/// </summary>
/// <remarks>
/// <para>
/// One panel per kind. The shared four-box grid this replaced showed every kind the same four
/// numbers and let the headers carry the difference, which meant a flat curve — a constant — was
/// offered a lower and an upper value, a mix curve was offered a temperature range it does not
/// read, and a trigger's thresholds were called "lower" and "upper" rather than idle and load.
/// </para>
/// <para>
/// The boxes bind two-way straight to the editor. They used to be named controls filled and read
/// back by hand, because a two-way binding fired while the panel was being replaced and wrote the
/// old curve's numbers into the new one — which is a real hazard, and the reason the editor is now
/// replaced wholesale rather than refilled in place.
/// </para>
/// </remarks>
public sealed partial class CurvesPage : Page
{
    public CurvesPage()
    {
        InitializeComponent();

        Canvas.CurveChanged += (_, _) => ViewModel.IsDirty = true;
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Confirm = ConfirmAsync;

        Loaded += async (_, _) => await ViewModel.LoadAsync().ConfigureAwait(true);

        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            ViewModel.Dispose();
        };
    }

    /// <summary>The page's view model, pulled from the container because WinUI builds pages itself.</summary>
    public CurvesViewModel ViewModel { get; } = App.GetService<CurvesViewModel>();

    /// <summary>The ways a mix can combine its inputs, for the one combo box that offers them.</summary>
    public static IReadOnlyList<MixFunctionChoice> MixFunctions => CurveEditorViewModel.MixFunctions;

    /// <summary>Shown when nothing is selected.</summary>
    public static Visibility WhenNull(object? value) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shown when something is.</summary>
    public static Visibility WhenNotNull(object? value) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Shown when the condition holds.</summary>
    public static Visibility When(bool condition) =>
        condition ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shown when it does not.</summary>
    public static Visibility WhenNot(bool condition) =>
        condition ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>What kind of curve is open, for the strip above the panel.</summary>
    public static string KindLabel(CurveEditorViewModel? editor) => editor?.Kind switch
    {
        CurveEditorKind.Flat => "Flat curve",
        CurveEditorKind.Linear => "Linear curve",
        CurveEditorKind.Graph => "Graph curve",
        CurveEditorKind.Mix => "Mix curve",
        CurveEditorKind.Sync => "Sync curve",
        CurveEditorKind.Trigger => "Trigger curve",
        CurveEditorKind.Auto => "Auto curve",
        _ => string.Empty,
    };

    /// <summary>The subtitle under a curve in the list: what it is, and what it drives.</summary>
    public static string DescribeCurve(string kind, int users) => users switch
    {
        0 => $"{kind} · not used",
        1 => $"{kind} · drives 1 fan",
        _ => $"{kind} · drives {users} fans",
    };

    /// <summary>A sensor with its current reading, so the choice can be made on live values.</summary>
    public static string DescribeSensor(string name, string value) => $"{name}   {value}";

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

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CurvesViewModel.Editor))
        {
            Canvas.Editor = ViewModel.Editor;
        }
    }

    /// <summary>Records which sensor the curve should read.</summary>
    private void OnSensorChosen(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: SensorItemViewModel sensor })
        {
            ViewModel.ChooseSensor(sensor);
        }
    }

    private void OnAddGraph(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Graph);

    private void OnAddLinear(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Linear);

    private void OnAddAuto(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Auto);

    private void OnAddTrigger(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Trigger);

    private void OnAddFlat(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Flat);

    private void OnAddMix(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Mix);

    private void OnAddSync(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Sync);

    private void Add(CurveEditorKind kind) => ViewModel.AddCommand.Execute(kind);
}
