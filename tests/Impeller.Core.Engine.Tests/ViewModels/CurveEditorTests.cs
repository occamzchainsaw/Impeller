using Impeller.App.ViewModels.Curves;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// Covers the editing behaviour behind the curve canvas.
/// </summary>
/// <remarks>
/// The constraints are the whole feature. A graph curve is a function of temperature, so two points
/// at one reading, or a point dragged past its neighbour, produce a shape the engine cannot
/// evaluate — and the place to stop that is at the drag, not at validation afterwards.
/// </remarks>
public class CurveEditorTests
{
    private static CurveEditorViewModel Graph(params (float Input, float Duty)[] points) =>
        new(new GraphCurveDefinition
        {
            Id = CurveId.New(),
            Name = "Case fans",
            Source = SensorId.New(),
            Points = [.. points.Select(point => new CurvePointDefinition(point.Input, new Duty(point.Duty)))],
        });

    [Fact]
    public void Points_open_in_ascending_order_whatever_order_they_were_stored_in()
    {
        var editor = Graph((70f, 100f), (30f, 20f), (50f, 60f));

        Assert.Equal([30f, 50f, 70f], editor.Points.Select(point => point.Input));
    }

    [Fact]
    public void A_point_dragged_past_its_neighbour_stops_at_it()
    {
        // Letting it through folds the curve back on itself, which is two duties for one
        // temperature and no longer a curve at all.
        var editor = Graph((30f, 20f), (50f, 60f), (70f, 100f));

        editor.MovePoint(editor.Points[1], 90f, 60f);

        Assert.Equal(70f, editor.Points[1].Input);
        Assert.Equal([30f, 70f, 70f], editor.Points.Select(point => point.Input));
    }

    [Fact]
    public void A_point_dragged_below_its_lower_neighbour_stops_there_too()
    {
        var editor = Graph((30f, 20f), (50f, 60f), (70f, 100f));

        editor.MovePoint(editor.Points[1], 10f, 60f);

        Assert.Equal(30f, editor.Points[1].Input);
    }

    [Fact]
    public void The_end_points_are_held_inside_the_axis()
    {
        var editor = Graph((30f, 20f), (70f, 100f));
        editor.AxisMinimum = 20f;
        editor.AxisMaximum = 90f;

        editor.MovePoint(editor.Points[0], -50f, 20f);
        editor.MovePoint(editor.Points[1], 500f, 100f);

        Assert.Equal(20f, editor.Points[0].Input);
        Assert.Equal(90f, editor.Points[1].Input);
    }

    [Fact]
    public void A_duty_dragged_out_of_range_is_clamped_but_may_still_fall_as_it_heats()
    {
        // Falling with temperature is unusual and perfectly legitimate — a pump that should be
        // quiet once the loop is warm, for one — so only the range is enforced, not the direction.
        var editor = Graph((30f, 20f), (70f, 100f));

        editor.MovePoint(editor.Points[1], 70f, 400f);
        Assert.Equal(100f, editor.Points[1].Duty);

        editor.MovePoint(editor.Points[1], 70f, -20f);
        Assert.Equal(0f, editor.Points[1].Duty);
        Assert.True(editor.Points[1].Duty < editor.Points[0].Duty);
    }

    [Fact]
    public void A_new_point_lands_in_order()
    {
        var editor = Graph((30f, 20f), (70f, 100f));

        editor.AddPoint(50f, 60f);

        Assert.Equal([30f, 50f, 70f], editor.Points.Select(point => point.Input));
    }

    [Fact]
    public void Clicking_on_an_existing_point_selects_it_rather_than_stacking_a_second()
    {
        // Two points at one reading make the curve ambiguous at exactly that temperature, and the
        // second one is invisible underneath the first.
        var editor = Graph((30f, 20f), (70f, 100f));

        var same = editor.AddPoint(30f, 90f);

        Assert.Equal(2, editor.Points.Count);
        Assert.Same(editor.Points[0], same);
        Assert.Equal(20f, same.Duty);
    }

    [Fact]
    public void The_last_two_points_cannot_be_deleted()
    {
        // One point is a flat curve wearing a graph's clothes; none is a curve with nothing to say,
        // at which point the engine holds the last duty and the fan stays wherever it was.
        var editor = Graph((30f, 20f), (50f, 60f), (70f, 100f));

        Assert.True(editor.RemovePoint(editor.Points[1]));
        Assert.False(editor.RemovePoint(editor.Points[0]));
        Assert.Equal(2, editor.Points.Count);
    }

    [Fact]
    public void A_graph_curve_round_trips_through_the_editor()
    {
        var editor = Graph((30f, 20f), (70f, 100f));
        editor.Name = "Renamed";
        editor.AddPoint(50f, 55f);

        var built = Assert.IsType<GraphCurveDefinition>(editor.Build());

        Assert.Equal(editor.Id, built.Id);
        Assert.Equal("Renamed", built.Name);
        Assert.Equal(3, built.Points.Count);
        Assert.Equal(55f, built.Points[1].Duty.Percent);
    }

    [Theory]
    [InlineData(typeof(FlatCurveDefinition), CurveEditorKind.Flat)]
    [InlineData(typeof(LinearCurveDefinition), CurveEditorKind.Linear)]
    [InlineData(typeof(GraphCurveDefinition), CurveEditorKind.Graph)]
    [InlineData(typeof(MixCurveDefinition), CurveEditorKind.Mix)]
    [InlineData(typeof(SyncCurveDefinition), CurveEditorKind.Sync)]
    [InlineData(typeof(TriggerCurveDefinition), CurveEditorKind.Trigger)]
    [InlineData(typeof(AutoCurveDefinition), CurveEditorKind.Auto)]
    public void Every_kind_opens_as_itself_and_builds_back_to_itself(Type type, CurveEditorKind kind)
    {
        // The view chooses its panel from the kind, and Build has to agree with it. A mismatch would
        // save an auto curve as a flat one and quietly discard everything the user set.
        var definition = (CurveDefinition)Activator.CreateInstance(type)!;
        var editor = new CurveEditorViewModel(definition with { Id = CurveId.New(), Name = "x" });

        Assert.Equal(kind, editor.Kind);
        Assert.IsType(type, editor.Build());
    }

    [Fact]
    public void An_auto_curve_keeps_the_numbers_that_make_it_an_auto_curve()
    {
        var source = SensorId.New();

        var editor = new CurveEditorViewModel(new AutoCurveDefinition
        {
            Id = CurveId.New(),
            Name = "Auto CPU",
            Source = source,
            IdleTemperature = 40f,
            LoadTemperature = 80f,
            MinimumDuty = new Duty(25f),
            MaximumDuty = new Duty(90f),
            Step = 1.5f,
            Deadband = 4f,
            ResponseTime = TimeSpan.FromSeconds(5),
        });

        var built = Assert.IsType<AutoCurveDefinition>(editor.Build());

        Assert.Equal(source, built.Source);
        Assert.Equal(40f, built.IdleTemperature);
        Assert.Equal(80f, built.LoadTemperature);
        Assert.Equal(25f, built.MinimumDuty.Percent);
        Assert.Equal(1.5f, built.Step);
        Assert.Equal(4f, built.Deadband);
        Assert.Equal(TimeSpan.FromSeconds(5), built.ResponseTime);
    }
}
