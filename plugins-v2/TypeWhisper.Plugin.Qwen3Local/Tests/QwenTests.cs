using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using TypeWhisper.Plugin.Qwen3Local;

public sealed class QwenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qwen-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task VerifiedDownloadPublishesOnlyCompleteAssetsAndRepairsTruncation()
    {
        var bytes = Archive();
        var requests = 0;
        using var http = Http(() => { requests++; return new ByteArrayContent(bytes); });
        var assets = new QwenModelAssets(http, Source(bytes));
        Assert.False(assets.IsReady(_root));
        await assets.DownloadAsync(_root, null, default);
        Assert.True(assets.IsReady(_root));
        await assets.DownloadAsync(_root, null, default);
        Assert.Equal(1, requests);
        File.WriteAllText(Path.Combine(_root, "encoder.int8.onnx"), "");
        Assert.False(assets.IsReady(_root));
        await assets.DownloadAsync(_root, null, default);
        Assert.True(assets.IsReady(_root));
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("missing")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("truncated")]
    public async Task InvalidDownloadsNeverBecomeReady(string failure)
    {
        var bytes = Archive(failure);
        var source = Source(bytes);
        if (failure == "checksum") source = source with { Sha256 = new string('0', 64) };
        using var http = Http(() => new ByteArrayContent(failure == "truncated" ? bytes[..^1] : bytes));
        var assets = new QwenModelAssets(http, source);
        await Assert.ThrowsAsync<InvalidDataException>(() => assets.DownloadAsync(_root, null, default));
        Assert.False(assets.IsReady(_root));
        Assert.False(Directory.Exists(_root));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + ".download-*"));
    }

    [Fact]
    public async Task CancelledDownloadCleansStagingAndCanRetry()
    {
        var bytes = Archive();
        using var http = Http(() => new ByteArrayContent(bytes));
        var assets = new QwenModelAssets(http, Source(bytes));
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => assets.DownloadAsync(_root, new CallbackProgress(_ => cts.Cancel()), cts.Token));
        Assert.False(assets.IsReady(_root));
        await assets.DownloadAsync(_root, null, default);
        Assert.True(assets.IsReady(_root));
    }

    [Fact]
    public async Task LongAudioIsFullyCoveredAndCancellationDoesNotReturnPartialSuccess()
    {
        var bytes = Archive();
        var decoder = new FakeRecognizer();
        using var plugin = new Qwen3LocalPlugin(Http(() => new ByteArrayContent(bytes)), _ => decoder, Source(bytes));
        await plugin.ActivateAsync(new TestHost(_root));
        await plugin.DownloadModelAsync(Qwen3LocalPlugin.ModelId, null, default);
        Assert.Null(plugin.SelectedModelId);
        plugin.SelectModel(Qwen3LocalPlugin.ModelId);
        var samples = Enumerable.Repeat(.1f, QwenAudio.SampleRate * 40).ToArray();
        var result = await plugin.TranscribePcmAsync(samples, "de-DE", false, default);
        Assert.Equal(samples.Length, decoder.Lengths.Sum());
        Assert.All(decoder.Lengths, length => Assert.InRange(length, 1, QwenAudio.ChunkSamples));
        Assert.Equal(40, result.DurationSeconds);
        Assert.True(result.Segments.Count >= 4);
        Assert.Equal("German", decoder.Language);
        Assert.Null(result.DetectedLanguage);
        using var cts = new CancellationTokenSource();
        decoder.AfterDecode = cts.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.TranscribePcmAsync(samples, null, false, cts.Token));
        decoder.AfterDecode = null;
        await plugin.UnloadModelAsync(); Assert.True(decoder.Disposed);
        await plugin.LoadModelAsync(Qwen3LocalPlugin.ModelId, default);
        await plugin.RemoveModelAsync(Qwen3LocalPlugin.ModelId, default);
        Assert.False(plugin.IsModelDownloaded(Qwen3LocalPlugin.ModelId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync(Qwen3LocalPlugin.ModelId, default));
    }

    [Fact]
    public async Task UnloadWaitsForNativeDecodeToFinish()
    {
        var bytes = Archive();
        using var started = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var decoder = new FakeRecognizer { AfterDecode = () => { started.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); } };
        using var plugin = new Qwen3LocalPlugin(Http(() => new ByteArrayContent(bytes)), _ => decoder, Source(bytes));
        await plugin.ActivateAsync(new TestHost(_root)); await plugin.DownloadModelAsync(Qwen3LocalPlugin.ModelId, null, default);
        var decode = plugin.TranscribePcmAsync(new[] { .5f }, null, false, default);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            var unload = plugin.UnloadModelAsync();
            Assert.False(unload.IsCompleted); Assert.False(decoder.Disposed);
            finish.Set(); await decode; await unload; Assert.True(decoder.Disposed);
        }
        finally { finish.Set(); }
    }

    [Fact]
    public async Task RejectsUnsupportedRequestsAndInvalidPcm()
    {
        using var plugin = new Qwen3LocalPlugin(); await plugin.ActivateAsync(new TestHost(_root));
        Assert.Throws<ArgumentException>(() => plugin.SelectModel("../other"));
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.TranscribePcmAsync(new[] { .5f }, null, true, default));
        foreach (var sample in new[] { float.NaN, float.PositiveInfinity, 1.1f })
            await Assert.ThrowsAsync<ArgumentException>(() => plugin.TranscribePcmAsync(new[] { sample }, null, false, default));
        var result = await plugin.TranscribePcmAsync(Array.Empty<float>(), null, false, default);
        Assert.Empty(result.Text);
        Assert.Throws<ArgumentException>(() => Qwen3LocalPlugin.NormalizeLanguage("xx"));
    }

    [Fact]
    public void WavParsingHandlesMetadataAndRejectsWrongFormatsAndTruncation()
    {
        var bytes = Wav();
        Assert.Equal(new[] { -.5f, .5f }, QwenAudio.DecodeWav(bytes));
        Assert.Throws<ArgumentException>(() => QwenAudio.DecodeWav(bytes[..^1]));
        bytes[22] = 2; Assert.Throws<ArgumentException>(() => QwenAudio.DecodeWav(bytes));
        bytes = Wav(); bytes[20] = 3; Assert.Throws<ArgumentException>(() => QwenAudio.DecodeWav(bytes));
        bytes = Wav(); bytes[24] = 0; Assert.Throws<ArgumentException>(() => QwenAudio.DecodeWav(bytes));
    }

    internal static byte[] Wav()
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write("RIFF"u8); writer.Write(0); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("JUNK"u8); writer.Write(1); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write("data"u8); writer.Write(4); writer.Write((short)-16384); writer.Write((short)16384);
        memory.Position = 4; writer.Write((int)memory.Length - 8); return memory.ToArray();
    }
    private static byte[] Archive(string? failure = null)
    {
        using var rawTar = new MemoryStream();
        using var memory = new MemoryStream();
        using (var tar = new TarWriter(rawTar, leaveOpen: true))
        {
            foreach (var file in QwenModelAssets.RequiredFiles)
            {
                if (failure == "missing" && file == "encoder.int8.onnx") continue;
                using var data = new MemoryStream("test-model"u8.ToArray());
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "model/" + file) { DataStream = data });
            }
            if (failure is "traversal" or "duplicate")
            {
                using var data = new MemoryStream("invalid"u8.ToArray());
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, failure == "traversal" ? "model/../bad" : "model/encoder.int8.onnx") { DataStream = data });
            }
        }
        rawTar.Position = 0;
        using (var zip = BZip2Stream.Create(memory, CompressionMode.Compress, false, leaveOpen: true))
        { rawTar.CopyTo(zip); zip.Finish(); }
        return memory.ToArray();
    }
    private static QwenAssetSource Source(byte[] bytes) => new("https://fixture.invalid/model", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
    internal static HttpClient Http(Func<HttpContent> content) => new(new Handler(content));
    private sealed class Handler(Func<HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content() }); }
    }
    private sealed class CallbackProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
    private sealed class FakeRecognizer : IQwenRecognizer
    {
        public List<int> Lengths { get; } = [];
        public string? Language; public bool Disposed; public Action? AfterDecode;
        public string Decode(float[] samples, string? language)
        { Lengths.Add(samples.Length); Language = language; AfterDecode?.Invoke(); return "Test transcript."; }
        public void Dispose() => Disposed = true;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
