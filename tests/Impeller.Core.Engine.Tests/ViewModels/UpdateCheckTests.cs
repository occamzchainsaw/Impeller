using Impeller.App.ViewModels.Updates;

namespace Impeller.Core.Engine.Tests.ViewModels;

/// <summary>
/// Deciding whether a published release is worth telling somebody about.
/// </summary>
/// <remarks>
/// Both ways of getting this wrong are bad in the same direction. A check that never fires leaves
/// people on an old build believing they are current; a check that always fires becomes a prompt
/// they learn to dismiss, which is the same thing with extra steps. Every case here is a tag shape
/// that actually appears on release pages.
/// </remarks>
public sealed class UpdateCheckTests
{
    [Theory]
    [InlineData("0.1.0", "0.2.0")]
    [InlineData("0.1.0", "v0.2.0")]
    [InlineData("0.1.0", "1.0.0")]
    [InlineData("0.1.0", "0.1.1")]
    [InlineData("1.2.3.4", "1.2.4")]
    public void A_higher_version_is_offered(string running, string candidate) =>
        Assert.True(UpdateCheck.IsNewer(running, candidate));

    [Theory]
    [InlineData("0.2.0", "0.1.0")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0", "v0.1.0")]
    [InlineData("1.0.0", "0.9.9")]
    public void The_same_or_older_is_not(string running, string candidate) =>
        Assert.False(UpdateCheck.IsNewer(running, candidate));

    /// <summary>
    /// A background check nobody asked for should not push anyone onto a prerelease.
    /// </summary>
    /// <remarks>
    /// GitHub's <c>releases/latest</c> excludes prereleases already, so this is the second of two
    /// defences. The first can be changed by someone clicking a checkbox on a release page; this
    /// one cannot.
    /// </remarks>
    [Theory]
    [InlineData("0.2.0-beta.1")]
    [InlineData("v0.2.0-rc1")]
    [InlineData("0.2.0-alpha")]
    public void A_prerelease_is_never_offered(string candidate) =>
        Assert.False(UpdateCheck.IsNewer("0.1.0", candidate));

    /// <summary>
    /// Anything unparseable means no.
    /// </summary>
    /// <remarks>
    /// The failure to avoid is an update prompt that cannot be satisfied, because the version it
    /// names will never compare equal to anything. One of those is worse than never checking at
    /// all: it teaches people that this app's notifications are noise.
    /// </remarks>
    [Theory]
    [InlineData("0.1.0", "")]
    [InlineData("0.1.0", null)]
    [InlineData("0.1.0", "latest")]
    [InlineData("0.1.0", "release-2026-09")]
    [InlineData(null, "0.2.0")]
    [InlineData("", "0.2.0")]
    public void Nonsense_on_either_side_is_not_an_update(string? running, string? candidate) =>
        Assert.False(UpdateCheck.IsNewer(running, candidate));

    /// <summary>Build metadata is a label, not a version, and must not read as one.</summary>
    [Fact]
    public void Build_metadata_does_not_make_a_release_newer() =>
        Assert.False(UpdateCheck.IsNewer("0.1.0", "0.1.0+abc1234"));

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData(" 1.2.3 ", 1, 2, 3)]
    [InlineData("1.2.3-beta", 1, 2, 3)]
    [InlineData("1.2.3+meta", 1, 2, 3)]
    public void Tags_are_read_the_way_release_pages_write_them(
        string tag,
        int major,
        int minor,
        int build)
    {
        Assert.True(UpdateCheck.TryParse(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }
}
