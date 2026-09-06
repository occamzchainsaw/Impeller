using System.Text.Json;
using System.Text.Json.Serialization;

namespace Impeller.App.ViewModels.Shell;

/// <summary>What the window remembers about how it should behave.</summary>
/// <param name="CheckForUpdates">Whether to ask GitHub, on startup, if there is a newer release.</param>
public sealed record ShellSettings(bool CheckForUpdates)
{
    /// <summary>
    /// What a machine with no settings file gets.
    /// </summary>
    /// <remarks>
    /// Checking is on. It is one request that sends nothing, and the alternative — a fan controller
    /// that silently stays on an old version while the person running it has no way to find out —
    /// is worse. The Settings page says what it does and switches it off in one click.
    /// </remarks>
    public static ShellSettings Default { get; } = new(CheckForUpdates: true);
}

/// <summary>
/// Reads and writes the window's own settings, and never throws at the caller.
/// </summary>
/// <remarks>
/// Beside <c>window.json</c> under the user's local app data, and deliberately nowhere near the
/// engine's state, for the reason <c>ShellState</c> already gives: this describes a person's window,
/// not a machine's fans, and must not travel when a configuration is copied.
///
/// Every failure reads as "the defaults", exactly as the placement store treats a position it cannot
/// read. Nothing here is worth a launch that fails.
/// </remarks>
public sealed class ShellSettingsStore(string path)
{
    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>Where the settings are stored.</summary>
    public string Path => _path;

    /// <summary>What is stored, or the defaults when there is nothing readable.</summary>
    public ShellSettings Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return ShellSettings.Default;
            }

            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));

            return stored is null
                ? ShellSettings.Default
                : new ShellSettings(stored.CheckForUpdates ?? ShellSettings.Default.CheckForUpdates);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return ShellSettings.Default;
        }
    }

    /// <summary>Writes them down. Silent on failure.</summary>
    public void Write(ShellSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var folder = System.IO.Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(new Stored(settings.CheckForUpdates)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cost is a preference that does not survive a restart, which is not worth failing
            // a shutdown over.
        }
    }

    /// <summary>
    /// The file's shape.
    /// </summary>
    /// <remarks>
    /// Nullable, so a setting absent from an older file takes the default rather than false. This
    /// file will gain fields, and every one of them will be missing from every file already written.
    /// </remarks>
    private sealed record Stored(
        [property: JsonPropertyName("checkForUpdates")] bool? CheckForUpdates);
}
