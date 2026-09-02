using System.Collections.Specialized;
using Impeller.App.ViewModels.Curves;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Impeller.App.Shell.Controls;

/// <summary>
/// The graph curve editor: temperature across, duty up, and points the user drags.
/// </summary>
/// <remarks>
/// <para>
/// Purpose-built rather than a charting library, because almost none of this is charting. The
/// drawing is a polyline and a grid; the whole of the work is the interaction — click empty space
/// to add a point, right-click one to remove it, drag one with its reading held between its
/// neighbours. Bending a chart control into accepting that costs more code than the control does,
/// and leaves a dependency behind.
/// </para>
/// <para>
/// The constraints live in <see cref="CurveEditorViewModel"/>, not here. This translates pixels into
/// readings and asks; the model decides what is allowed, which is what lets the rules be tested
/// without a window.
/// </para>
/// </remarks>
public sealed class CurveCanvas : Canvas
{
    /// <summary>Room around the plot for the axis labels.</summary>
    private const double Padding = 28;

    /// <summary>How wide a point is, and how near the pointer has to be to grab one.</summary>
    private const double ThumbSize = 12;

    /// <summary>Spacing of the grid, in each axis's own units.</summary>
    private const double GridStep = 10;

    private readonly Polyline _line = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly List<Ellipse> _thumbs = [];
    private readonly List<Line> _grid = [];
    private readonly List<TextBlock> _labels = [];

    private CurvePointViewModel? _dragging;

