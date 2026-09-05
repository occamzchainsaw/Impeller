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

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);

            return Executable() is { Length: > 0 } exe
                && key?.GetValue(name) is string value
                && value.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Turns autostart on, or off.</summary>
    /// <param name="name">The value name, which is what Task Manager shows.</param>
    /// <param name="enabled">Whether it should start with Windows.</param>
    /// <returns>Whether the change was made.</returns>
    /// <remarks>
    /// Never throws. A registry hive that will not open is a policy-managed machine or a corrupted
    /// profile, and neither is a reason for a fan controller to fail to start — the caller shows
    /// the toggle as it actually is and moves on.
    /// </remarks>
    public static bool Set(string name, bool enabled)
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
            key.SetValue(name, $"\"{exe}\"");
            return true;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string Executable() => Environment.ProcessPath ?? string.Empty;
}
