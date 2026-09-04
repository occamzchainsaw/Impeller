using System.ComponentModel;
using Impeller.App.ViewModels;
using Impeller.App.ViewModels.Curves;
using Impeller.App.ViewModels.Sensors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Pages;

/// <summary>
/// The curve editor: a list on the left, and whichever editor the selected curve needs on the right.
/// </summary>
/// <remarks>
/// The number boxes are filled from the editor and written back on change rather than two-way bound,
/// because the whole panel is replaced whenever the selection moves. A two-way binding fires while
/// that is happening and writes the old curve's numbers into the new one.
/// </remarks>
public sealed partial class CurvesPage : Page
{
    private bool _loading;

    public CurvesPage()
    {
        InitializeComponent();

        Canvas.CurveChanged += (_, _) => ViewModel.IsDirty = true;
        ViewModel.PropertyChanged += OnViewModelChanged;

        Loaded += async (_, _) => await ViewModel.LoadAsync().ConfigureAwait(true);

        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            ViewModel.Dispose();
        };
    }

    /// <summary>The page's view model, pulled from the container because WinUI builds pages itself.</summary>
    public CurvesViewModel ViewModel { get; } = App.GetService<CurvesViewModel>();

    /// <summary>Shown when nothing is selected.</summary>
    public static Visibility WhenNull(object? value) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shown when something is.</summary>
    public static Visibility WhenNotNull(object? value) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The canvas, for the one kind that has one.</summary>
    public static Visibility WhenGraph(CurveEditorViewModel? editor) =>
        editor?.Kind == CurveEditorKind.Graph ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The numbers, for the kinds that are numbers.</summary>
    public static Visibility WhenNotGraph(CurveEditorViewModel? editor) =>
        editor is null || editor.Kind == CurveEditorKind.Graph ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The sensor picker, for the kinds that read one.</summary>
    /// <remarks>
    /// Mix and sync read other curves, and flat reads nothing at all. Offering them a temperature
    /// would be offering a setting that does nothing.
    /// </remarks>
    public static Visibility WhenReadsASensor(CurveEditorViewModel? editor) =>
        editor?.Kind is CurveEditorKind.Linear or CurveEditorKind.Graph
            or CurveEditorKind.Trigger or CurveEditorKind.Auto
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>The subtitle under a curve in the list: what it is, and what it drives.</summary>
    public static string DescribeCurve(string kind, int users) => users switch
    {
        0 => $"{kind} · not used",
        1 => $"{kind} · drives 1 fan",
        _ => $"{kind} · drives {users} fans",
    };

    /// <summary>A sensor with its current reading, so the choice can be made on live values.</summary>
    public static string DescribeSensor(string name, string value) => $"{name}   {value}";

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CurvesViewModel.Editor))
        {
            Fill(ViewModel.Editor);
        }
    }

    /// <summary>
    /// Loads the editor's values into the controls.
    /// </summary>
    /// <remarks>
    /// The flag is what stops the assignments below being read back as user edits, which would mark
    /// a freshly opened curve unsaved before anyone had touched it.
    /// </remarks>
    private void Fill(CurveEditorViewModel? editor)
    {
        _loading = true;

        try
        {
            Canvas.Editor = editor;

            if (editor is null)
            {
                return;
            }

            NameBox.Text = editor.Name;
            LowInputBox.Value = editor.LowInput;
            HighInputBox.Value = editor.HighInput;
            MinimumDutyBox.Value = editor.MinimumDuty;
            MaximumDutyBox.Value = editor.MaximumDuty;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Writes the controls back into the editor and marks the curve unsaved.</summary>
    private void OnEdited(object sender, object args)
    {
        if (_loading || ViewModel.Editor is not { } editor)
        {
            return;
        }

        editor.Name = NameBox.Text;
        editor.LowInput = Sane(LowInputBox.Value, editor.LowInput);
        editor.HighInput = Sane(HighInputBox.Value, editor.HighInput);
        editor.MinimumDuty = Sane(MinimumDutyBox.Value, editor.MinimumDuty);
        editor.MaximumDuty = Sane(MaximumDutyBox.Value, editor.MaximumDuty);

        if (ViewModel.Selected is { } selected)
        {
            selected.Name = editor.Name;
        }

        ViewModel.IsDirty = true;
    }

    /// <summary>Records which sensor the curve should read.</summary>
    private void OnSensorChosen(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: SensorItemViewModel sensor })
        {
            ViewModel.SensorPicker.Selected = sensor;
            ViewModel.IsDirty = true;
        }
    }

    /// <summary>
    /// A number box's value, or the one it had.
    /// </summary>
    /// <remarks>
    /// An emptied box reports NaN, and writing that through would put a curve's threshold at a
    /// value nothing compares true against — a curve that silently never fires.
    /// </remarks>
    private static float Sane(double value, float fallback) =>
        double.IsNaN(value) ? fallback : (float)value;

    private void OnAddGraph(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Graph);

    private void OnAddLinear(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Linear);

    private void OnAddAuto(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Auto);

    private void OnAddTrigger(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Trigger);

    private void OnAddFlat(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Flat);

    private void OnAddMix(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Mix);

    private void OnAddSync(object sender, RoutedEventArgs e) => Add(CurveEditorKind.Sync);

    private void Add(CurveEditorKind kind) => ViewModel.AddCommand.Execute(kind);
}
