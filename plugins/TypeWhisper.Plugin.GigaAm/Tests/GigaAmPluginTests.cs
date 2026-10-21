using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.GigaAm;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GigaAm.Tests;

public sealed class GigaAmPluginTests : IDisposable
{
    private const int SampleRate = 16000;
    private const string FixtureUrl = "https://fixture.invalid/model";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gigaam-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void VersionsMatchTheManifestAndTheProject()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        using var plugin = new GigaAmPlugin();

        Assert.Equal(manifest.Id, plugin.PluginId);
        Assert.Equal(manifest.Version, plugin.PluginVersion);
        Assert.Equal(manifest.Version, typeof(GigaAmPlugin).Assembly.GetName().Version!.ToString(3));
    }

    [Fact]
    public void OffersOneRussianModelWithoutTranslation()
    {
        using var plugin = new GigaAmPlugin();

        Assert.Equal("gigaam", plugin.ProviderId);
        Assert.Equal(["ru"], plugin.SupportedLanguages);
        Assert.False(plugin.SupportsTranslation);
        Assert.False(plugin.IsConfigured);
        Assert.True(plugin.SupportsLocalLivePreview);
        var model = Assert.Single(plugin.TranscriptionModels);
        Assert.Equal("gigaam-v3-e2e-rnnt", model.Id);
        Assert.Equal(["ru"], model.LanguageCodes);
        Assert.Equal(216, model.EstimatedSizeMB);
    }

    [Fact]
    public void ModelFilesArePinnedToOneRevision()
    {
        Assert.Matches("^[0-9a-f]{40}$", GigaAmModelAssets.Revision);
        Assert.Equal("https://huggingface.co/istupakov/gigaam-v3-onnx/resolve/" + GigaAmModelAssets.Revision, GigaAmModelAssets.DefaultBaseUrl);
        Assert.Equal([GigaAmModelFiles.Encoder, GigaAmModelFiles.Decoder, GigaAmModelFiles.Joint, GigaAmModelFiles.Vocabulary],
            GigaAmModelAssets.DefaultFiles.Select(file => file.Name));
        Assert.All(GigaAmModelAssets.DefaultFiles, file => Assert.Matches("^[0-9a-f]{64}$", file.Sha256));
    }

    [Fact]
    public async Task DownloadPublishesVerifiedFilesAndSkipsThemNextTime()
    {
        using var server = new FileServer(ModelBytes());
        using var plugin = Plugin(server, out _);
        await plugin.ActivateAsync(Host());
        var reports = new List<double>();

        Assert.False(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
        await plugin.DownloadModelAsync(GigaAmPlugin.ModelId, new SyncProgress(reports.Add), default);

        Assert.True(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
        Assert.Equal(4, server.Requests.Count);
        Assert.Equal(1, reports[^1], 3);
        Assert.DoesNotContain(Directory.EnumerateFiles(ModelDirectory()), path => path.EndsWith(".unverified", StringComparison.Ordinal));
        await plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, default);
        Assert.Equal(4, server.Requests.Count);
    }

    [Fact]
    public async Task ChecksumMismatchNeverBecomesAModelFile()
    {
        var files = ModelBytes();
        using var server = new FileServer(files);
        var definitions = Definitions(files).Select(file => file.Name == GigaAmModelFiles.Joint ? file with { Sha256 = new string('0', 64) } : file).ToArray();
        // The plugin disposes the client it shares with its assets.
        var http = new HttpClient(server, disposeHandler: false);
        using var plugin = new GigaAmPlugin(http, (_, _) => new FakeRecognizer(_ => ""),
            new GigaAmModelAssets(http, FixtureUrl, definitions, _ => null));
        await plugin.ActivateAsync(Host());

        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, default));

        Assert.False(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
        Assert.False(File.Exists(Path.Combine(ModelDirectory(), GigaAmModelFiles.Joint)));
        Assert.False(File.Exists(Path.Combine(ModelDirectory(), GigaAmModelFiles.Joint + ".unverified")));
    }

    [Fact]
    public async Task TruncatedFileIsDownloadedAgain()
    {
        using var server = new FileServer(ModelBytes());
        using var plugin = Plugin(server, out _);
        await plugin.ActivateAsync(Host());
        await plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, default);
        File.WriteAllText(Path.Combine(ModelDirectory(), GigaAmModelFiles.Encoder), "x");

        Assert.False(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
        await plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, default);

        Assert.True(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
        Assert.Equal(5, server.Requests.Count);
    }

    [Fact]
    public async Task DownloadStopsBeforeRequestingWhenTheDiskIsFull()
    {
        var files = ModelBytes();
        using var server = new FileServer(files);
        var http = new HttpClient(server, disposeHandler: false);
        using var plugin = new GigaAmPlugin(http, (_, _) => new FakeRecognizer(_ => ""),
            new GigaAmModelAssets(http, FixtureUrl, Definitions(files), _ => 1024));
        await plugin.ActivateAsync(Host());

        await Assert.ThrowsAsync<InsufficientModelStorageException>(() => plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, default));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task LoadingRequiresTheDownloadedModel()
    {
        using var server = new FileServer(ModelBytes());
        using var plugin = Plugin(server, out _);
        await plugin.ActivateAsync(Host());

        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync(GigaAmPlugin.ModelId, default));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.LoadModelAsync("parakeet-tdt-0.6b", default));
    }

    [Fact]
    public async Task TranscribesLongAudioInChunksAsRussian()
    {
        var replies = new Queue<string>(["Спустя десять лет", "Эта истина верна.", "", "Новое предложение."]);
        using var server = new FileServer(ModelBytes());
        using var plugin = Plugin(server, out var recognizers, _ => replies.Dequeue());
        await plugin.ActivateAsync(Host());
        await plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, default);
        plugin.SelectModel(GigaAmPlugin.ModelId);

        var result = await plugin.TranscribePcmAsync(Noise(70 * SampleRate), "de", false, default);

        var recognizer = Assert.Single(recognizers);
        Assert.Equal(4, recognizer.Lengths.Count);
        Assert.All(recognizer.Lengths, length => Assert.InRange(length, 1, GigaAmChunking.MaximumChunkSeconds * SampleRate));
        Assert.Equal("Спустя десять лет эта истина верна. Новое предложение.", result.Text);
        Assert.Equal("ru", result.DetectedLanguage);
        Assert.Equal(70, result.DurationSeconds, 3);
        Assert.Equal(["Спустя десять лет", "эта истина верна.", "Новое предложение."], result.Segments.Select(segment => segment.Text));
        Assert.Equal(0, result.Segments[0].Start);
        Assert.Equal(70, result.Segments[^1].End, 3);
    }

    [Fact]
    public async Task TranslationAndInvalidSamplesAreRejected()
    {
        using var server = new FileServer(ModelBytes());
        using var plugin = Plugin(server, out _);
        await plugin.ActivateAsync(Host());

        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.TranscribePcmAsync(new float[SampleRate], "ru", true, default));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.TranscribePcmAsync(new[] { float.NaN }, "ru", false, default));
        Assert.Equal("", (await plugin.TranscribePcmAsync(ReadOnlyMemory<float>.Empty, "ru", false, default)).Text);
    }

    [Fact]
    public async Task SelectionSurvivesRestartAndRemovalClearsIt()
    {
        var host = Host();
        using var server = new FileServer(ModelBytes());
        using (var plugin = Plugin(server, out var recognizers))
        {
            await plugin.ActivateAsync(host);
            await plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, default);
            plugin.SelectModel(GigaAmPlugin.ModelId);
            await plugin.LoadModelAsync(GigaAmPlugin.ModelId, default);
            Assert.True(plugin.IsConfigured);
            Assert.Equal(GigaAmPlugin.PreprocessorPath, Assert.Single(recognizers).PreprocessorPath);
            await plugin.DeactivateAsync();
            Assert.True(recognizers[0].Disposed);
        }

        using var restarted = Plugin(server, out var restartedRecognizers);
        await restarted.ActivateAsync(host);
        Assert.Equal(GigaAmPlugin.ModelId, restarted.SelectedModelId);
        await restarted.LoadModelAsync(GigaAmPlugin.ModelId, default);

        await restarted.RemoveModelAsync(GigaAmPlugin.ModelId, default);

        Assert.True(Assert.Single(restartedRecognizers).Disposed);
        Assert.Null(restarted.SelectedModelId);
        Assert.False(restarted.IsConfigured);
        Assert.False(Directory.Exists(ModelDirectory()));
    }

    [Fact]
    public void WavFilesAreReadFromTheirDataChunk()
    {
        var wav = Wav(SampleRate, [0, 16384, -32768], extraChunk: true);

        Assert.Equal([0f, 0.5f, -1f], GigaAmPlugin.DecodeWav(wav));
        Assert.Throws<ArgumentException>(() => GigaAmPlugin.DecodeWav(Wav(44100, [0])));
        Assert.Throws<ArgumentException>(() => GigaAmPlugin.DecodeWav(Encoding.ASCII.GetBytes("not a wav file")));
    }

    private GigaAmPlugin Plugin(FileServer server, out List<FakeRecognizer> recognizers, Func<float[], string>? transcribe = null)
    {
        var created = new List<FakeRecognizer>();
        recognizers = created;
        var http = new HttpClient(server, disposeHandler: false);
        return new GigaAmPlugin(http, (directory, preprocessor) =>
        {
            Assert.Equal(ModelDirectory(), directory);
            var recognizer = new FakeRecognizer(transcribe ?? (_ => "")) { PreprocessorPath = preprocessor };
            created.Add(recognizer);
            return recognizer;
        }, new GigaAmModelAssets(http, FixtureUrl, Definitions(server.Files), _ => null));
    }

    private VocabularyHostServices Host() => new(Path.Combine(_root, "data"));

    private string ModelDirectory() => Path.Combine(_root, "data", "Models", GigaAmPlugin.ModelId);

    private static Dictionary<string, byte[]> ModelBytes() => new()
    {
        [GigaAmModelFiles.Encoder] = Encoding.UTF8.GetBytes("encoder weights"),
        [GigaAmModelFiles.Decoder] = Encoding.UTF8.GetBytes("decoder weights"),
        [GigaAmModelFiles.Joint] = Encoding.UTF8.GetBytes("joint weights"),
        [GigaAmModelFiles.Vocabulary] = Encoding.UTF8.GetBytes("▁да 0\n<blk> 1\n")
    };

    private static GigaAmModelFile[] Definitions(IReadOnlyDictionary<string, byte[]> files) =>
        files.Select(file => new GigaAmModelFile(file.Key, file.Value.Length, Convert.ToHexStringLower(SHA256.HashData(file.Value)))).ToArray();

    private static float[] Noise(int length)
    {
        var random = new Random(7);
        var samples = new float[length];
        for (var index = 0; index < length; index++) samples[index] = (float)(random.NextDouble() - 0.5) * 0.2f;
        return samples;
    }

    private static byte[] Wav(int sampleRate, short[] samples, bool extraChunk = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var list = extraChunk ? 12 : 0;
        writer.Write("RIFF"u8);
        writer.Write(36 + list + samples.Length * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        if (extraChunk)
        {
            writer.Write("LIST"u8);
            writer.Write(4);
            writer.Write("INFO"u8);
        }
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples) writer.Write(sample);
        return stream.ToArray();
    }

    private sealed class FakeRecognizer(Func<float[], string> transcribe) : IGigaAmRecognizer
    {
        public List<int> Lengths { get; } = [];
        public string? PreprocessorPath { get; init; }
        public bool Disposed { get; private set; }

        public string Transcribe(float[] samples, CancellationToken cancellationToken)
        {
            Lengths.Add(samples.Length);
            return transcribe(samples);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FileServer(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files => files;
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = request.RequestUri!.Segments[^1];
            lock (Requests) Requests.Add(name);
            return Task.FromResult(files.TryGetValue(name, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
