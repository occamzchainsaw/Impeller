using Impeller.Core.Persistence;

namespace Impeller.EngineService;

/// <summary>Where this installation keeps its state, and whether that is the portable location.</summary>
/// <param name="ConfigurationRoot">The folder holding configurations and the identity map.</param>
/// <param name="Portable">
/// Whether it sits beside the executable rather than in shared app data. Reported to clients so a
/// bug report names the path this install actually used rather than the one it would prefer.
/// </param>
public sealed record EngineStatePaths(string ConfigurationRoot, bool Portable)
{
    /// <summary>The folder log files are written to.</summary>
    public string LogRoot { get; init; } = StateLocation.ResolveLogs(ConfigurationRoot);

    /// <summary>The sensor identity map, which sits beside the configurations rather than among them.</summary>
    public string IdentityMapPath { get; init; } = StateLocation.ResolveIdentityMap(ConfigurationRoot);

    /// <summary>Where the choice of configuration is remembered across restarts.</summary>
    public string SelectionPath { get; init; } = StateLocation.ResolveSelection(ConfigurationRoot);

    /// <summary>
    /// Which programs may drive this machine's fans. Installation state, not user state.
    /// </summary>
    /// <remarks>
    /// Beside the configurations rather than among them, for the reason Phase 1 established the
    /// hard way: anything ending in <c>.json</c> inside that folder is offered to the user as a
    /// configuration they could load. A configuration copied to another machine must also not
    /// carry this machine's plugin approvals with it.
    /// </remarks>
    public string PluginsPath { get; init; } = StateLocation.ResolvePlugins(ConfigurationRoot);
}
