using Impeller.App.ViewModels;
using Impeller.Core.Abstractions;
using Impeller.Core.Abstractions.Configuration;
using Impeller.Ipc.Contracts;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// The line under each curve's name on the page.
/// </summary>
/// <remarks>
/// "Which of my curves is watching the GPU" used to need opening every one of them in turn, which
/// is the sort of thing a list is for.
/// </remarks>
public sealed class CurveListTests
{
    private static readonly SensorId Cpu = SensorId.New();

    private static Dictionary<SensorId, SensorDescriptor> Sensors() => new()
    {
        [Cpu] = new SensorDescriptor(
            Cpu, "Core (Tctl/Tdie)", "Core (Tctl/Tdie)", "AMD Ryzen 7 9800X3D",
            SensorKind.Temperature, "lhm", "lhm/amdcpu/0/temperature/2", 61f),
    };

    [Fact]
    public void A_curve_says_which_sensor_it_reads()
    {
        var curve = new AutoCurveDefinition { Name = "Auto CPU", Source = Cpu };

        Assert.Equal(
            "Reads Core (Tctl/Tdie)",
            CurvesViewModel.Describe(curve, Sensors(), new ImpellerConfiguration()));
    }

    [Fact]
    public void A_curve_reading_nothing_says_so()
    {
        var curve = new LinearCurveDefinition { Name = "Ramp" };

        Assert.Equal(
            "No sensor chosen yet",
            CurvesViewModel.Describe(curve, Sensors(), new ImpellerConfiguration()));
    }

    [Fact]
    public void A_curve_reading_hardware_that_is_gone_says_that_instead()
    {
        // Not "no sensor chosen": the user chose one, and it is the disappearance that is the news.
        var curve = new GraphCurveDefinition { Name = "Graph", Source = SensorId.New() };

        Assert.Equal(
            "Reads a sensor that is not here",
            CurvesViewModel.Describe(curve, Sensors(), new ImpellerConfiguration()));
    }

    [Fact]
    public void A_flat_curve_says_its_one_number()
    {
        var curve = new FlatCurveDefinition { Name = "Flat", Duty = new Duty(75f) };

        Assert.Equal("Always 75 %", CurvesViewModel.Describe(curve, Sensors(), new ImpellerConfiguration()));
    }

    [Fact]
    public void A_mix_counts_what_it_combines()
    {
        var curve = new MixCurveDefinition { Name = "Mix", Sources = [CurveId.New(), CurveId.New()] };

        Assert.Equal("Combines 2 curves", CurvesViewModel.Describe(curve, Sensors(), new ImpellerConfiguration()));
    }

    [Fact]
    public void A_sync_names_the_curve_it_follows()
    {
        var followed = new FlatCurveDefinition { Id = CurveId.New(), Name = "Pump" };

        var curve = new SyncCurveDefinition
        {
            Name = "Follower",
            SourceKind = SyncSourceKind.Curve,
            SourceCurve = followed.Id,
        };

        var configuration = new ImpellerConfiguration { Curves = [followed] };

        Assert.Equal("Follows 'Pump'", CurvesViewModel.Describe(curve, Sensors(), configuration));
    }

    [Fact]
    public void A_row_says_what_it_is_and_what_it_drives()
    {
        var row = new CurveListItemViewModel(new AutoCurveDefinition { Name = "Auto CPU" });

        Assert.Equal("Auto · not used", row.Summary);

        row.Users = 1;
        Assert.Equal("Auto · drives 1 fan", row.Summary);

        row.Users = 3;
        Assert.Equal("Auto · drives 3 fans", row.Summary);
    }

    [Theory]
    [InlineData(0, "Nothing is using it, so nothing else changes.")]
    [InlineData(1, "The fan it drives will be switched off. This cannot be undone.")]
    [InlineData(2, "The 2 fans it drives will be switched off. This cannot be undone.")]
    public void Deleting_a_curve_says_what_it_costs(int users, string expected)
    {
        var row = new CurveListItemViewModel(new FlatCurveDefinition { Name = "Flat" }) { Users = users };

        Assert.Equal(expected, CurvesViewModel.DeletionCost(row));
    }
}
