using Impeller.App.ViewModels.Engine;
using Impeller.App.ViewModels.Sensors;
using Impeller.Core.Abstractions;
using Impeller.Ipc.Contracts;

namespace Impeller.App.ViewModels;

/// <summary>
/// Every sensor the engine can see, across all providers.
/// </summary>
/// <remarks>
/// Mostly a diagnostic page, and a useful one: "does the engine see my water temperature" is the
/// first question behind half of what goes wrong, and it is answerable here in one search.
/// </remarks>
public sealed partial class SensorsViewModel : EnginePageViewModel
{
    public SensorsViewModel(EngineConnection connection)
        : base(connection) =>
        Tree.Rename = RenameAsync;

    /// <inheritdoc />
    public override string Title => "Sensors";

    /// <summary>The sensors, grouped by hardware and filtered by the search box.</summary>
    public SensorTreeViewModel Tree { get; } = new();

    /// <summary>
    /// Gives a sensor the user's own name.
    /// </summary>
    /// <remarks>
    /// The engine stores it and announces the change, so the tree reloads from the fresh snapshot
    /// rather than this page guessing at what it now looks like.
    /// </remarks>
    private async Task RenameAsync(SensorId id, string? name)
    {
        if (Connection.Engine is { } engine)
        {
            try
            {
                await engine.RenameAsync(id, name).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // The reconnect loop reports a lost connection properly; a second account of the
                // same fact on this page would help nobody.
            }
        }
    }

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot) => Tree.Load(snapshot.Sensors);

    /// <inheritdoc />
    protected override void OnTick(TickSnapshot tick) => Tree.Apply(tick);
}
