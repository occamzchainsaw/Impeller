using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Impeller.App.Shell;

/// <summary>
/// Where the shell writes its own log.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the engine's, and per-user rather than machine-wide, because the two answer
/// different questions. The engine's log says what happened to the fans. This one says what the
/// window was told and when it lost the connection — which is the half of a "it says not connected"
/// report that the engine, by definition, cannot contain.
/// </para>
/// <para>
/// Under the user's local app data because the shell runs unelevated and there is one log per
/// person signed in, not one per machine.
/// </para>
/// </remarks>
public static class ShellLogging
{
    /// <summary>The folder shell logs are written to.</summary>
    public static string LogRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Impeller",
        "Logs");

    /// <summary>Adds logging to the shell's container.</summary>
    /// <remarks>
    /// Failing to open a log file must not stop the app. Somebody whose profile is on a full or
    /// read-only disk still gets a working fan controller; they just get no log, which is a smaller
    /// problem than the one they would otherwise have.
    /// </remarks>
    public static IServiceCollection AddShellLogging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            Directory.CreateDirectory(LogRoot);

            Serilog.Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(LogEventLevel.Debug)
                .MinimumLevel.Override("StreamJsonRpc", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    Path.Combine(LogRoot, "shell-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 8L * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    buffered: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(2),
                    outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Logger = Serilog.Core.Logger.None;
        }

        return services.AddLogging(builder => builder.AddSerilog(dispose: true));
    }

    /// <summary>Flushes anything still buffered. Called as the window closes.</summary>
    public static void Close() => Serilog.Log.CloseAndFlush();
}
