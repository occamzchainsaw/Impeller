using CommunityToolkit.Mvvm.ComponentModel;

namespace Impeller.App.ViewModels;

/// <summary>
/// The fan and sensor overview: the page the app opens on and the one people leave open.
/// </summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    /// <inheritdoc />
    public override string Title => "Dashboard";

    /// <summary>
    /// A human-readable summary of the engine connection, shown while there is nothing else to display.
    /// </summary>
    /// <remarks>
    /// The shell has no channel to the engine service yet, so this reports the honest state
    /// rather than an empty dashboard that looks like a machine with no fans.
    /// </remarks>
    [ObservableProperty]
    public partial string EngineStatus { get; set; } = "Not connected to the engine service.";
}
