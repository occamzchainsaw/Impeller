using System.ComponentModel;
using System.Runtime.Versioning;
using Impeller.Platform.Windows;

namespace Impeller.EngineService;

/// <summary>
/// The engine executable's management verbs: <c>install</c>, <c>uninstall</c>, <c>start</c>,
/// <c>stop</c> and <c>status</c>.
/// </summary>
/// <remarks>
/// Kept in the engine binary rather than a separate installer so the path registered with the SCM
/// is necessarily the path of the thing being registered. A standalone installer has to be told
/// where the engine lives, and that is one more thing to get wrong on an upgrade or a move.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ServiceCommandLine
{
    /// <summary>
    /// Handles a management verb if one was given.
    /// </summary>
    /// <param name="args">The process command line.</param>
    /// <returns>
    /// The exit code to return, or <see langword="null"/> when no verb was present and the process
    /// should go on to run the engine.
    /// </returns>
    public static int? TryHandle(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return null;
        }

        var verb = args[0].TrimStart('-', '/').ToLowerInvariant();

        try
        {
            return verb switch
            {
                "install" => Install(),
                "uninstall" or "remove" => Uninstall(),
                "start" => Start(),
                "stop" => Stop(),
                "status" => Status(),
                "help" or "?" => Help(),
                _ => null,
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Re-run this command from an elevated prompt.");
            return 5;
        }
        catch (Win32Exception ex)
        {
            Console.Error.WriteLine($"{ex.Message} (error {ex.NativeErrorCode})");
            return ex.NativeErrorCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Install()
    {
        if (ServiceControlManager.IsInstalled())
        {
            Console.WriteLine($"{ServiceControlManager.DisplayName} is already installed.");
            return 0;
        }

        var path = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the engine executable path.");

        ServiceControlManager.Install(path);

        Console.WriteLine($"Installed {ServiceControlManager.DisplayName}.");
        Console.WriteLine($"  Binary:  {path}");
        Console.WriteLine("  Account: LocalSystem");
        Console.WriteLine("  Start:   automatic");
        Console.WriteLine("  Restart: after 1 min, then 5, then 10 (counter resets hourly)");
        Console.WriteLine();
        Console.WriteLine($"Start it with: \"{path}\" start");
        return 0;
    }

    private static int Uninstall()
    {
        if (ServiceControlManager.Uninstall())
        {
            Console.WriteLine($"Removed {ServiceControlManager.DisplayName}.");
            Console.WriteLine("Fans were returned to their failsafe duty before the service stopped.");
            return 0;
        }

        Console.WriteLine($"{ServiceControlManager.DisplayName} is not installed.");
        return 0;
    }

    private static int Start()
    {
        Console.WriteLine(ServiceControlManager.Start()
            ? $"Started {ServiceControlManager.DisplayName}."
            : $"{ServiceControlManager.DisplayName} was already running.");
        return 0;
    }

    private static int Stop()
    {
        Console.WriteLine(ServiceControlManager.Stop()
            ? $"Stopped {ServiceControlManager.DisplayName}."
            : $"{ServiceControlManager.DisplayName} was not running.");
        return 0;
    }

    private static int Status()
    {
        var state = ServiceControlManager.Query();

        Console.WriteLine(state is null
            ? $"{ServiceControlManager.DisplayName} is not installed."
            : $"{ServiceControlManager.DisplayName}: {state}");

        return 0;
    }

    private static int Help()
    {
        Console.WriteLine($"{ServiceControlManager.DisplayName}");
        Console.WriteLine();
        Console.WriteLine("  install     Register as a Windows service (LocalSystem, automatic start)");
        Console.WriteLine("  uninstall   Stop and remove the service");
        Console.WriteLine("  start       Start the installed service");
        Console.WriteLine("  stop        Stop the installed service");
        Console.WriteLine("  status      Report whether the service is installed and running");
        Console.WriteLine();
        Console.WriteLine("With no arguments, runs the engine in the foreground.");
        Console.WriteLine("install, uninstall, start and stop require elevation.");
        return 0;
    }
}
