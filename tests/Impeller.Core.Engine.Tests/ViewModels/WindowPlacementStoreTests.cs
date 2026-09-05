using Impeller.App.ViewModels.Shell;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// The file that carries the window's position from one run to the next.
/// </summary>
public sealed class WindowPlacementStoreTests : IDisposable
{
    private readonly string _folder;
    private readonly string _file;

    public WindowPlacementStoreTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"impeller-window-{Guid.NewGuid():N}");
        _file = Path.Combine(_folder, "window.json");
    }

    [Fact]
    public void A_placement_comes_back_the_way_it_went_in()
    {
        var placement = new WindowPlacement(1700, 240, 1400, 900, Maximized: true);

        new WindowPlacementStore(_file).Write(placement);

        Assert.Equal(placement, new WindowPlacementStore(_file).Read());
    }

    [Fact]
    public void A_first_run_has_nothing_to_read()
    {
        Assert.Null(new WindowPlacementStore(_file).Read());
    }

    [Fact]
    public void A_corrupt_file_reads_as_a_first_run()
    {
        // Not a crash on startup. Whatever went wrong here, the window still has to open.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_file, "{ this is not json");

        Assert.Null(new WindowPlacementStore(_file).Read());
    }

    [Fact]
    public void A_stored_size_of_nothing_reads_as_a_first_run()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_file, """{"x":10,"y":10,"width":0,"height":0,"maximized":false}""");

        Assert.Null(new WindowPlacementStore(_file).Read());
    }

    [Fact]
    public void A_place_that_cannot_be_written_to_is_not_worth_failing_over()
    {
        // A file where the folder should be, which is the shape of a profile that has gone wrong.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "blocked"), string.Empty);

        var store = new WindowPlacementStore(Path.Combine(_folder, "blocked", "window.json"));

        store.Write(new WindowPlacement(0, 0, 1200, 820, Maximized: false));

        Assert.Null(store.Read());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
