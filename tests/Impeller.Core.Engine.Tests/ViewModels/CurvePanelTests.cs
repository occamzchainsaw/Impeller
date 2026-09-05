using Impeller.App.ViewModels.Curves;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// Covers the per-kind editor panels: what each kind offers, and what it keeps.
/// </summary>
/// <remarks>
/// <para>
/// The editor served all seven kinds from one grid of four numbers, with the headers carrying the
/// difference. That grid offered a flat curve — a constant — a lower and an upper value, offered a
/// mix curve a temperature range it does not read, and never showed hysteresis, a mix's inputs, a
/// sync's target or a trigger's hold times at all: four settings a user could store in a file and
/// never reach from the app.
/// </para>
/// <para>
/// So the cases worth pinning are the ones that were unreachable before, and the two rules that
/// decide which panel a kind gets.
/// </para>
/// </remarks>
public class CurvePanelTests
{
    private static CurveEditorOptions Options(params CurveDefinition[] curves) =>
        new(
            [.. curves.Select(curve => new CurveChoiceViewModel(curve.Id, curve.Name))],
            [new ControlChoice(SensorId.New(), "System Fan #3")]);

    // ---- which panel a kind gets ---------------------------------------------------------------

    [Theory]
    [InlineData(CurveEditorKind.Linear, true)]
    [InlineData(CurveEditorKind.Graph, true)]
    [InlineData(CurveEditorKind.Trigger, true)]
    [InlineData(CurveEditorKind.Auto, true)]
    [InlineData(CurveEditorKind.Flat, false)]
    [InlineData(CurveEditorKind.Mix, false)]
    [InlineData(CurveEditorKind.Sync, false)]
    public void Only_the_kinds_that_read_a_temperature_are_offered_one(CurveEditorKind kind, bool reads)
    {
        // Mix and sync read other curves; flat reads nothing. Offering any of them a sensor is
        // offering a setting that does nothing, which is worse than offering nothing.
        Assert.Equal(reads, Open(kind).ReadsASensor);
    }

    [Theory]
    [InlineData(CurveEditorKind.Linear, true)]
    [InlineData(CurveEditorKind.Graph, true)]
    [InlineData(CurveEditorKind.Trigger, false)]
    [InlineData(CurveEditorKind.Auto, false)]
    [InlineData(CurveEditorKind.Flat, false)]
    public void Only_the_kinds_that_map_a_reading_straight_onto_a_duty_get_hysteresis(
        CurveEditorKind kind,
        bool hysteresis)
    {
        // A trigger has thresholds and hold times of its own and an auto curve has a deadband.
        // Giving either a second set would be two mechanisms arguing over the same decision.
        Assert.Equal(hysteresis, Open(kind).HasHysteresis);
    }

    // ---- hysteresis, which had no UI at all -----------------------------------------------------

    [Fact]
    public void Hysteresis_survives_being_opened_and_saved()
    {
        var editor = new CurveEditorViewModel(new LinearCurveDefinition
        {
            Id = CurveId.New(),
            Name = "Case",
            Hysteresis = new HysteresisDefinition(2f, 1.5f, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9)),
        });

        Assert.Equal(2f, editor.DeadbandUp, precision: 3);
        Assert.Equal(3d, editor.HysteresisUpSeconds, precision: 3);
        Assert.Equal(9d, editor.HysteresisDownSeconds, precision: 3);

        var built = Assert.IsType<LinearCurveDefinition>(editor.Build());

