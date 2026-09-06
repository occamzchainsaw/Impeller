using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Impeller.Platform.Windows;

/// <summary>
/// Whether a user application starts when the user logs in.
/// </summary>
/// <remarks>
/// <para>
/// An ordinary <c>HKCU\...\Run</c> value, which is the whole mechanism. It needs no elevation, it
/// is per-user, and it is visible to the person who set it: Task Manager's Startup tab lists it and
/// can switch it off, which matters because an app that installs an autostart the user cannot find
/// in the usual place is an app behaving badly.
/// </para>
/// <para>
/// Deliberately not a scheduled task, and deliberately not <c>HKLM</c>. Both exist to launch
/// something elevated or for every user, and neither applies here: the engine is the elevated part
/// and it is a service, which has its own start-at-boot setting. The shell and any plugin app run
/// as the user.
/// </para>
/// <para>
/// This is separate from the engine's own autostart. The service starts at boot whether or not
/// anyone logs in — which is the point of it being a service, since fans need managing on a machine
/// sitting at the login screen — while this only decides whether the window and its tray icon come
/// back with the desktop.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Whether a Run value with this name is present and points at this executable.</summary>
    /// <param name="name">The value name, which is what Task Manager shows.</param>
    /// <remarks>
    /// The path is compared as well as the name, so a value left behind by a copy running from
    /// somewhere else reads as "not enabled" here rather than as this build's doing. Switching it
    /// on then rewrites it to this executable, which is the repair a user expects from a toggle.
    /// </remarks>
    public static bool IsEnabled(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Executable() is { Length: > 0 } exe
            && Value(name) is { } value
            && value.Contains(exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the registered command line carries a particular switch.
    /// </summary>
    /// <param name="name">The value name, which is what Task Manager shows.</param>
    /// <param name="argument">The switch to look for, as it is written.</param>
    /// <remarks>
    /// How a preference that only applies to the log-in launch is stored: as an argument on the
    /// value that performs that launch. It cannot drift out of step with autostart being on,
    /// because switching autostart off deletes the value that holds it, and it cannot affect a
    /// launch the user started by hand, because that launch has a different command line.
    /// </remarks>
    public static bool HasArgument(string name, string argument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);

        return IsEnabled(name)
            && Value(name) is { } value
            && Arguments(value).Any(present =>
                string.Equals(present, argument, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Turns autostart on, or off.</summary>
    /// <param name="name">The value name, which is what Task Manager shows.</param>
    /// <param name="enabled">Whether it should start with Windows.</param>
    /// <param name="arguments">What to launch it with, or null for nothing.</param>
    /// <returns>Whether the change was made.</returns>
    /// <remarks>
    /// Never throws. A registry hive that will not open is a policy-managed machine or a corrupted
    /// profile, and neither is a reason for a fan controller to fail to start — the caller shows
    /// the toggle as it actually is and moves on.
    /// </remarks>
    public static bool Set(string name, bool enabled, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            if (key is null)
            {
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                return true;
            }

            if (Executable() is not { Length: > 0 } exe)
            {
                return false;
            }

            // Quoted, because Program Files has a space in it and an unquoted path there is a
            // well-known way to end up launching something else entirely.
            var tail = string.IsNullOrWhiteSpace(arguments) ? string.Empty : $" {arguments.Trim()}";

            key.SetValue(name, $"\"{exe}\"{tail}");
            return true;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string Executable() => Environment.ProcessPath ?? string.Empty;

    /// <summary>The raw Run value, or null when there is not one to read.</summary>
    private static string? Value(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);

            return key?.GetValue(name) as string;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Everything after the executable.
    /// </summary>
    /// <remarks>
    /// Only reads arguments off a quoted path, which is the only shape <see cref="Set"/> writes. A
    /// value someone has hand-edited into the unquoted form reads as having no arguments, which is
    /// the conservative answer: guessing where an unquoted path with spaces in it ends is how the
    /// quoting rule earned its place to begin with.
    /// </remarks>
    private static string[] Arguments(string value)
    {
        var end = value.StartsWith('"') ? value.IndexOf('"', 1) : -1;
        var tail = end > 0 ? value[(end + 1)..] : string.Empty;

        return tail.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
