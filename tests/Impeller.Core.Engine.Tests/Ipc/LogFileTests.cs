using System.Text;
using Impeller.Core.Persistence.Diagnostics;

namespace Impeller.Core.Engine.Tests.Ipc;

/// <summary>
/// Covers reading the log back while it is still being written.
/// </summary>
/// <remarks>
/// The concurrent-writer case is the whole point. Serilog holds the current file open for the life
/// of the process, so a reader that opens it exclusively fails on exactly the machine that has
/// something worth reporting.
/// </remarks>
public class LogFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "impeller-log-tests",
        Guid.NewGuid().ToString("N"));

    public LogFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string Write(string name, params string[] lines)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void The_tail_is_the_end_of_the_file_not_the_start()
    {
        Write("engine-20260902.log", [.. Enumerable.Range(1, 500).Select(i => $"line {i}")]);

        var tail = LogFiles.Tail(_folder, "engine-*.log", lines: 10);

        Assert.Equal(10, tail.Count);
        Assert.Equal("line 491", tail[0]);
        Assert.Equal("line 500", tail[^1]);
    }

    [Fact]
    public void A_file_shorter_than_the_tail_comes_back_whole()
    {
        Write("engine-20260902.log", "only", "two");

        Assert.Equal(["only", "two"], LogFiles.Tail(_folder, "engine-*.log", lines: 50));
    }

    [Fact]
    public void The_newest_file_is_the_one_read()
    {
        var old = Write("engine-20260901.log", "yesterday");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));

        Write("engine-20260902.log", "today");

        Assert.Equal(["today"], LogFiles.Tail(_folder, "engine-*.log"));
    }

    [Fact]
    public void A_file_the_engine_still_has_open_can_be_read()
    {
        var path = Path.Combine(_folder, "engine-20260902.log");

        // Held the way a logger holds it: open for writing, for the life of the process.
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(Encoding.UTF8.GetBytes("while open\n"));
        writer.Flush();

        Assert.Equal(["while open"], LogFiles.Tail(_folder, "engine-*.log"));
    }

    [Fact]
    public void No_log_yet_is_not_an_error()
    {
        // A first run reaches this, and a report that throws instead of saying "nothing logged yet"
        // is worse than useless.
        Assert.Empty(LogFiles.Tail(_folder, "engine-*.log"));
        Assert.Empty(LogFiles.Tail(Path.Combine(_folder, "does-not-exist"), "engine-*.log"));
        Assert.Null(LogFiles.Newest(_folder, "engine-*.log"));
    }
}
