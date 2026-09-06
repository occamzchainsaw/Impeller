namespace Impeller.App.ViewModels.Updates;

/// <summary>A published release, as far as this app cares.</summary>
/// <param name="Version">The version, with any leading <c>v</c> already stripped.</param>
/// <param name="Name">What the release is called, for showing beside the number.</param>
/// <param name="Url">Where a person goes to get it.</param>
public sealed record ReleaseInfo(string Version, string Name, string Url);

/// <summary>
/// Where to ask what the latest release is.
/// </summary>
/// <remarks>
/// An interface for one method, so the check can be tested without a network. That is not
/// ceremony: the interesting cases here are all failures — offline, rate-limited, a repository
/// that does not exist yet — and none of them can be produced on demand against the real thing.
/// </remarks>
public interface IReleaseFeed
{
    /// <summary>
    /// The most recent release, or <see langword="null"/> when there is nothing to report.
    /// </summary>
    /// <remarks>
    /// Null covers every failure as well as "no releases". An update check is a convenience, and a
    /// convenience that produces error messages about its own plumbing is worse than one that stays
    /// quiet.
    /// </remarks>
    Task<ReleaseInfo?> LatestAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Whether a release is worth telling somebody about.
/// </summary>
/// <remarks>
/// Kept apart from the feed so the comparison — which is where the mistakes are — can be tested
/// exhaustively without either a network or a fake.
/// </remarks>
public static class UpdateCheck
{
    /// <summary>
    /// Whether <paramref name="candidate"/> is a newer release than <paramref name="running"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unparseable on either side means no. A version string this code does not understand is not
    /// evidence of anything, and guessing produces an update prompt that never goes away — which
    /// trains people to ignore the one that matters.
    /// </para>
    /// <para>
    /// Prereleases are refused as candidates. Somebody running 0.1.0 should not be sent to
    /// 0.2.0-beta.1 by a background check they did not ask for.
    /// </para>
    /// </remarks>
    public static bool IsNewer(string? running, string? candidate) =>
        TryParse(running, out var here)
        && TryParse(candidate, out var there)
        && !IsPrerelease(candidate)
        && there > here;

    /// <summary>
    /// Reads a version out of a tag, tolerating the shapes releases actually use.
    /// </summary>
    /// <remarks>
    /// A leading <c>v</c> is conventional on a git tag and meaningless here. Anything after a
    /// <c>-</c> or <c>+</c> is a prerelease or build label, which <see cref="Version"/> cannot
    /// parse and which does not affect ordering between releases.
    /// </remarks>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().TrimStart('v', 'V');
        var cut = trimmed.IndexOfAny(['-', '+']);

        if (cut >= 0)
        {
            trimmed = trimmed[..cut];
        }

        return Version.TryParse(trimmed, out version!);
    }

    /// <summary>Whether a tag names something not meant for everybody.</summary>
    public static bool IsPrerelease(string? text) =>
        text is not null && text.Contains('-', StringComparison.Ordinal);
}
