using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using TypeWhisper.Plugin.Qwen3Local;
using TypeWhisper.PluginSDK;

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
    public async Task InsufficientDiskSpaceStopsBeforeDownloadAfterRemovingAbandonedStaging()
    {
        var bytes = Archive();
        var requests = 0;
        using var http = Http(() => { requests++; return new ByteArrayContent(bytes); });
        var source = Source(bytes) with { ExtractedSize = 1_000_000 };
        var parent = Path.GetDirectoryName(_root)!;
        var abandoned = _root + ".download-" + Guid.NewGuid().ToString("N");
        var active = _root + ".download-" + Guid.NewGuid().ToString("N");
        var unrelated = _root + ".download-backup";
        foreach (var directory in new[] { abandoned, active, unrelated }) Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Join(abandoned, "model.tar.bz2"), new byte[16]);
        File.WriteAllBytes(Path.Join(active, "model.tar.bz2"), new byte[16]);
        try
        {
            string? probed = null;
            var assets = new QwenModelAssets(http, source, directory => { probed = directory; return 1_000_000; });
            TypeWhisper.PluginSDK.Helpers.InsufficientModelStorageException error;
            using (new FileStream(Path.Join(active, "model.tar.bz2"), FileMode.Open, FileAccess.Write, FileShare.None))
                error = await Assert.ThrowsAsync<TypeWhisper.PluginSDK.Helpers.InsufficientModelStorageException>(
                    () => assets.DownloadAsync(_root, null, default));

            Assert.Equal(parent, probed);
            Assert.Equal(0, requests);
            Assert.Equal(bytes.Length + 1_000_000 + TypeWhisper.PluginSDK.Helpers.ModelStorageSpace.ReserveBytes, error.RequiredBytes);
            Assert.StartsWith("Not enough free disk space to download Qwen3-ASR 0.6B.", error.Message);
            Assert.False(Directory.Exists(abandoned));
            Assert.True(Directory.Exists(active));
            Assert.True(Directory.Exists(unrelated));
            Assert.False(assets.IsReady(_root));
        }
        finally
        {
            foreach (var directory in new[] { abandoned, active, unrelated }.Where(Directory.Exists))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PinnedFileSourceInstallsOnlyVerifiedFilesAndRetriesAfterFailure()
    {
        var files = QwenModelAssets.RequiredFiles.ToDictionary(name => name, name => System.Text.Encoding.UTF8.GetBytes("model:" + name));
        var source = FileSource(files);
        var requested = new List<string>();
        var corrupt = "decoder.int8.onnx";
        using var http = Http(request =>
        {
            var name = request.RequestUri!.AbsolutePath["/pinned/".Length..];
            requested.Add(name);
            var bytes = files[name].ToArray();
            if (name == corrupt) bytes[0] ^= 1;
            return new ByteArrayContent(bytes);
        });
        var assets = new QwenModelAssets(http, source);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => assets.DownloadAsync(_root, null, default));
        Assert.Contains("checksum", error.Message);
        Assert.False(assets.IsReady(_root));
        Assert.False(Directory.Exists(_root));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + ".download-*"));

        corrupt = "";
        requested.Clear();
        var reports = new List<double>();
        await assets.DownloadAsync(_root, new CallbackProgress(reports.Add), default);
        Assert.True(assets.IsReady(_root));
        Assert.Equal(QwenModelAssets.RequiredFiles, requested);
        Assert.Equal(1, reports[^1]);
        Assert.Equal(reports.Order(), reports);
        foreach (var (name, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(Path.Join(_root, name)));

        // Changing any pinned hash changes the identity, so the files are downloaded again.
        var repinned = source with { Files = source.Files!.Select((file, index) => index == 0 ? file with { Sha256 = new string('0', 64) } : file).ToArray() };
        Assert.False(new QwenModelAssets(http, QwenAssetSource.FromFiles(repinned.Name, repinned.Url, [.. repinned.Files!])).IsReady(_root));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    public async Task PinnedFileSourceRejectsUnexpectedSizes(string failure)
    {
        var files = QwenModelAssets.RequiredFiles.ToDictionary(name => name, name => System.Text.Encoding.UTF8.GetBytes("model:" + name));
        using var http = Http(request =>
        {
            var bytes = files[request.RequestUri!.AbsolutePath["/pinned/".Length..]];
            // Without a Content-Length header the copy itself must enforce the pinned size.
            var stream = new MemoryStream(failure == "short" ? bytes[..^1] : [.. bytes, 0]);
            return new StreamContent(new NonSeekableStream(stream));
        });
        var assets = new QwenModelAssets(http, FileSource(files));
        await Assert.ThrowsAsync<InvalidDataException>(() => assets.DownloadAsync(_root, null, default));
        Assert.False(assets.IsReady(_root));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + ".download-*"));
    }

    [Fact]
    public async Task AbandonedFileSourceStagingIsRemovedButAnActiveDownloadIsKept()
    {
        var abandoned = Path.Join(_root + ".download-" + Guid.NewGuid().ToString("N"), "model", "tokenizer");
        var active = Path.Join(_root + ".download-" + Guid.NewGuid().ToString("N"), "model", "tokenizer");
        Directory.CreateDirectory(abandoned); Directory.CreateDirectory(active);
        File.WriteAllBytes(Path.Join(abandoned, "vocab.json"), new byte[16]);
        File.WriteAllBytes(Path.Join(active, "..", "encoder.int8.onnx"), new byte[16]);
        try
        {
            using (new FileStream(Path.Join(active, "vocab.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                QwenModelAssets.RemoveAbandonedStaging(_root);
            Assert.False(Directory.Exists(Path.Join(abandoned, "..", "..")));
            Assert.True(File.Exists(Path.Join(active, "..", "encoder.int8.onnx")));
        }
        finally
        {
            var staging = Path.GetFullPath(Path.Join(active, "..", ".."));
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    [Fact]
    public void LargeModelPinsRevisionAndEveryRequiredFile()
    {
        var source = QwenModelAssets.Model17B;
        Assert.Matches("/resolve/[0-9a-f]{40}$", source.Url);
        Assert.Equal(QwenModelAssets.RequiredFiles, source.Files!.Select(file => file.Name));
        Assert.All(source.Files!, file => { Assert.Matches("^[0-9a-f]{64}$", file.Sha256); Assert.True(file.Size > 0); });
        Assert.Equal(source.Files!.Sum(file => file.Size), source.Size);
        Assert.Null(QwenModelAssets.Model06B.Files);
    }

    [Fact]
    public async Task ModelsAreDownloadedSelectedLoadedAndRemovedIndependently()
    {
        var bytes = Archive();
        var host = new TestHost(_root);
        var loaded = new List<(string Directory, FakeRecognizer Recognizer)>();
        using var plugin = new Qwen3LocalPlugin(Http(() => new ByteArrayContent(bytes)), directory =>
        { var recognizer = new FakeRecognizer(); loaded.Add((directory, recognizer)); return recognizer; }, Source(bytes));
        await plugin.ActivateAsync(host);
        Assert.Equal([Qwen3LocalPlugin.ModelId, Qwen3LocalPlugin.LargeModelId], plugin.TranscriptionModels.Select(model => model.Id));
        Assert.Single(plugin.TranscriptionModels, model => model.IsRecommended);

        await plugin.DownloadModelAsync(Qwen3LocalPlugin.LargeModelId, null, default);
        Assert.True(plugin.IsModelDownloaded(Qwen3LocalPlugin.LargeModelId));
        Assert.False(plugin.IsModelDownloaded(Qwen3LocalPlugin.ModelId));
        plugin.SelectModel(Qwen3LocalPlugin.LargeModelId);
        Assert.True(plugin.IsConfigured);
        await plugin.TranscribePcmAsync(new[] { .5f }, null, false, default);
        Assert.EndsWith(Qwen3LocalPlugin.LargeModelId, Assert.Single(loaded).Directory);

        // Switching models keeps only the newly selected one resident.
        await plugin.DownloadModelAsync(Qwen3LocalPlugin.ModelId, null, default);
        plugin.SelectModel(Qwen3LocalPlugin.ModelId);
        await plugin.TranscribePcmAsync(new[] { .5f }, null, false, default);
        Assert.Equal(2, loaded.Count);
        Assert.True(loaded[0].Recognizer.Disposed);
        Assert.EndsWith(Qwen3LocalPlugin.ModelId, loaded[1].Directory);

        // The host keeps blocking the earlier model's removal, so its own action removes it while the selection
        // and the loaded model stay.
        Assert.Equal(["remove-" + Qwen3LocalPlugin.ModelId, "remove-" + Qwen3LocalPlugin.LargeModelId], plugin.SettingsActions.Select(action => action.Id));
        await plugin.ExecuteSettingsActionAsync("remove-" + Qwen3LocalPlugin.LargeModelId, default);
        Assert.False(plugin.IsModelDownloaded(Qwen3LocalPlugin.LargeModelId));
        Assert.Equal(Qwen3LocalPlugin.ModelId, plugin.SelectedModelId);
        Assert.False(loaded[1].Recognizer.Disposed);
        Assert.True(plugin.IsConfigured);

        await plugin.ExecuteSettingsActionAsync("remove-" + Qwen3LocalPlugin.ModelId, default);
        Assert.True(loaded[1].Recognizer.Disposed);
        Assert.Null(plugin.SelectedModelId);
        Assert.False(plugin.IsModelDownloaded(Qwen3LocalPlugin.ModelId));
        Assert.Equal("Qwen3-ASR 0.6B INT8 is not downloaded.", await plugin.ExecuteSettingsActionAsync("remove-" + Qwen3LocalPlugin.ModelId, default));

        // A model directory that fails the readiness check is still removable through its action.
        var incomplete = Path.Join(((TypeWhisper.PluginSDK.IPluginHostServices)host).PluginAssetDirectory, "Models", Qwen3LocalPlugin.LargeModelId);
        Directory.CreateDirectory(incomplete);
        await File.WriteAllTextAsync(Path.Join(incomplete, "encoder.int8.onnx"), "partial");
        Assert.False(plugin.IsModelDownloaded(Qwen3LocalPlugin.LargeModelId));
        await plugin.ExecuteSettingsActionAsync("remove-" + Qwen3LocalPlugin.LargeModelId, default);
        Assert.False(Directory.Exists(incomplete));

        host.SetSetting("selectedModel", Qwen3LocalPlugin.LargeModelId);
        await plugin.DeactivateAsync();
        await plugin.ActivateAsync(host);
        Assert.Equal(Qwen3LocalPlugin.LargeModelId, plugin.SelectedModelId);
        Assert.False(plugin.IsConfigured);
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
    public async Task ReadinessRequiresSelectedIntactAssetsAndSettingsActionRemovesSelectedModel()
    {
        var bytes = Archive();
        var host = new TestHost(_root);
        var decoder = new FakeRecognizer();
        using var plugin = new Qwen3LocalPlugin(Http(() => new ByteArrayContent(bytes)), _ => decoder, Source(bytes));
        Assert.False(plugin.IsConfigured);
        await plugin.ActivateAsync(host);
        plugin.SelectModel(Qwen3LocalPlugin.ModelId);
        Assert.False(plugin.IsConfigured);
        await plugin.DownloadModelAsync(Qwen3LocalPlugin.ModelId, null, default);
        Assert.True(plugin.IsConfigured);
        var directory = Path.Combine(host.PluginDataDirectory, "Models", Qwen3LocalPlugin.ModelId);
        File.WriteAllText(Path.Combine(directory, "encoder.int8.onnx"), "");
        Assert.False(plugin.IsConfigured);
        await plugin.DownloadModelAsync(Qwen3LocalPlugin.ModelId, null, default);
        await plugin.LoadModelAsync(Qwen3LocalPlugin.ModelId, default);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.ExecuteSettingsActionAsync("remove-" + Qwen3LocalPlugin.ModelId, cancelled.Token));
        Assert.True(plugin.IsConfigured);
        Assert.False(decoder.Disposed);
        await plugin.ExecuteSettingsActionAsync(plugin.SettingsActions[0].Id, default);
        Assert.True(decoder.Disposed);
        Assert.False(Directory.Exists(directory));
        Assert.False(plugin.IsConfigured);
        Assert.Null(plugin.SelectedModelId);
        await plugin.DeactivateAsync();
        await plugin.ActivateAsync(host);
        Assert.False(plugin.IsConfigured);
        Assert.Null(plugin.SelectedModelId);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.ExecuteSettingsActionAsync("unknown", default));
    }

    [Fact]
    public async Task SelectionSurvivesReactivationAndRestartButRemovalClearsIt()
    {
        var bytes = Archive();
        var host = new TestHost(_root);
        using (var plugin = new Qwen3LocalPlugin(Http(() => new ByteArrayContent(bytes)), _ => new FakeRecognizer(), Source(bytes)))
        {
            await plugin.ActivateAsync(host);
            await plugin.DownloadModelAsync(Qwen3LocalPlugin.ModelId, null, default);
            Assert.Null(plugin.SelectedModelId);
            plugin.SelectModel(Qwen3LocalPlugin.ModelId);
            await plugin.DeactivateAsync();
            await plugin.ActivateAsync(host);
            Assert.Equal(Qwen3LocalPlugin.ModelId, plugin.SelectedModelId);
        }
        using var restarted = new Qwen3LocalPlugin(Http(() => new ByteArrayContent(bytes)), _ => new FakeRecognizer(), Source(bytes));
        await restarted.ActivateAsync(host);
        Assert.Equal(Qwen3LocalPlugin.ModelId, restarted.SelectedModelId);
        Assert.True(restarted.IsModelDownloaded(Qwen3LocalPlugin.ModelId));
        await restarted.RemoveModelAsync(Qwen3LocalPlugin.ModelId, default);
        await restarted.DeactivateAsync();
        await restarted.ActivateAsync(host);
        Assert.Null(restarted.SelectedModelId);
        host.SetSetting("selectedModel", "unknown-model");
        await restarted.DeactivateAsync();
        await restarted.ActivateAsync(host);
        Assert.Null(restarted.SelectedModelId);
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
    public async Task SelectModelAsyncPersistsTheSelectionThroughTheHost()
    {
        // The plugin keeps the SDK default, which forwards to the synchronous member.
        var host = new TestHost(_root);
        using var plugin = new Qwen3LocalPlugin(); await plugin.ActivateAsync(host);
        ITranscriptionEnginePlugin engine = plugin;
        await engine.SelectModelAsync(Qwen3LocalPlugin.LargeModelId, default);
        Assert.Equal(Qwen3LocalPlugin.LargeModelId, plugin.SelectedModelId);
        Assert.Equal(Qwen3LocalPlugin.LargeModelId, host.GetSetting<string>("selectedModel"));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.SelectModelAsync("../other", default));
        Assert.Equal(Qwen3LocalPlugin.LargeModelId, plugin.SelectedModelId);
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

    [Theory]
    [InlineData(.2f, .707f)]
    [InlineData(.9f, .9f)]
    [InlineData(.005f, .005f)]
    public async Task QuietAudioIsRaisedToTheRecorderLevelBeforeDecoding(float peak, float expected)
    {
        var bytes = Archive();
        var decoder = new FakeRecognizer();
        using var plugin = new Qwen3LocalPlugin(Http(() => new ByteArrayContent(bytes)), _ => decoder, Source(bytes));
        await plugin.ActivateAsync(new TestHost(_root));
        await plugin.DownloadModelAsync(Qwen3LocalPlugin.ModelId, null, default);
        var samples = Enumerable.Range(0, QwenAudio.SampleRate * 12).Select(i => i % 100 == 0 ? peak : peak / 4).ToArray();
        await plugin.TranscribePcmAsync(samples, null, false, default);
        Assert.Equal(2, decoder.Peaks.Count);
        Assert.All(decoder.Peaks, value => Assert.Equal(expected, value, 4));
    }

    [Theory]
    [InlineData("language German<asr_text>Es leben noch viele Menschen.", "Es leben noch viele Menschen.")]
    [InlineData("Nachdem der Damm erbaut war. language German<asr_text>Kamen die Fluten.", "Nachdem der Damm erbaut war. Kamen die Fluten.")]
    [InlineData("<asr_text>Hello there.", "Hello there.")]
    [InlineData("Die Sprache language ist wichtig.", "Die Sprache language ist wichtig.")]
    public void LanguageMarkersLeakedByTheModelAreRemoved(string text, string expected) =>
        Assert.Equal(expected, QwenRecognizer.StripLanguageMarkers(text));

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
    private static QwenAssetSource Source(byte[] bytes) => new("Qwen3-ASR 0.6B", "https://fixture.invalid/model", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
    private static QwenAssetSource FileSource(Dictionary<string, byte[]> files) =>
        QwenAssetSource.FromFiles("Qwen3-ASR 1.7B", "https://fixture.invalid/pinned", files.Select(file =>
            new QwenAssetFile(file.Key, Convert.ToHexStringLower(SHA256.HashData(file.Value)), file.Value.Length)).ToArray());
    internal static HttpClient Http(Func<HttpContent> content) => Http(_ => content());
    internal static HttpClient Http(Func<HttpRequestMessage, HttpContent> content) => new(new Handler(content));
    private sealed class Handler(Func<HttpRequestMessage, HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content(request) }); }
    }
    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class CallbackProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
    private sealed class FakeRecognizer : IQwenRecognizer
    {
        public List<int> Lengths { get; } = [];
        public List<float> Peaks { get; } = [];
        public string? Language; public bool Disposed; public Action? AfterDecode;
        public string Decode(float[] samples, string? language)
        {
            Lengths.Add(samples.Length); Peaks.Add(samples.Max(MathF.Abs)); Language = language;
            AfterDecode?.Invoke(); return "Test transcript.";
        }
        public void Dispose() => Disposed = true;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
