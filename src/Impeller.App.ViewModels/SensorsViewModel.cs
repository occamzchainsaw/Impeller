using Impeller.App.ViewModels.Engine;
using Impeller.App.ViewModels.Sensors;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels;

/// <summary>
/// Every sensor the engine can see, across all providers.
/// </summary>
/// <remarks>
/// Mostly a diagnostic page, and a useful one: "does the engine see my water temperature" is the
/// first question behind half of what goes wrong, and it is answerable here in one search.
/// </remarks>
public sealed partial class SensorsViewModel(EngineConnection connection)
    : EnginePageViewModel(connection)
{
    /// <inheritdoc />
    public override string Title => "Sensors";

    /// <summary>The sensors, grouped by hardware and filtered by the search box.</summary>
    public SensorTreeViewModel Tree { get; } = new();

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot) => Tree.Load(snapshot.Sensors);

    /// <inheritdoc />
    protected override void OnTick(TickSnapshot tick) => Tree.Apply(tick);
}
