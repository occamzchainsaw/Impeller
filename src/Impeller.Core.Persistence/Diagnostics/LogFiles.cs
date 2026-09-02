using System.Text;

namespace Impeller.Core.Persistence.Diagnostics;

/// <summary>
/// Reads back what the engine has been writing to its log.
/// </summary>
/// <remarks>
/// The awkward part is that the log is open. Serilog holds the current file for the life of the
/// process, so anything reading it has to say up front that it tolerates a concurrent writer —
/// otherwise a diagnostic report fails precisely on the machine that has something to report.
/// </remarks>
public static class LogFiles
{
    /// <summary>How many lines of tail a report carries when no other number is asked for.</summary>
    public const int DefaultTailLines = 200;

    /// <summary>The newest log file in a folder, or null when nothing has been written yet.</summary>
    public static FileInfo? Newest(string folder, string pattern)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        return new DirectoryInfo(folder)
            .EnumerateFiles(pattern)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// The last lines of the newest log file.
    /// </summary>
    /// <remarks>
    /// Reads the whole file and keeps the tail rather than seeking backwards. Retention caps the
    /// file at a size where that is not worth optimising, and a backwards reader has to handle
    /// partial UTF-8 sequences at whatever offset it lands on — complexity bought with nothing.
    /// </remarks>
    public static IReadOnlyList<string> Tail(string folder, string pattern, int lines = DefaultTailLines)
    {
        if (Newest(folder, pattern) is not { } file)
        {
            return [];
        }

        try
        {
            // ReadWrite sharing, because the writer still has it open.
            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var reader = new StreamReader(stream, Encoding.UTF8);

            var tail = new Queue<string>(lines);

            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == lines)
                {
                    tail.Dequeue();
                }

                tail.Enqueue(line);
            }

            return [.. tail];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A report that says it could not read the log is more use than one that fails.
            return [$"<the log at {file.FullName} could not be read: {ex.Message}>"];
        }
    }
}
