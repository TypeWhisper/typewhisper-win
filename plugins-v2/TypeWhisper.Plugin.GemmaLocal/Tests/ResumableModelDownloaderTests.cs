using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using TypeWhisper.Plugin.GemmaLocal;

namespace PortableMigration.Tests;

public sealed class ResumableModelDownloaderTests
{
    private static readonly byte[] Payload = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly GemmaModelDefinition Model = new("fixture", "Fixture", "8 bytes", 0, false,
        "https://fixture.invalid/model", "model.gguf", Payload.Length, Convert.ToHexString(SHA256.HashData(Payload)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeUsesValidatedRangeOrRestartsIfServerIgnoresRange(bool ignoresRange)
    {
        using var f = new PortableFixture();
        var path = Path.Combine(f.Root, "model.gguf");
        await File.WriteAllBytesAsync(path + ".download", Payload[..3]);
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("bytes=3-", request.Headers.Range!.ToString());
            return ignoresRange ? Full(Payload) : Range(Payload[3..], 3, 7, 8);
        }));
        await ResumableModelDownloader.DownloadAsync(client, Model, path, null, default);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".download"));
    }

    [Theory]
    [InlineData(2, 7, 8)]
    [InlineData(3, 6, 8)]
    [InlineData(3, 7, 9)]
    public async Task InvalidRangeNeverAppendsToSavedData(long from, long to, long total)
    {
        using var f = new PortableFixture();
        var path = Path.Combine(f.Root, "model.gguf");
        await File.WriteAllBytesAsync(path + ".download", Payload[..3]);
        using var client = new HttpClient(new Handler(_ => Range(Payload[3..], from, to, total)));
        await Assert.ThrowsAsync<IOException>(() => ResumableModelDownloader.DownloadAsync(client, Model, path, null, default));
        Assert.Equal(Payload[..3], await File.ReadAllBytesAsync(path + ".download"));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkFailureOrCancellationRetainsBytesForNextAttempt(bool cancel)
    {
        using var f = new PortableFixture();
        var path = Path.Combine(f.Root, "model.gguf");
        using var cts = new CancellationTokenSource();
        using (var broken = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
            { Content = new StreamContent(new InterruptedStream(cancel ? cts : null)) })))
        {
            var download = () => ResumableModelDownloader.DownloadAsync(broken, Model, path, null, cts.Token);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(download);
            else await Assert.ThrowsAsync<IOException>(download);
        }
        Assert.Equal(Payload[..3], await File.ReadAllBytesAsync(path + ".download"));
        using var retry = new HttpClient(new Handler(request =>
        {
            Assert.Equal("bytes=3-", request.Headers.Range!.ToString());
            return Range(Payload[3..], 3, 7, 8);
        }));
        await ResumableModelDownloader.DownloadAsync(retry, Model, path, null, default);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("missing-range")]
    [InlineData("wrong-length")]
    [InlineData("encoded")]
    public async Task InvalidResponseHeadersPreserveTheExistingPartial(string scenario)
    {
        using var f = new PortableFixture();
        var path = Path.Combine(f.Root, "model.gguf");
        await File.WriteAllBytesAsync(path + ".download", Payload[..3]);
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = Range(Payload[3..], 3, 7, 8);
            if (scenario == "missing-range") response.Content.Headers.ContentRange = null;
            if (scenario == "wrong-length") response.Content.Headers.ContentLength = 4;
            if (scenario == "encoded") response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        }));
        await Assert.ThrowsAsync<IOException>(() => ResumableModelDownloader.DownloadAsync(client, Model, path, null, default));
        Assert.Equal(Payload[..3], await File.ReadAllBytesAsync(path + ".download"));
    }

    [Fact]
    public async Task PreCanceledRetryLeavesSavedBytesUntouched()
    {
        using var f = new PortableFixture();
        var path = Path.Combine(f.Root, "model.gguf");
        await File.WriteAllBytesAsync(path + ".download", Payload[..3]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var client = new HttpClient(new Handler(_ => throw new Exception("No HTTP request expected.")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResumableModelDownloader.DownloadAsync(client, Model, path, null, cancellation.Token));
        Assert.Equal(Payload[..3], await File.ReadAllBytesAsync(path + ".download"));
    }

    [Fact]
    public async Task CompleteSavedFileIsVerifiedAndPublishedWithoutNetwork()
    {
        using var f = new PortableFixture();
        var path = Path.Combine(f.Root, "model.gguf");
        await File.WriteAllBytesAsync(path + ".download", Payload);
        using var client = new HttpClient(new Handler(_ => throw new Exception("No HTTP request expected.")));
        await ResumableModelDownloader.DownloadAsync(client, Model, path, null, default);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task CompleteCorruptSavedFileRestartsFromZero()
    {
        using var f = new PortableFixture();
        var path = Path.Combine(f.Root, "model.gguf");
        await File.WriteAllBytesAsync(path + ".download", new byte[Payload.Length]);
        using var client = new HttpClient(new Handler(request => { Assert.Null(request.Headers.Range); return Full(Payload); }));
        await ResumableModelDownloader.DownloadAsync(client, Model, path, null, default);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidPayloadIsDiscardedAndNeverReplacesPublishedFile(bool overrun)
    {
        using var f = new PortableFixture();
        var path = Path.Combine(f.Root, "model.gguf");
        await File.WriteAllTextAsync(path, "keep previous file");
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = Full(overrun ? [.. Payload, 9] : new byte[Payload.Length]);
            response.Content.Headers.ContentLength = Payload.Length;
            return response;
        }));
        await Assert.ThrowsAsync<IOException>(() => ResumableModelDownloader.DownloadAsync(client, Model, path, null, default));
        Assert.False(File.Exists(path + ".download"));
        Assert.Equal("keep previous file", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task TruncatedResponseIsRetainedButNotPublished()
    {
        using var f = new PortableFixture();
        var path = Path.Combine(f.Root, "model.gguf");
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = Full(Payload[..3]); response.Content.Headers.ContentLength = Payload.Length; return response;
        }));
        await Assert.ThrowsAsync<IOException>(() => ResumableModelDownloader.DownloadAsync(client, Model, path, null, default));
        Assert.Equal(Payload[..3], await File.ReadAllBytesAsync(path + ".download"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DiscardActionRemovesOnlyKnownPartialsAndHonorsCancellation()
    {
        using var f = new PortableFixture();
        using var plugin = new GemmaLocalPlugin([Model]);
        await plugin.ActivateAsync(f.Host);
        var directory = Path.Combine(f.Host.PluginAssetDirectory, "Models", Model.Id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Model.FileName);
        await File.WriteAllBytesAsync(path, Payload);
        await File.WriteAllTextAsync(path + ".download", "partial");
        await File.WriteAllTextAsync(path + ".download.tmp", "old partial");
        await File.WriteAllTextAsync(Path.Combine(directory, "foreign.download"), "keep");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.ExecuteSettingsActionAsync("discard-partial-downloads", canceled.Token));
        Assert.True(File.Exists(path + ".download"));
        await plugin.ExecuteSettingsActionAsync("discard-partial-downloads", default);
        Assert.False(File.Exists(path + ".download"));
        Assert.False(File.Exists(path + ".download.tmp"));
        Assert.True(File.Exists(Path.Combine(directory, "foreign.download")));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
    }

    private static HttpResponseMessage Full(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Range(byte[] bytes, long from, long to, long total)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }
    private sealed class InterruptedStream(CancellationTokenSource? cancellation) : Stream
    {
        private bool _sent;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent)
            {
                cancellation?.Cancel(); ct.ThrowIfCancellationRequested();
                throw new IOException("Connection interrupted.");
            }
            _sent = true; Payload.AsMemory(0, 3).CopyTo(buffer); return ValueTask.FromResult(3);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
