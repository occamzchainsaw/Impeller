namespace Impeller.Core.Persistence;

/// <summary>
/// Decides where this installation keeps its configurations and its identity map.
/// </summary>
/// <remarks>
/// Portable by preference: a folder beside the executable means a copy of the directory is a
/// complete, movable install, which is how the app this replaces works and is worth keeping. But
/// "beside the executable" is not writable under Program Files, and an installer that puts it
/// there must not produce an engine that cannot save. So the location is probed rather than
/// assumed, and the answer is logged at startup — a bug report that names the wrong path costs
/// more to chase than this costs to write.
/// </remarks>
public static class StateLocation
{
    /// <summary>The folder name used in both locations.</summary>
    public const string FolderName = "Configurations";

    /// <summary>The folder logs are written to, alongside the configurations.</summary>
    public const string LogFolderName = "Logs";

    /// <summary>The file mapping this machine's hardware onto the ids configurations reference.</summary>
    public const string IdentityMapName = "sensor-identity.json";

    /// <summary>The file remembering which configuration was last loaded.</summary>
    public const string SelectionName = "selected-configuration.json";

    /// <summary>The file recording which plugins have been seen and what they were granted.</summary>
    public const string PluginsName = "plugins.json";

    /// <summary>The file holding the names the user gave this machine's sensors and fans.</summary>
    public const string NamesName = "names.json";

    /// <summary>
    /// Works out the configuration folder, creating it if necessary.
    /// </summary>
    /// <param name="configured">
    /// An explicit path from configuration, which wins over everything and is not probed — someone
    /// who names a path means it, and silently using a different one would be worse than failing.
    /// </param>
    /// <param name="portable">Set to whether the portable location was chosen.</param>
    public static string Resolve(string? configured, out bool portable)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            portable = false;
            Directory.CreateDirectory(configured);
            return configured;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, FolderName);

        if (IsWritable(beside))
        {
            portable = true;
            return beside;
        }

        portable = false;
        var shared = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Impeller",
            FolderName);

        Directory.CreateDirectory(shared);
        return shared;
    }

    /// <summary>
    /// The folder logs go in, beside wherever the configurations ended up.
    /// </summary>
    /// <remarks>
    /// Deliberately not a fixed shared-app-data path. A portable install is meant to be a directory
    /// you can copy, move and delete; scattering its logs into <c>%ProgramData%</c> would leave
    /// them behind after the folder was gone and would write outside the install on a machine where
    /// that was the whole point. Wherever the state went, the logs go beside it.
    /// </remarks>
    public static string ResolveLogs(string configurationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationRoot);

        var parent = Path.GetDirectoryName(configurationRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));

        var logs = Path.Combine(parent ?? configurationRoot, LogFolderName);

        try
        {
            Directory.CreateDirectory(logs);
            return logs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never worth failing to start over. An engine with no log file still controls fans;
            // the Event Log still records that it came up.
            return configurationRoot;
        }
    }

    /// <summary>
    /// Where the sensor identity map lives: beside the configuration folder, never inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to sit inside, and that was a bug with a visible symptom: the configuration store
    /// lists every JSON file in its folder, so the map appeared in the list of saved configurations
    /// and in the shell's dropdown, where choosing it fails to load. Excluding it by name would have
    /// hidden the symptom and left a machine-state file living among the user's documents.
    /// </para>
    /// <para>
    /// It does not belong there on its own terms either. A configuration is portable between
    /// machines; the identity map is the one file that is emphatically not, because it records which
    /// synthetic id <em>this</em> PC assigned to which physical sensor. Copying a configuration
    /// folder to another machine should carry the curves and leave the identities behind.
    /// </para>
    /// </remarks>
    public static string ResolveIdentityMap(string configurationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationRoot);

        var beside = Beside(configurationRoot, IdentityMapName);
        var legacy = Path.Combine(configurationRoot, IdentityMapName);

        if (File.Exists(beside) || !File.Exists(legacy))
        {
            return beside;
        }

        try
        {
            // Moved rather than left in place, and moved once. This file is the identity of every
            // sensor on the machine: losing it silently repoints every curve in every configuration
            // at whatever gets enumerated first next time.
            File.Move(legacy, beside);
            return beside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep reading the one that exists. A tidier path is not worth a machine that forgets
            // which fan is which.
            return legacy;
        }
    }

    /// <summary>
    /// Where the record of which configuration is loaded lives.
    /// </summary>
    /// <remarks>
    /// Beside the folder rather than in it, like everything else that describes the installation
    /// rather than a configuration's contents — and, concretely, because anything ending in
    /// <c>.json</c> inside that folder is listed to the user as a configuration they could load.
    /// </remarks>
    public static string ResolveSelection(string configurationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationRoot);
        return Beside(configurationRoot, SelectionName);
    }

    /// <summary>
    /// Where the record of plugin approvals and grants lives.
    /// </summary>
    /// <remarks>
    /// Beside the configuration folder, like everything else describing the installation rather
    /// than a configuration's contents, and for the concrete reason the identity map taught us:
    /// anything ending in <c>.json</c> inside that folder is offered to the user as a configuration
    /// they could load. It also must not travel: a configuration copied to another machine carries
    /// curves, and must not carry that machine's decisions about which programs may drive its fans.
    /// </remarks>
    public static string ResolvePlugins(string configurationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationRoot);
        return Beside(configurationRoot, PluginsName);
    }

    /// <summary>
    /// The names the user gave this machine's fans and sensors.
    /// </summary>
    /// <remarks>
    /// Beside the configurations for two reasons at once. Anything ending in <c>.json</c> inside
    /// that folder is offered as a configuration to load; and a name describes this PC's hardware,
    /// so it must survive switching profiles and must not travel when one is copied elsewhere.
    /// </remarks>
    public static string ResolveNames(string configurationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationRoot);
        return Beside(configurationRoot, NamesName);
    }

    /// <summary>The path a file takes when it belongs next to the configuration folder, not inside it.</summary>
    private static string Beside(string configurationRoot, string fileName)
    {
        var parent = Path.GetDirectoryName(configurationRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));

        return Path.Combine(parent ?? configurationRoot, fileName);
    }

    /// <summary>
    /// Whether a folder can actually be written to, tested by writing to it.
    /// </summary>
    /// <remarks>
    /// Inspecting the ACL would be the tidier-looking check and the wrong one: the service runs as
    /// LocalSystem, virtualisation and policy both intervene, and the only question that matters is
    /// whether a write succeeds.
    /// </remarks>
    private static bool IsWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            var probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
