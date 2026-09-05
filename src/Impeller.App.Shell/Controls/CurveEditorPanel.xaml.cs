using Impeller.App.ViewModels.Curves;
using Impeller.App.ViewModels.Sensors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Controls;

/// <summary>
/// One curve's editor, shown over the list rather than beside it.
/// </summary>
/// <remarks>
/// The same shape the computed-sensor panel has, and moved here for the same two reasons: the page
/// stays the list it is, and a curve that is being written is not in that list until it is kept.
/// Wider than the sensor panel because the graph editor needs a canvas worth dragging points on.
/// </remarks>
public sealed partial class CurveEditorPanel : UserControl
{
    /// <param name="editor">The curve being edited.</param>
    public CurveEditorPanel(CurveEditorViewModel editor)
    {
        Editor = editor ?? throw new ArgumentNullException(nameof(editor));
        InitializeComponent();
    }

    /// <summary>The curve being edited.</summary>
    public CurveEditorViewModel Editor { get; }

    /// <summary>The ways a mix can combine its inputs, for the one combo box that offers them.</summary>
    public static IReadOnlyList<MixFunctionChoice> MixFunctions => CurveEditorViewModel.MixFunctions;

    /// <summary>Shown when the condition holds.</summary>
    public static Visibility When(bool condition) =>
        condition ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shown when it does not.</summary>
    public static Visibility WhenNot(bool condition) =>
        condition ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Shown when nothing is there.</summary>
    public static Visibility WhenNull(object? value) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shown when something is.</summary>
    public static Visibility WhenNotNull(object? value) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>What kind of curve this is, for the strip above the panel.</summary>
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

    /// <summary>A sensor with its current reading, so the choice can be made on live values.</summary>
    public static string DescribeSensor(string name, string value) => $"{name}   {value}";

    /// <summary>
    /// Records the sensor a radio button stands for.
    /// </summary>
    /// <remarks>
    /// The editor decides whether it is a change. These fire on realisation as well as on a click,
    /// and the group holding the current sensor opens itself every time the panel opens.
    /// </remarks>
    private void OnSensorChosen(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: SensorItemViewModel sensor })
        {
            Editor.ChooseSensor(sensor);
        }
    }

    /// <summary>Marks the curve changed when a point is dragged, added or removed.</summary>
    private void OnCurveChanged(object sender, EventArgs e)
    {
        // Nothing to record: the canvas edits the editor's own points, and the panel is kept or
        // discarded as a whole.
    }
}
