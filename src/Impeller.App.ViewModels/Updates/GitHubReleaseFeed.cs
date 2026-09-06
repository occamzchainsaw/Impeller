using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Impeller.App.ViewModels.Updates;

/// <summary>
/// Asks GitHub what the latest release is.
/// </summary>
/// <remarks>
/// <para>
/// One unauthenticated GET against the public releases API. Nothing is sent but the request: no
/// identifier, no version, no machine details, and nothing that would distinguish one person asking
/// from another. That is worth being able to say plainly on the Settings page, and it is only true
/// because this asks a question rather than reporting anything.
/// </para>
/// <para>
/// Every failure is silence. Offline, rate-limited by a shared address, behind a proxy that refuses
/// it, or asking about a repository that does not exist yet — all of them mean "no information", and
/// none of them is the user's problem to see. Until Impeller is actually published, the
/// does-not-exist case is the one that runs, which is exactly why it must be quiet rather than
/// merely handled.
/// </para>
/// </remarks>
public sealed class GitHubReleaseFeed : IReleaseFeed, IDisposable
{
    /// <summary>
    /// Short, because nothing waits on this.
    /// </summary>
    /// <remarks>
    /// It runs in the background well after startup, and its answer is a line on a page nobody is
    /// looking at yet. A check that takes longer than this has already failed to be useful.
    /// </remarks>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repository;

    private bool _disposed;

    public GitHubReleaseFeed(string owner, string repository, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        _owner = owner;
        _repository = repository;

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = Timeout;

        // GitHub refuses requests with no User-Agent outright, and the refusal looks exactly like
        // every other failure - which would make this method quietly never work.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Impeller", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <inheritdoc />
    public async Task<ReleaseInfo?> LatestAsync(CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{_owner}/{_repository}/releases/latest";

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);

            return Read(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }

    /// <summary>
    /// Pulls the three fields worth having out of a release.
    /// </summary>
    /// <remarks>
    /// A draft has no business being offered, and a prerelease is filtered later by
    /// <see cref="UpdateCheck"/> — both are checked because <c>/releases/latest</c> is documented to
    /// exclude them and a change of mind upstream should not become an update prompt here.
    /// </remarks>
    private static ReleaseInfo? Read(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (release.TryGetProperty("tag_name", out var tag) && tag.GetString() is { Length: > 0 } version)
        {
            var name = release.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = release.TryGetProperty("html_url", out var u) ? u.GetString() : null;

            return new ReleaseInfo(
                version,
                string.IsNullOrWhiteSpace(name) ? version : name,
                url ?? string.Empty);
        }

        return null;
    }
}
