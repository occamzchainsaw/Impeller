using System.Text.Json;
using System.Text.Json.Serialization;

namespace Impeller.Core.Persistence;

/// <summary>
/// Remembers which configuration the engine was last told to run.
/// </summary>
/// <remarks>
/// <para>
/// Without this the engine comes back on whatever the settings file names — "Default" — after every
/// restart and every reboot. Switching configuration would appear to work and then quietly undo
/// itself the next morning, which is the kind of bug people blame on themselves.
/// </para>
/// <para>
/// Kept beside the configurations rather than inside one, for the same reason the identity map is:
/// it describes this installation, not any configuration's contents, and a configuration copied to
/// another machine should not bring along which one that machine ought to be running.
/// </para>
/// </remarks>
public sealed class SelectedConfiguration(string path)
{
    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>Where the choice is stored.</summary>
    public string Path => _path;

    /// <summary>
    /// The configuration last loaded, or <see langword="null"/> if none has been.
    /// </summary>
    /// <remarks>
    /// Any failure reads as "nothing remembered". A corrupt or unreadable file must not stop the
    /// engine starting: falling back to the default configuration cools the machine, and refusing
    /// to start does not.
    /// </remarks>
    public string? Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
            return string.IsNullOrWhiteSpace(stored?.Name) ? null : stored.Name;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records which configuration is now in force.
    /// </summary>
    /// <remarks>
    /// Failure is swallowed. Not being able to write this costs the choice at the next restart,
    /// which is worth far less than the running engine that would be taken down by throwing.
    /// </remarks>
    public void Write(string name)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(new Stored(name)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to do about it, and nothing worth failing over.
        }
    }

    private sealed record Stored([property: JsonPropertyName("name")] string Name);
}
