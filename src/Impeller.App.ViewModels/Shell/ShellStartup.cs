namespace Impeller.App.ViewModels.Shell;

/// <summary>
/// What the shell's own command line can ask of it.
/// </summary>
/// <remarks>
/// <para>
/// One switch, and it exists because of autostart. Somebody who double-clicks Impeller wants the
/// window. Somebody whose machine launched it at log-in almost never does — they asked for it to
/// start with Windows so the tray icon would be there, not so a window would be sitting over
/// whatever they opened next.
/// </para>
/// <para>
/// The preference lives in the arguments of the <c>Run</c> value rather than in a settings file,
/// and that is what keeps the two readings apart. It travels with the thing it describes, so
/// launching Impeller by hand still opens the window, and there is no second place for the answer
/// to disagree with the registry.
/// </para>
/// <para>
/// Here rather than in the shell so it can be tested, for the same reason
/// <see cref="WindowPlacementPolicy"/> is: deciding what an argument means is arithmetic, and only
/// acting on it needs a window.
/// </para>
/// </remarks>
public static class ShellStartup
{
    /// <summary>The switch that asks the shell to start in the notification area.</summary>
    /// <remarks>
    /// Written this way, read either way. Nothing but Impeller writes this value, but a person
    /// editing their own <c>Run</c> entry spells it however they spell it, and an app that ignores
    /// the other spelling in silence is one they cannot debug.
    /// </remarks>
    public const string MinimisedSwitch = "--minimised";

    /// <summary>
    /// Whether the shell was asked to start hidden.
    /// </summary>
    /// <param name="commandLine">
    /// The command line as <see cref="Environment.GetCommandLineArgs"/> hands it back, executable
    /// path included. The path is skipped here so callers do not have to remember to.
    /// </param>
    public static bool StartsMinimised(IReadOnlyList<string>? commandLine)
    {
        if (commandLine is null)
        {
            return false;
        }

        for (var index = 1; index < commandLine.Count; index++)
        {
            if (IsMinimised(commandLine[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether one argument is the switch.
    /// </summary>
    /// <remarks>
    /// Leading dashes and slashes are stripped rather than matched, because all four forms are
    /// ordinary on Windows and refusing three of them would be a distinction with no purpose.
    /// </remarks>
    private static bool IsMinimised(string? argument) =>
        argument?.Trim().TrimStart('-', '/') is { Length: > 0 } name
        && (name.Equals("minimised", StringComparison.OrdinalIgnoreCase)
            || name.Equals("minimized", StringComparison.OrdinalIgnoreCase));
}
