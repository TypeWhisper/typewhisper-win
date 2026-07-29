using System.IO;
using System.Text;
using System.Text.Json;
using Velopack.Logging;
using Velopack.Sources;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class AppReleaseGithubSourceTests
{
    private const string RepositoryUrl = "https://github.com/TypeWhisper/typewhisper-win";
    private const string PackageFileName = "TypeWhisper-1.0.6-win-x64-full.nupkg";
    private const string FeedJson =
        """
        {
          "Assets": [
            {
              "PackageId": "TypeWhisper",
              "Version": "1.0.6",
              "Type": "Full",
              "FileName": "TypeWhisper-1.0.6-win-x64-full.nupkg",
              "SHA1": "7806F55FB92B86005F703127C5CC7EF8ED0EF044",
              "SHA256": "F7E3758F1D04D0E0AEBED90E29FD77FF71CD833F8CCC7871CAE90D6C1633365C",
              "Size": 20317312
            }
          ]
        }
        """;

    [Fact]
    public async Task GetReleaseFeed_IgnoresMoreThanTenNewerPluginReleases()
    {
        var releases = Enumerable.Range(1, 12)
            .Select(index => CreateRelease(
                $"Plugin {index}",
                publishedAt: new DateTime(2026, 7, 29, 12, index, 0, DateTimeKind.Utc),
                prerelease: false,
                [$"com.typewhisper.plugin-{index}.zip"]))
            .Append(CreateRelease(
                "v1.0.6",
                publishedAt: new DateTime(2026, 7, 28, 20, 47, 0, DateTimeKind.Utc),
                prerelease: false,
                ["releases.win-x64.json", PackageFileName]))
            .ToArray();
        var downloader = new FakeDownloader(url =>
            url.Contains("/releases?", StringComparison.Ordinal)
                ? SerializeReleases(releases)
                : throw new InvalidOperationException($"Unexpected string request: {url}"))
        {
            BytesResponse = Encoding.UTF8.GetBytes(FeedJson)
        };
        var source = new AppReleaseGithubSource(
            RepositoryUrl,
            "win-x64",
            prerelease: false,
            downloader);

        var feed = await source.GetReleaseFeed(new TestLogger(), "TypeWhisper", "win-x64");

        var asset = Assert.Single(feed.Assets);
        Assert.Equal(PackageFileName, asset.FileName);
        Assert.Contains(
            downloader.StringRequests,
            url => url.Contains("per_page=100&page=1", StringComparison.Ordinal));
        Assert.DoesNotContain(
            downloader.BytesRequests,
            url => url.Contains("plugin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetReleaseFeed_PaginatesUntilItFindsTheExactChannelFeed()
    {
        var firstPage = Enumerable.Range(1, 100)
            .Select(index => CreateRelease(
                $"Plugin {index}",
                publishedAt: new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-index),
                prerelease: false,
                [$"plugin-{index}.zip"]))
            .ToArray();
        var secondPage = new[]
        {
            CreateRelease(
                "v1.0.7-daily.20260729",
                publishedAt: new DateTime(2026, 7, 29, 6, 31, 0, DateTimeKind.Utc),
                prerelease: true,
                ["releases.win-x64-daily.json", PackageFileName])
        };
        var downloader = new FakeDownloader(url =>
        {
            if (url.Contains("&page=1", StringComparison.Ordinal))
                return SerializeReleases(firstPage);
            if (url.Contains("&page=2", StringComparison.Ordinal))
                return SerializeReleases(secondPage);

            throw new InvalidOperationException($"Unexpected string request: {url}");
        })
        {
            BytesResponse = Encoding.UTF8.GetBytes(FeedJson)
        };
        var source = new AppReleaseGithubSource(
            RepositoryUrl,
            "win-x64-daily",
            prerelease: true,
            downloader);

        var feed = await source.GetReleaseFeed(new TestLogger(), "TypeWhisper", "win-x64-daily");

        Assert.Single(feed.Assets);
        Assert.Equal(2, downloader.StringRequests.Count);
        Assert.Contains(
            downloader.StringRequests,
            url => url.Contains("per_page=100&page=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetReleaseFeed_StableChannelSkipsPrereleaseWithMatchingFeed()
    {
        var releases = new[]
        {
            CreateRelease(
                "v1.0.7-preview",
                publishedAt: new DateTime(2026, 7, 29, 6, 31, 0, DateTimeKind.Utc),
                prerelease: true,
                ["releases.win-x64.json", PackageFileName]),
            CreateRelease(
                "v1.0.6",
                publishedAt: new DateTime(2026, 7, 28, 20, 47, 0, DateTimeKind.Utc),
                prerelease: false,
                ["releases.win-x64.json", PackageFileName])
        };
        var stableFeedUrl = "https://example.test/v1.0.6/releases.win-x64.json";
        var downloader = new FakeDownloader(_ => SerializeReleases(releases))
        {
            BytesResponder = url =>
            {
                Assert.Equal(stableFeedUrl, url);
                return Encoding.UTF8.GetBytes(FeedJson);
            }
        };
        releases[1].Assets.Single(asset => asset.Name == "releases.win-x64.json").BrowserDownloadUrl = stableFeedUrl;
        var source = new AppReleaseGithubSource(
            RepositoryUrl,
            "win-x64",
            prerelease: false,
            downloader);

        var feed = await source.GetReleaseFeed(new TestLogger(), "TypeWhisper", "win-x64");

        Assert.Single(feed.Assets);
        Assert.Equal([stableFeedUrl], downloader.BytesRequests);
    }

    [Fact]
    public async Task GetReleaseFeed_ReturnsEmptyWhenNoReleaseContainsTheChannelFeed()
    {
        var releases = new[]
        {
            CreateRelease(
                "TypeWhisper.Plugin.SherpaOnnx v1.0.5",
                publishedAt: new DateTime(2026, 7, 29, 12, 24, 0, DateTimeKind.Utc),
                prerelease: false,
                [
                    "com.typewhisper.sherpa-onnx-1.0.5.zip",
                    "RELEASES.WIN-X64.JSON",
                    "releases.win-x64-daily.json"
                ])
        };
        var downloader = new FakeDownloader(_ => SerializeReleases(releases));
        var source = new AppReleaseGithubSource(
            RepositoryUrl,
            "win-x64",
            prerelease: false,
            downloader);

        var feed = await source.GetReleaseFeed(new TestLogger(), "TypeWhisper", "win-x64");

        Assert.Empty(feed.Assets);
        Assert.Empty(downloader.BytesRequests);
    }

    [Fact]
    public async Task DownloadReleaseEntry_UsesPackageFromTheSelectedAppRelease()
    {
        var packageUrl = $"https://example.test/v1.0.6/{PackageFileName}";
        var releases = new[]
        {
            CreateRelease(
                "v1.0.6",
                publishedAt: new DateTime(2026, 7, 28, 20, 47, 0, DateTimeKind.Utc),
                prerelease: false,
                ["releases.win-x64.json", PackageFileName])
        };
        releases[0].Assets.Single(asset => asset.Name == PackageFileName).BrowserDownloadUrl = packageUrl;
        var downloader = new FakeDownloader(_ => SerializeReleases(releases))
        {
            BytesResponse = Encoding.UTF8.GetBytes(FeedJson)
        };
        var source = new AppReleaseGithubSource(
            RepositoryUrl,
            "win-x64",
            prerelease: false,
            downloader);
        var logger = new TestLogger();
        var feed = await source.GetReleaseFeed(logger, "TypeWhisper", "win-x64");
        var targetFile = Path.Join(Path.GetTempPath(), $"tw_update_source_{Guid.NewGuid():N}.nupkg");

        try
        {
            await source.DownloadReleaseEntry(
                logger,
                Assert.Single(feed.Assets),
                targetFile,
                _ => { },
                CancellationToken.None);

            Assert.Equal([packageUrl], downloader.FileRequests);
            Assert.True(File.Exists(targetFile));
        }
        finally
        {
            if (File.Exists(targetFile))
                File.Delete(targetFile);
        }
    }

    private static GithubRelease CreateRelease(
        string name,
        DateTime publishedAt,
        bool prerelease,
        IReadOnlyList<string> assetNames) =>
        new()
        {
            Name = name,
            PublishedAt = publishedAt,
            Prerelease = prerelease,
            Assets = assetNames
                .Select(assetName => new GithubReleaseAsset
                {
                    Name = assetName,
                    BrowserDownloadUrl = $"https://example.test/{Uri.EscapeDataString(name)}/{assetName}",
                    Url = $"https://api.example.test/{Uri.EscapeDataString(name)}/{assetName}"
                })
                .ToArray()
        };

    private static string SerializeReleases(IEnumerable<GithubRelease> releases) =>
        JsonSerializer.Serialize(releases);

    private sealed class FakeDownloader(Func<string, string> stringResponder) : IFileDownloader
    {
        public List<string> StringRequests { get; } = [];
        public List<string> BytesRequests { get; } = [];
        public List<string> FileRequests { get; } = [];
        public byte[] BytesResponse { get; init; } = [];
        public Func<string, byte[]>? BytesResponder { get; init; }

        public Task<string> DownloadString(
            string url,
            IDictionary<string, string>? headers = null,
            double timeout = 30)
        {
            StringRequests.Add(url);
            return Task.FromResult(stringResponder(url));
        }

        public Task<byte[]> DownloadBytes(
            string url,
            IDictionary<string, string>? headers = null,
            double timeout = 30)
        {
            BytesRequests.Add(url);
            return Task.FromResult(BytesResponder?.Invoke(url) ?? BytesResponse);
        }

        public async Task DownloadFile(
            string url,
            string targetFile,
            Action<int> progress,
            IDictionary<string, string>? headers = null,
            double timeout = 30,
            CancellationToken cancelToken = default)
        {
            FileRequests.Add(url);
            await File.WriteAllBytesAsync(targetFile, [1, 2, 3], cancelToken);
            progress(100);
        }
    }

    private sealed class TestLogger : IVelopackLogger
    {
        public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
        {
        }
    }
}
