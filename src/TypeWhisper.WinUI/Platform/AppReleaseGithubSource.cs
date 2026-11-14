using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace TypeWhisper.WinUI.Platform;

/// <summary>
/// Retrieves app updates from GitHub releases that contain one of the exact Velopack channel feeds.
/// </summary>
/// <remarks>
/// The newest releases of each feed are read together, so Velopack chooses the highest version among them. A
/// maintenance release of an older line that is published after a newer line therefore does not hide the newer one.
/// </remarks>
internal sealed class AppReleaseGithubSource : GithubSource
{
    private const int ReleasesPerPage = 100;
    private const int MaximumPages = 10;
    /// <summary>How many of the newest releases of each feed are read.</summary>
    internal const int ReleasesPerFeed = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string[] _releaseIndexNames;
    internal bool? HasMatchingRelease { get; private set; }

    /// <param name="repoUrl">The repository whose releases carry the app packages.</param>
    /// <param name="channel">The installation's own feed.</param>
    /// <param name="prerelease">Whether prereleases are considered.</param>
    /// <param name="downloader">The downloader; tests replace it.</param>
    /// <param name="additionalChannels">Further feeds whose packages this installation also accepts.</param>
    public AppReleaseGithubSource(
        string repoUrl,
        string channel,
        bool prerelease,
        IFileDownloader? downloader = null,
        IReadOnlyList<string>? additionalChannels = null)
        : base(repoUrl, accessToken: null, prerelease, downloader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        foreach (var additional in additionalChannels ?? []) ArgumentException.ThrowIfNullOrWhiteSpace(additional);
        _releaseIndexNames = new[] { channel }.Concat(additionalChannels ?? [])
            .Select(name => $"releases.{name}.json").Distinct(StringComparer.Ordinal).ToArray();
    }

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        HasMatchingRelease = null;
        var matches = new List<GithubRelease>();
        for (var page = 1; page <= MaximumPages; page++)
        {
            var releasesPath = $"repos{RepoUri.AbsolutePath}/releases?per_page={ReleasesPerPage}&page={page}";
            var releasesUri = new Uri(GetApiBaseUrl(RepoUri), releasesPath);
            var response = await Downloader.DownloadString(
                releasesUri.ToString(),
                GetRequestHeaders("application/vnd.github.v3+json"))
                .ConfigureAwait(false);

            var releases = JsonSerializer.Deserialize<GithubRelease[]>(response, JsonOptions) ?? [];
            matches.AddRange(releases
                .Where(release => includePrereleases || !release.Prerelease)
                .Where(release => _releaseIndexNames.Any(name => ContainsAsset(release, name))));

            if (releases.Length < ReleasesPerPage || _releaseIndexNames.All(name => matches.Count(release => ContainsAsset(release, name)) >= ReleasesPerFeed))
                return Select(matches);
        }

        if (matches.Count > 0) return Select(matches);
        // A bounded search cannot prove that an older channel feed is absent.
        // Propagate failure so callers cannot present an empty feed as up to date.
        throw new InvalidOperationException(Loc.T("The release search could not be completed. Please try again later."));
    }

    // The newest releases of each feed, newest first.
    private GithubRelease[] Select(List<GithubRelease> matches)
    {
        HasMatchingRelease = matches.Count > 0;
        return _releaseIndexNames
            .SelectMany(name => matches.Where(release => ContainsAsset(release, name))
                .OrderByDescending(release => release.PublishedAt).Take(ReleasesPerFeed))
            .Distinct()
            .OrderByDescending(release => release.PublishedAt)
            .ToArray();
    }

    /// <summary>Reads every accepted feed of the selected releases; each package stays tied to the release it came from.</summary>
    public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        var releases = await GetReleases(Prerelease).ConfigureAwait(false);
        var entries = new List<VelopackAsset>();
        foreach (var release in releases)
            foreach (var name in _releaseIndexNames.Where(name => ContainsAsset(release, name)))
            {
                var bytes = await Downloader.DownloadBytes(GetAssetUrlFromName(release, name), GetRequestHeaders("application/octet-stream"))
                    .ConfigureAwait(false);
                var feed = VelopackAssetFeed.FromJson(RemoveByteOrderMark(bytes));
                entries.AddRange(feed.Assets.Select(asset => new GitBaseAsset(asset, release)));
            }
        if (releases.Length == 0) logger.Warn($"No releases found at '{RepoUri}'.");
        return new VelopackAssetFeed { Assets = entries.ToArray() };
    }

    private static string RemoveByteOrderMark(byte[] bytes)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private static bool ContainsAsset(GithubRelease release, string name) =>
        release.Assets?.Any(asset => string.Equals(asset.Name, name, StringComparison.Ordinal)) == true;
}
