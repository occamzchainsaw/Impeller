using Impeller.App.ViewModels.Shell;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// Reading the one switch the shell takes on its command line.
/// </summary>
/// <remarks>
/// The failure this guards against is quiet in both directions: a switch that is not recognised
/// opens a window the user asked not to see, and one recognised where it was not written hides a
/// window they double-clicked. Neither produces an error anywhere.
/// </remarks>
public sealed class ShellStartupTests
{
    /// <summary>What Windows actually launches, quoted path and all.</summary>
    private const string Exe = @"C:\00 PORTABLE SOFT\Impeller\Impeller.exe";

    [Fact]
    public void An_ordinary_launch_opens_the_window() =>
        Assert.False(ShellStartup.StartsMinimised([Exe]));

    [Fact]
    public void The_switch_is_recognised() =>
        Assert.True(ShellStartup.StartsMinimised([Exe, ShellStartup.MinimisedSwitch]));

    /// <summary>
    /// The other spelling, and the other prefixes.
    /// </summary>
    /// <remarks>
    /// Nothing but Impeller writes this value, but the whole point of putting it in the registry
    /// where Task Manager shows it is that a person can edit it — and they will spell it their way.
    /// </remarks>
    [Theory]
    [InlineData("--minimised")]
    [InlineData("--minimized")]
    [InlineData("-minimised")]
    [InlineData("/minimized")]
    [InlineData("--MINIMISED")]
    public void Every_reasonable_way_of_writing_it_is_accepted(string argument) =>
        Assert.True(ShellStartup.StartsMinimised([Exe, argument]));

    [Fact]
    public void Something_else_entirely_is_ignored() =>
        Assert.False(ShellStartup.StartsMinimised([Exe, "--verbose", "--maximised"]));

    /// <summary>
    /// The executable is never read as an argument.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.GetCommandLineArgs"/> puts the program's own path first, and a folder
    /// somebody named after the switch would otherwise hide their window every log-in with nothing
    /// to point at.
    /// </remarks>
    [Fact]
    public void The_program_path_is_not_an_argument() =>
        Assert.False(ShellStartup.StartsMinimised([@"C:\minimised\Impeller.exe"]));

    [Fact]
    public void No_command_line_at_all_opens_the_window()
    {
        Assert.False(ShellStartup.StartsMinimised(null));
        Assert.False(ShellStartup.StartsMinimised([]));
    }
}
