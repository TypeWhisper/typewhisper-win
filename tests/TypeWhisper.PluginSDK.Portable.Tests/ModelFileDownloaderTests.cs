using System.Net;
using TypeWhisper.PluginSDK.Helpers;
using Xunit;

public sealed class ModelFileDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "download-" + Guid.NewGuid());
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response());
    }
    private string Destination { get { Directory.CreateDirectory(_root); return Path.Combine(_root, "model.onnx"); } }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 5, false)]
    [InlineData(0, 0, false)]
    public async Task PublishesOnlyCompleteNonemptyResponse(int bytes, long declared, bool succeeds)
    {
        using var client = new HttpClient(new Handler(() => {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes]) };
            response.Content.Headers.ContentLength = declared; return response;
        }));
        var path = Destination;
        if (succeeds) { await ModelFileDownloader.DownloadAsync(client, "https://fixture.invalid/model", path, null, default); Assert.Equal(bytes, new FileInfo(path).Length); }
        else { await Assert.ThrowsAsync<IOException>(() => ModelFileDownloader.DownloadAsync(client, "https://fixture.invalid/model", path, null, default)); Assert.False(File.Exists(path)); }
        Assert.False(File.Exists(path + ".tmp"));
    }
    [Fact]
    public async Task HttpFailurePreservesExistingFileAndCleansPartial()
    {
        var path = Destination; await File.WriteAllTextAsync(path, "existing"); await File.WriteAllTextAsync(path + ".tmp", "partial");
        using var client = new HttpClient(new Handler(() => new(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<HttpRequestException>(() => ModelFileDownloader.DownloadAsync(client, "https://fixture.invalid/model", path, null, default));
        Assert.Equal("existing", await File.ReadAllTextAsync(path)); Assert.False(File.Exists(path + ".tmp"));
    }
    [Fact]
    public async Task CancellationDoesNotPublishOrLeaveTemporaryFile()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var path = Destination; await File.WriteAllTextAsync(path + ".tmp", "partial");
        using var client = new HttpClient(new Handler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelFileDownloader.DownloadAsync(client, "https://fixture.invalid/model", path, null, cancellation.Token));
        Assert.False(File.Exists(path)); Assert.False(File.Exists(path + ".tmp"));
    }
}
