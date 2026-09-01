namespace Impeller.EngineService;

/// <summary>Tunables for the engine host, bound from configuration.</summary>
public sealed class EngineOptions
{
    /// <summary>The configuration section these are bound from.</summary>
    public const string SectionName = "Engine";

    /// <summary>
    /// How often the control loop runs.
    /// </summary>
    /// <remarks>
    /// One second matches what fan control actually needs: thermal mass means nothing useful
    /// changes faster, and polling harder mostly costs SMBus traffic. Curves and ramp limits are
    /// written against elapsed time rather than a tick count, so this can be retuned safely.
    /// </remarks>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a tick may overrun before the previous one is considered hung. On expiry the
    /// engine engages the failsafe rather than leaving fans at a duty nobody is maintaining.
    /// </summary>
    public TimeSpan TickTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Where configuration files live. Defaults to a folder beside the executable so a portable
    /// install keeps its configs with it.
    /// </summary>
    public string? ConfigurationPath { get; set; }

    /// <summary>
    /// Which named configuration to load at startup.
    /// </summary>
    /// <remarks>
    /// Generated from the hardware present, with every control disabled, when it does not exist
    /// yet. A first run leaves the machine exactly as it found it.
    /// </remarks>
    public string ConfigurationName { get; set; } = "Default";

    /// <summary>
    /// Whether to drive controls to their failsafe duty when the engine stops normally.
    /// </summary>
    /// <remarks>
    /// On by default. Leaving fans at whatever the last curve output happened to be is only safe
    /// while something is still maintaining them, and after shutdown nothing is.
    /// </remarks>
    public bool FailsafeOnShutdown { get; set; } = true;
}
