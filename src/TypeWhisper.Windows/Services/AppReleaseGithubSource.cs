using System.Text.Json;
using Velopack.Sources;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Retrieves app updates from GitHub releases that contain the exact Velopack channel feed.
/// </summary>
internal sealed class AppReleaseGithubSource : GithubSource
{
    private const int ReleasesPerPage = 100;
    private const int MaximumPages = 10;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _releaseIndexName;
    internal bool? HasMatchingRelease { get; private set; }

    public AppReleaseGithubSource(
        string repoUrl,
        string channel,
        bool prerelease,
        IFileDownloader? downloader = null)
        : base(repoUrl, accessToken: null, prerelease, downloader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        _releaseIndexName = $"releases.{channel}.json";
    }

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        HasMatchingRelease = null;
        for (var page = 1; page <= MaximumPages; page++)
        {
            var releasesPath = $"repos{RepoUri.AbsolutePath}/releases?per_page={ReleasesPerPage}&page={page}";
            var releasesUri = new Uri(GetApiBaseUrl(RepoUri), releasesPath);
            var response = await Downloader.DownloadString(
                releasesUri.ToString(),
                GetRequestHeaders("application/vnd.github.v3+json"))
                .ConfigureAwait(false);

            var releases = JsonSerializer.Deserialize<GithubRelease[]>(response, JsonOptions) ?? [];
            var matchingRelease = releases
                .Where(release => includePrereleases || !release.Prerelease)
                .Where(ContainsReleaseIndex)
                .OrderByDescending(release => release.PublishedAt)
                .FirstOrDefault();

            if (matchingRelease is not null)
            {
                HasMatchingRelease = true;
                return [matchingRelease];
            }

            if (releases.Length < ReleasesPerPage)
            {
                HasMatchingRelease = false;
                return [];
            }
        }

        // A bounded search cannot prove that an older channel feed is absent.
        // Propagate failure so callers cannot present an empty feed as up to date.
        throw new InvalidOperationException("The release search could not be completed. Please try again later.");
    }

    private bool ContainsReleaseIndex(GithubRelease release) =>
        release.Assets?.Any(asset =>
            string.Equals(asset.Name, _releaseIndexName, StringComparison.Ordinal)) == true;
}
