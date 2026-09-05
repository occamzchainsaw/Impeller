using Impeller.App.ViewModels.Sensors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Impeller.App.Shell.Controls;

/// <summary>
/// The form for a computed sensor, shown inside a dialog.
/// </summary>
/// <remarks>
/// A dialog rather than a panel on the page, unlike the curve editor. Making one of these is a
/// bounded act with an obvious end - name it, say what it reads, done - where a curve is something
/// people sit and shape while watching it respond. The Sensors page stays the list it is, which is
/// what it is used for the other ninety-nine times.
/// </remarks>
public sealed partial class CustomSensorPanel : UserControl
{
    /// <param name="editor">The sensor being written.</param>
    public CustomSensorPanel(CustomSensorEditorViewModel editor)
    {
        Editor = editor ?? throw new ArgumentNullException(nameof(editor));
        InitializeComponent();
    }

    /// <summary>The sensor being written.</summary>
    public CustomSensorEditorViewModel Editor { get; }

    /// <summary>Shown when the condition holds.</summary>
    public static Visibility When(bool condition) =>
        condition ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The heading over the picker, which is a different question for one sensor than for several.</summary>
    public static string Reads(bool several) => several ? "Sensors to combine" : "Sensor to read";

    /// <summary>A row in the picker: what it is called, and what it says right now.</summary>
    /// <remarks>
    /// The live reading is there so a sensor can be told apart from its neighbours by what it is
    /// doing. On this machine four of them are called "Temperature".
    /// </remarks>
    public static string Describe(string name, string value) => $"{name}    {value}";
}
