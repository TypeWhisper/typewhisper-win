using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginHost;
using Xunit;

namespace TypeWhisper.PluginSDK.Portable.Tests;

public sealed class PluginCatalogDownloadUrlTests
{
    private const string Release = "https://github.com/TypeWhisper/typewhisper-win/releases/download/";

    [Theory]
    [InlineData(Release + "plugin-assemblyai-v1.2.0/com.typewhisper.assemblyai-1.2.0-win-x64.zip")]
    [InlineData(Release + "plugin-sherpa-onnx-v1.3.4/com.typewhisper.sherpa-onnx-1.3.4-win-arm64.zip")]
    public void ReleaseAssetsOfThisRepositoryAreAccepted(string url)
    {
        Assert.True(PortableCatalogEntry.IsReleaseDownload(url));
        Entry(url).Validate();
    }

    [Theory]
    [InlineData("https://example.com/plugin.zip")]
    [InlineData("http://github.com/TypeWhisper/typewhisper-win/releases/download/tag/plugin.zip")]
    [InlineData("https://github.com/Other/typewhisper-win/releases/download/tag/plugin.zip")]
    [InlineData("https://github.com/TypeWhisper/other/releases/download/tag/plugin.zip")]
    [InlineData("https://github.com/typewhisper/typewhisper-win/releases/download/tag/plugin.zip")]
    [InlineData("https://github.com.example.com/TypeWhisper/typewhisper-win/releases/download/tag/plugin.zip")]
    [InlineData("https://github.com:8443/TypeWhisper/typewhisper-win/releases/download/tag/plugin.zip")]
    [InlineData("https://user@github.com/TypeWhisper/typewhisper-win/releases/download/tag/plugin.zip")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/plugin.zip")]
    [InlineData(Release + "plugin.zip")]
    [InlineData(Release + "tag/sub/plugin.zip")]
    [InlineData(Release + "tag/../../../../Other/repo/releases/download/tag/plugin.zip")]
    [InlineData(Release + "tag/%2e%2e/plugin.zip")]
    [InlineData(Release + "tag/plugin.zip?redirect=https://example.com")]
    [InlineData(Release + "tag/plugin.zip#fragment")]
    [InlineData(Release + "tag/.hidden.zip")]
    [InlineData(Release + "tag/")]
    [InlineData("")]
    public void DownloadsOutsideTheReleasesOfThisRepositoryAreRejected(string url)
    {
        Assert.False(PortableCatalogEntry.IsReleaseDownload(url));
        Assert.Throws<InvalidDataException>(Entry(url).Validate);
    }

    [Fact]
    public async Task CatalogWithAForeignDownloadIsRejectedAsAWhole()
    {
        var feed = JsonSerializer.Serialize(new { plugins = new[] { Entry(Release + "tag/good.zip"), Entry("https://example.com/plugin.zip") with { Id = "com.example.other" } } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var http = new HttpClient(new Handler(Encoding.UTF8.GetBytes(feed)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PortablePluginCatalog(http).FetchAsync());
    }

    [Fact]
    public async Task InstallRefusesAForeignDownloadBeforeAnyRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "plugin-url-" + Guid.NewGuid().ToString("N"));
        var handler = new Handler([1, 2, 3]);
        using var http = new HttpClient(handler);
        try
        {
            var store = new PortablePluginStore(root, new(1, 1, 0), http);
            await store.InitializeAsync();
            await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(Entry("https://example.com/plugin.zip")));
            Assert.Equal(0, handler.Requests);
            Assert.False(store.IsInstalled("com.example.provider"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static PortableCatalogEntry Entry(string url) => new()
    {
        Id = "com.example.provider", Name = "Provider", Version = "1.1.0", DownloadUrl = url,
        Sha256 = new string('a', 64), Size = 3, SupportedArchitectures = [PortablePluginCatalog.Architecture]
    };

    private sealed class Handler(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(body) });
        }
    }
}