        Assert.Equal(1.5f, built.Hysteresis.DeadbandDown, precision: 3);
        Assert.Equal(TimeSpan.FromSeconds(3), built.Hysteresis.ResponseUp);
        Assert.Equal(TimeSpan.FromSeconds(9), built.Hysteresis.ResponseDown);
    }

    [Fact]
    public void An_emptied_box_is_read_as_nothing_rather_than_thrown_over()
    {
        // A NumberBox reports NaN when it is cleared, and TimeSpan.FromSeconds throws on it. A hold
        // time of zero is a perfectly good answer; an exception out of a text box is not.
        var editor = new CurveEditorViewModel(new LinearCurveDefinition { Id = CurveId.New(), Name = "Case" })
        {
            HysteresisUpSeconds = double.NaN,
            DeadbandUp = double.NaN,
            LowInput = double.NaN,
        };

        var built = Assert.IsType<LinearCurveDefinition>(editor.Build());

        Assert.Equal(TimeSpan.Zero, built.Hysteresis.ResponseUp);
        Assert.Equal(0f, built.Hysteresis.DeadbandUp);
        Assert.Equal(0f, built.MinimumInput);
    }

    // ---- a trigger's hold times, in the seconds the boxes hold ----------------------------------

    [Fact]
    public void A_triggers_hold_times_survive_being_opened_and_saved()
    {
        var editor = new CurveEditorViewModel(new TriggerCurveDefinition
        {
            Id = CurveId.New(),
            Name = "Burst",
            IdleInput = 45f,
            LoadInput = 65f,
            IdleDuty = new Duty(30f),
            LoadDuty = new Duty(85f),
            ResponseUp = TimeSpan.FromSeconds(4),
            ResponseDown = TimeSpan.FromSeconds(20),
        });

        Assert.Equal(4d, editor.ResponseUpSeconds, precision: 3);
        Assert.Equal(20d, editor.ResponseDownSeconds, precision: 3);

        var built = Assert.IsType<TriggerCurveDefinition>(editor.Build());

        Assert.Equal(45f, built.IdleInput, precision: 3);
        Assert.Equal(65f, built.LoadInput, precision: 3);
        Assert.Equal(30f, built.IdleDuty.Percent, precision: 3);
        Assert.Equal(85f, built.LoadDuty.Percent, precision: 3);
        Assert.Equal(TimeSpan.FromSeconds(4), built.ResponseUp);
        Assert.Equal(TimeSpan.FromSeconds(20), built.ResponseDown);
    }

    // ---- a mix's inputs, which were unreachable ------------------------------------------------

    [Fact]
    public void A_mix_opens_with_the_curves_it_already_combines_ticked()
    {
        var one = new FlatCurveDefinition { Id = CurveId.New(), Name = "CPU" };
        var two = new FlatCurveDefinition { Id = CurveId.New(), Name = "GPU" };
        var three = new FlatCurveDefinition { Id = CurveId.New(), Name = "Drives" };

        var editor = new CurveEditorViewModel(
            new MixCurveDefinition
            {
                Id = CurveId.New(),
                Name = "Case",
                Function = MixFunction.Maximum,
                Sources = [one.Id, three.Id],
            },
            Options(one, two, three));

        Assert.Equal(
            ["CPU", "Drives"],
            editor.CurveChoices.Where(curve => curve.IsSelected).Select(curve => curve.Name));
    }

    [Fact]
    public void Ticking_a_curve_puts_it_into_the_mix()
    {
        var one = new FlatCurveDefinition { Id = CurveId.New(), Name = "CPU" };
        var two = new FlatCurveDefinition { Id = CurveId.New(), Name = "GPU" };

        var editor = new CurveEditorViewModel(
            new MixCurveDefinition { Id = CurveId.New(), Name = "Case" },
            Options(one, two));

        editor.CurveChoices.Single(curve => curve.Name == "GPU").IsSelected = true;

        var built = Assert.IsType<MixCurveDefinition>(editor.Build());

        Assert.Equal([two.Id], built.Sources);
    }

    [Fact]
    public void Ticking_a_curve_marks_the_curve_unsaved()
    {
        // The rows are separate objects, so a page watching only the editor would never notice.
        var one = new FlatCurveDefinition { Id = CurveId.New(), Name = "CPU" };

        var editor = new CurveEditorViewModel(
            new MixCurveDefinition { Id = CurveId.New(), Name = "Case" },
            Options(one));

        var changed = false;
        editor.PropertyChanged += (_, _) => changed = true;

        editor.CurveChoices[0].IsSelected = true;

        Assert.True(changed);
    }

    [Fact]
    public void A_curve_is_never_offered_itself_as_an_input()
    {
        // A mix that includes itself is a cycle the engine refuses, so offering it is offering a
        // way to break the configuration from a tick box.
        var self = new MixCurveDefinition { Id = CurveId.New(), Name = "Case" };
        var other = new FlatCurveDefinition { Id = CurveId.New(), Name = "CPU" };

        var editor = new CurveEditorViewModel(self, Options(self, other));

        Assert.Equal(["CPU"], editor.CurveChoices.Select(curve => curve.Name));
    }

    [Fact]
    public void A_mix_with_nothing_to_mix_says_so_rather_than_showing_an_empty_box()
    {
        var editor = new CurveEditorViewModel(
            new MixCurveDefinition { Id = CurveId.New(), Name = "Case" },
            CurveEditorOptions.Empty);

        Assert.False(editor.HasOtherCurves);
    }

    [Fact]
    public void Every_way_of_mixing_has_a_label()
    {
        // The combo box shows these rather than the enum names, and a missing one would show blank.
        Assert.Equal(
            Enum.GetValues<MixFunction>().Length,
            CurveEditorViewModel.MixFunctions.Count);

        Assert.All(
            CurveEditorViewModel.MixFunctions,
            choice => Assert.False(string.IsNullOrWhiteSpace(choice.Label)));
    }

    // ---- a sync's target, also unreachable ------------------------------------------------------

    [Fact]
    public void A_sync_opens_on_the_curve_it_follows()
    {
        var followed = new FlatCurveDefinition { Id = CurveId.New(), Name = "CPU" };

        var editor = new CurveEditorViewModel(
            new SyncCurveDefinition
            {
                Id = CurveId.New(),
                Name = "Rear",
                SourceKind = SyncSourceKind.Curve,
                SourceCurve = followed.Id,
                Offset = -10f,
            },
            Options(followed));

        Assert.Equal("CPU", editor.SelectedCurve?.Name);
        Assert.True(editor.FollowsCurve);
        Assert.False(editor.FollowsControl);
        Assert.Equal(0, editor.SyncSourceIndex);
        Assert.Equal(-10d, editor.Offset, precision: 3);
    }

    [Fact]
    public void A_sync_switched_to_a_fan_saves_the_fan()
    {
        var options = Options();
        var fan = options.Controls[0];

        var editor = new CurveEditorViewModel(
            new SyncCurveDefinition { Id = CurveId.New(), Name = "Rear" },
            options)
        {
            SyncSourceIndex = 1,
        };

        editor.SelectedControl = fan;

        var built = Assert.IsType<SyncCurveDefinition>(editor.Build());

        Assert.Equal(SyncSourceKind.Control, built.SourceKind);
        Assert.Equal(fan.Id, built.SourceControl);
        Assert.True(editor.FollowsControl);
    }

    [Fact]
    public void A_sync_that_has_chosen_neither_shows_the_curve_picker_and_saves_that_choice()
    {
        // None is a state, not a choice. A brand new sync curve with no picker at all looks broken,
        // and following another curve is much the commoner of the two - so the panel shows the
        // curve picker, and this used to assert that the editor went on holding None while it did.
        //
        // That is the bug rather than the behaviour. RadioButtons.SelectedIndex is bound two way,
        // and a two-way binding never pushes back a value the control is already displaying: the
        // panel said "Another curve" from the moment it opened, the user picked a curve, pressed
        // Save, and got SourceKind.None - a sync curve that follows nothing while every part of
        // the screen said otherwise. What is shown has to be what is stored.
        var editor = new CurveEditorViewModel(new SyncCurveDefinition { Id = CurveId.New(), Name = "Rear" });

        Assert.Equal(SyncSourceKind.Curve, editor.SyncSourceKind);
        Assert.True(editor.FollowsCurve);
        Assert.Equal(0, editor.SyncSourceIndex);

        Assert.Equal(SyncSourceKind.Curve, Assert.IsType<SyncCurveDefinition>(editor.Build()).SourceKind);
    }

    [Fact]
    public void A_sync_that_follows_a_fan_keeps_following_a_fan()
    {
        // The coercion above is for None alone. Reading back a choice the user did make and quietly
        // changing it would be a worse bug than the one it fixes.
        var editor = new CurveEditorViewModel(
            new SyncCurveDefinition { Id = CurveId.New(), Name = "Rear", SourceKind = SyncSourceKind.Control });

        Assert.Equal(SyncSourceKind.Control, editor.SyncSourceKind);
        Assert.True(editor.FollowsControl);
        Assert.Equal(1, editor.SyncSourceIndex);
    }

    // ---- the live read-out ----------------------------------------------------------------------

    [Fact]
    public void A_curve_with_no_output_says_so_rather_than_showing_zero()
    {
        // Zero is a duty. "No output" is a curve whose sensor is not reporting, and reading one as
        // the other would show a curve asking for a stopped fan.
        var editor = new CurveEditorViewModel(new FlatCurveDefinition { Id = CurveId.New(), Name = "Case" });

        Assert.Equal("no output", editor.OutputText);

        editor.LiveOutput = 0f;

        Assert.Equal("0 %", editor.OutputText);
    }

    [Fact]
    public void The_read_out_is_not_an_edit()
    {
        // It changes once a second on its own. If it counted as a change, every curve would be
        // unsaved within a second of being opened and Revert would be permanently lit.
        var editor = new CurveEditorViewModel(new FlatCurveDefinition { Id = CurveId.New(), Name = "Case" });
        var changes = new List<string?>();

        editor.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        editor.LiveOutput = 48.8f;

        Assert.All(
            changes,
            name => Assert.True(
                name is nameof(CurveEditorViewModel.LiveOutput) or nameof(CurveEditorViewModel.OutputText),
                $"{name} would be read as an edit"));
    }

    private static CurveEditorViewModel Open(CurveEditorKind kind)
    {
        CurveDefinition definition = kind switch
        {
            CurveEditorKind.Linear => new LinearCurveDefinition(),
            CurveEditorKind.Graph => new GraphCurveDefinition(),
            CurveEditorKind.Mix => new MixCurveDefinition(),
            CurveEditorKind.Sync => new SyncCurveDefinition(),
            CurveEditorKind.Trigger => new TriggerCurveDefinition(),
            CurveEditorKind.Auto => new AutoCurveDefinition(),
            _ => new FlatCurveDefinition(),
        };

        return new CurveEditorViewModel(definition with { Id = CurveId.New(), Name = "x" });
    }
}
