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
}
