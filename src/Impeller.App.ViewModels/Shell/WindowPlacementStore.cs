using System.Text.Json;
using System.Text.Json.Serialization;

namespace Impeller.App.ViewModels.Shell;

/// <summary>
/// Remembers where the window was between runs.
/// </summary>
/// <remarks>
/// <para>
/// Per user rather than per machine, and beside the shell's own log rather than with the engine's
/// state, for the reason the log gives: two people signed in to one PC have one set of fans and two
/// sets of windows. It is also the only piece of shell state that has nothing to do with the
/// engine, so a configuration copied elsewhere must not carry it.
/// </para>
/// <para>
/// Every failure reads as "nothing remembered", exactly as the engine's own selection file does.
/// A window position is the least important thing this app knows, and no part of it is worth a
/// launch that fails or a shutdown that throws.
/// </para>
/// </remarks>
public sealed class WindowPlacementStore(string path)
{
    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>Where the placement is stored.</summary>
    public string Path => _path;

    /// <summary>
    /// The placement last written, or <see langword="null"/> if there is none to be had.
    /// </summary>
    public WindowPlacement? Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));

            if (stored is null || stored.Width <= 0 || stored.Height <= 0)
            {
                return null;
            }

            return new WindowPlacement(stored.X, stored.Y, stored.Width, stored.Height, stored.Maximized);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes down where the window is now.
    /// </summary>
    public void Write(WindowPlacement placement)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(new Stored(
                    placement.X,
                    placement.Y,
                    placement.Width,
                    placement.Height,
                    placement.Maximized)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to do about it, and nothing worth failing over. The cost is that the window
            // opens centred next time, which is where it opens on a first run anyway.
        }
    }

    private sealed record Stored(
        [property: JsonPropertyName("x")] int X,
        [property: JsonPropertyName("y")] int Y,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height,
        [property: JsonPropertyName("maximized")] bool Maximized);
}
