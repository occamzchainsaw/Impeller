namespace Impeller.EngineService;

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
