namespace Impeller.App.Shell;

/// <summary>
/// Where the shell keeps what it knows about itself.
/// </summary>
/// <remarks>
/// Under the user's local app data, and deliberately nowhere near the engine's state. Everything
/// here describes a person's window rather than a machine's fans: two people signed in to one PC
/// share the cooling and share nothing else, and none of it should travel when a configuration is
/// copied to another machine.
/// </remarks>
internal static class ShellState
{
    /// <summary>The folder the shell writes its own files to.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Impeller");

    /// <summary>Where the window's size and position are remembered between runs.</summary>
    public static string WindowFile => Path.Combine(Root, "window.json");
}