    /// <summary>The curve being edited.</summary>
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor),
        typeof(CurveEditorViewModel),
        typeof(CurveCanvas),
        new PropertyMetadata(null, OnEditorChanged));

    public CurveCanvas()
    {
        Background = new SolidColorBrush(Colors.Transparent);
        MinHeight = 220;

        Children.Add(_line);

        SizeChanged += (_, _) => Redraw();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => _dragging = null;
    }

    /// <summary>Raised whenever the user changes the curve, so the page can mark it unsaved.</summary>
    public event EventHandler? CurveChanged;

    /// <summary>The curve being edited.</summary>
    public CurveEditorViewModel? Editor
    {
        get => (CurveEditorViewModel?)GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    /// <summary>The colour of the curve itself.</summary>
    public Brush LineBrush { get; set; } = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));

    /// <summary>The colour of the grid behind it.</summary>
    public Brush GridBrush { get; set; } = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));

    private static void OnEditorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var canvas = (CurveCanvas)sender;

        if (args.OldValue is CurveEditorViewModel old)
        {
            old.Points.CollectionChanged -= canvas.OnPointsChanged;
        }

        if (args.NewValue is CurveEditorViewModel editor)
        {
            editor.Points.CollectionChanged += canvas.OnPointsChanged;
        }

        canvas.Redraw();
    }

    private void OnPointsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    /// <summary>
    /// Adds a point where the user clicked, or takes hold of the one under the pointer.
    /// </summary>
    /// <remarks>
    /// A new point is picked up immediately, so placing one and putting it where you meant is a
    /// single gesture rather than a click followed by a separate drag.
    /// </remarks>
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Editor is not { Kind: CurveEditorKind.Graph } editor)
        {
            return;
        }

        var position = e.GetCurrentPoint(this).Position;

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (Nearest(editor, position) is { } target && editor.RemovePoint(target))
            {
                Raise();
            }

            return;
        }

        _dragging = Nearest(editor, position)
            ?? editor.AddPoint((float)ToInput(position.X), (float)ToDuty(position.Y));

        CapturePointer(e.Pointer);
        Redraw();
        Raise();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Editor is not { } editor)
        {
            return;
        }

        var position = e.GetCurrentPoint(this).Position;

        if (_dragging is null)
        {
            // A pointer over a point should say it can be moved before the user tries.
            ProtectedCursor = editor.Kind == CurveEditorKind.Graph && Nearest(editor, position) is not null
                ? InputSystemCursor.Create(InputSystemCursorShape.SizeAll)
                : InputSystemCursor.Create(InputSystemCursorShape.Arrow);

            return;
        }

        editor.MovePoint(_dragging, (float)ToInput(position.X), (float)ToDuty(position.Y));
        Redraw();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging is not null)
        {
            _dragging = null;
            Raise();
        }

        ReleasePointerCapture(e.Pointer);
    }

    /// <summary>The point under the pointer, if one is close enough to have been aimed at.</summary>
    private CurvePointViewModel? Nearest(CurveEditorViewModel editor, Point position)
    {
        CurvePointViewModel? best = null;
        var bestDistance = ThumbSize * 1.5;

        foreach (var point in editor.Points)
        {
            var dx = ToX(editor, point.Input) - position.X;
            var dy = ToY(point.Duty) - position.Y;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));

            if (distance < bestDistance)
            {
                best = point;
                bestDistance = distance;
            }
        }

        return best;
    }

    private void Redraw()
    {
        if (ActualWidth <= Padding * 2 || ActualHeight <= Padding * 2)
        {
            return;
        }

        DrawGrid();

        if (Editor is not { Kind: CurveEditorKind.Graph } editor)
        {
            _line.Points.Clear();
            ClearThumbs();
            return;
        }

        _line.Stroke = LineBrush;
        _line.Points.Clear();

        foreach (var point in editor.Points)
        {
            _line.Points.Add(new Point(ToX(editor, point.Input), ToY(point.Duty)));
        }

        DrawThumbs(editor);
    }

    private void DrawGrid()
    {
        var wanted = 0;

        for (var duty = 0.0; duty <= 100.0; duty += GridStep * 2)
        {
            var y = ToY(duty);
            Place(wanted++, Padding, y, ActualWidth - Padding, y);
        }

        if (Editor is { } editor)
        {
            for (var input = editor.AxisMinimum; input <= editor.AxisMaximum; input += (float)GridStep * 2)
            {
                var x = ToX(editor, input);
                Place(wanted++, x, Padding, x, ActualHeight - Padding);
            }
        }

        // Grid lines are reused rather than recreated on every pointer move, which happens often
        // enough while dragging that allocating a fresh visual tree each time is visible.
        for (var i = wanted; i < _grid.Count; i++)
        {
            _grid[i].Visibility = Visibility.Collapsed;
        }
    }

    private void Place(int index, double x1, double y1, double x2, double y2)
    {
        while (_grid.Count <= index)
        {
            var line = new Line { StrokeThickness = 1, Stroke = GridBrush };
            _grid.Add(line);
            Children.Insert(0, line);
        }

        var existing = _grid[index];
        existing.Visibility = Visibility.Visible;
        existing.X1 = x1;
        existing.Y1 = y1;
        existing.X2 = x2;
        existing.Y2 = y2;
    }

    private void DrawThumbs(CurveEditorViewModel editor)
    {
        while (_thumbs.Count < editor.Points.Count)
        {
            var thumb = new Ellipse
            {
                Width = ThumbSize,
                Height = ThumbSize,
                Fill = LineBrush,
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };

            _thumbs.Add(thumb);
            Children.Add(thumb);
        }

        for (var i = 0; i < _thumbs.Count; i++)
        {
            if (i >= editor.Points.Count)
            {
                _thumbs[i].Visibility = Visibility.Collapsed;
                continue;
            }

            var point = editor.Points[i];
            _thumbs[i].Visibility = Visibility.Visible;
            SetLeft(_thumbs[i], ToX(editor, point.Input) - (ThumbSize / 2));
            SetTop(_thumbs[i], ToY(point.Duty) - (ThumbSize / 2));
        }

        DrawLabels(editor);
    }

    /// <summary>
    /// Puts the numbers next to the points.
    /// </summary>
    /// <remarks>
    /// Dragging without them is guesswork: the whole reason someone opens this is to say "sixty
    /// percent at fifty degrees", and a shape with no numbers on it cannot be told that.
    /// </remarks>
    private void DrawLabels(CurveEditorViewModel editor)
    {
        while (_labels.Count < editor.Points.Count)
        {
            var label = new TextBlock { FontSize = 11, IsHitTestVisible = false, Opacity = 0.8 };
            _labels.Add(label);
            Children.Add(label);
        }

        for (var i = 0; i < _labels.Count; i++)
        {
            if (i >= editor.Points.Count)
            {
                _labels[i].Visibility = Visibility.Collapsed;
                continue;
            }

            var point = editor.Points[i];
            _labels[i].Visibility = Visibility.Visible;
            _labels[i].Text = $"{point.Input:0}° {point.Duty:0}%";
            SetLeft(_labels[i], ToX(editor, point.Input) + ThumbSize);
            SetTop(_labels[i], ToY(point.Duty) - ThumbSize - 4);
        }
    }

    private void ClearThumbs()
    {
        foreach (var thumb in _thumbs)
        {
            thumb.Visibility = Visibility.Collapsed;
        }

        foreach (var label in _labels)
        {
            label.Visibility = Visibility.Collapsed;
        }
    }

    private double ToX(CurveEditorViewModel editor, double input)
    {
        var span = Math.Max(editor.AxisMaximum - editor.AxisMinimum, 1);
        return Padding + ((input - editor.AxisMinimum) / span * (ActualWidth - (Padding * 2)));
    }

    private double ToY(double duty) =>
        ActualHeight - Padding - (duty / 100.0 * (ActualHeight - (Padding * 2)));

    private double ToInput(double x)
    {
        if (Editor is not { } editor)
        {
            return 0;
        }

        var span = Math.Max(editor.AxisMaximum - editor.AxisMinimum, 1);
        var plot = Math.Max(ActualWidth - (Padding * 2), 1);

        return editor.AxisMinimum + ((x - Padding) / plot * span);
    }

    private double ToDuty(double y)
    {
        var plot = Math.Max(ActualHeight - (Padding * 2), 1);
        return (ActualHeight - Padding - y) / plot * 100.0;
    }

    private void Raise() => CurveChanged?.Invoke(this, EventArgs.Empty);
}
