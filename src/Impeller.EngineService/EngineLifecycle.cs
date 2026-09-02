namespace Impeller.EngineService;

/// <summary>
/// The three things the Event Log is still for.
/// </summary>
/// <remarks>
/// <para>
/// Everything the engine does goes to its log file. These three do not: whether the service
/// started, whether it stopped, and whether it fell over. Those are what someone opens Event Viewer
/// to find, and they are the ones that have to be readable when there is no log file — because a
/// service that cannot write its log is precisely the case being diagnosed.
/// </para>
/// <para>
/// They share a category of their own so the Event Log provider can be filtered to warnings
/// everywhere else and still let these through at information level. Severity then means what it
/// says, rather than being inflated to get a message past a filter.
/// </para>
/// </remarks>
public static partial class EngineLifecycle
{
    /// <summary>The logger category these are written under, and the one the filter names.</summary>
    public const string Category = "Impeller.Engine.Lifecycle";

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Information,
        Message = "Impeller engine starting. State in {ConfigurationRoot}, logs in {LogRoot}.")]
    public static partial void Starting(ILogger logger, string configurationRoot, string logRoot);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Impeller engine stopped.")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(
        EventId = 102,
        Level = LogLevel.Critical,
        Message = "Impeller engine terminated unexpectedly. Fans have been left to their firmware.")]
    public static partial void Faulted(ILogger logger, Exception exception);
}
