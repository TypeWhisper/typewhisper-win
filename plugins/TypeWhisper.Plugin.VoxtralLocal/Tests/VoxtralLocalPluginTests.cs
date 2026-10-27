using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.VoxtralLocal.Tests;

public sealed class VoxtralLocalPluginTests : IDisposable
{
    private const int SampleRate = 16000;
    private const string Root = "transcribe-native-windows-x86_64-cpu-vulkan";
    private const string Contract = """{"version":"0.3.1","header_hash":"57b1af43650f195d","backends":["vulkan","cpu"],"lane":"cpu-vulkan"}""";
    private static readonly byte[] Gguf = Encoding.UTF8.GetBytes("GGUF Voxtral test weights");
    // Built once: every archive carries its own timestamps and so its own hash.
    private static readonly byte[] RuntimeArchive = Archive();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "voxtral-local-tests-" + Guid.NewGuid().ToString("N"));

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
        using var plugin = new VoxtralLocalPlugin();

        Assert.Equal(manifest.Id, plugin.PluginId);
        Assert.Equal(manifest.Version, plugin.PluginVersion);
        Assert.Equal(manifest.Version, typeof(VoxtralLocalPlugin).Assembly.GetName().Version!.ToString(3));
    }

    [Fact]
    public void OffersOneRecommendedModelForThirteenLanguagesWithoutTranslationOrLivePreview()
    {
        using var plugin = new VoxtralLocalPlugin();

        Assert.Equal("voxtral-local", plugin.ProviderId);
        Assert.Equal("Voxtral (Local)", plugin.ProviderDisplayName);
        Assert.Equal(13, plugin.SupportedLanguages.Count);
        Assert.Contains("de", plugin.SupportedLanguages);
        Assert.False(plugin.SupportsTranslation);
        Assert.False(((ITranscriptionEnginePlugin)plugin).SupportsStreaming);
        Assert.False(plugin.SupportsLocalLivePreview);
        Assert.False(plugin.IsConfigured);
        Assert.Equal([TranscriptionAccelerationBackend.Cpu, TranscriptionAccelerationBackend.AmdVulkan], plugin.SupportedAccelerationBackends);
        var model = Assert.Single(plugin.TranscriptionModels);
        Assert.Equal("voxtral-mini-4b-realtime-q4", model.Id);
        Assert.True(model.IsRecommended);
        Assert.Equal("Mistral AI", model.Publisher);
        // The model plus the 17 MB runtime.
        Assert.Equal(2716, model.EstimatedSizeMB);
        Assert.Equal("~2.8 GB", model.SizeDescription);
    }

    [Fact]
    public void ModelIsPinnedToOneRevisionAndHash()
    {
        var model = VoxtralLocalModel.MiniQ4;

        Assert.Matches("^[0-9a-f]{40}$", VoxtralLocalModel.Revision);
        Assert.Equal($"https://huggingface.co/handy-computer/Voxtral-Mini-4B-Realtime-2602-gguf/resolve/{VoxtralLocalModel.Revision}/{model.FileName}",
            model.DownloadUrl);
        Assert.Matches("^[0-9a-f]{64}$", model.Sha256);
        Assert.Equal(2_830_493_984, model.SizeBytes);
        Assert.Equal("transcribe.dll", TranscribeCppAsset.Runtime.FileName);
    }

    [Fact]
    public async Task DownloadInstallsRuntimeAndModelAndSkipsThemNextTime()
    {
        var requests = new List<string>();
        using var plugin = Plugin(Server(requests), out _);
        await plugin.ActivateAsync(Host());
        var reports = new List<double>();

        Assert.False(plugin.IsModelDownloaded(VoxtralLocalModel.MiniQ4.Id));
        await plugin.DownloadModelAsync(VoxtralLocalModel.MiniQ4.Id, new SyncProgress(reports.Add), default);

        Assert.True(plugin.IsModelDownloaded(VoxtralLocalModel.MiniQ4.Id));
        Assert.Equal(["/runtime.tar.gz", "/model.gguf"], requests);
        Assert.Equal(1, reports[^1], 3);
        Assert.Equal(reports.Order(), reports);
        Assert.True(File.Exists(Path.Combine(RuntimeDirectory(), "transcribe.dll")));
        Assert.Equal(Gguf, File.ReadAllBytes(Path.Combine(ModelDirectory(), "model.gguf")));
        await plugin.DownloadModelAsync(VoxtralLocalModel.MiniQ4.Id, null, default);
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task ModelThatFailsItsChecksumIsNotInstalled()
    {
        var requests = new List<string>();
        using var plugin = Plugin(Server(requests), out _, model: TestModel() with { Sha256 = new string('0', 64) });
        await plugin.ActivateAsync(Host());

        await Assert.ThrowsAsync<IOException>(() => plugin.DownloadModelAsync(VoxtralLocalModel.MiniQ4.Id, null, default));

        Assert.False(plugin.IsModelDownloaded(VoxtralLocalModel.MiniQ4.Id));
        Assert.False(File.Exists(Path.Combine(ModelDirectory(), "model.gguf")));
        Assert.False(File.Exists(Path.Combine(ModelDirectory(), "model.gguf.download")));
    }

    [Fact]
    public async Task DownloadStopsBeforeRequestingWhenTheDiskIsFull()
    {
        var requests = new List<string>();
        using var plugin = Plugin(Server(requests), out _);
        plugin.AvailableBytes = _ => 4;
        await plugin.ActivateAsync(Host());

        await Assert.ThrowsAsync<InsufficientModelStorageException>(() => plugin.DownloadModelAsync(VoxtralLocalModel.MiniQ4.Id, null, default));

        Assert.Empty(requests);
    }

    [Fact]
    public async Task LoadingRequiresTheDownloadedModel()
    {
        using var plugin = Plugin(Server([]), out var recognizers);
        await plugin.ActivateAsync(Host());

        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync(VoxtralLocalModel.MiniQ4.Id, default));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.LoadModelAsync("parakeet-tdt-0.6b", default));
        Assert.Empty(recognizers);
    }

    [Fact]
    public async Task TranscribesTheWholeRecordingInOnePassWithoutALanguageHint()
    {
        using var plugin = Plugin(Server([]), out var recognizers, _ => "  Der Termin ist am 12. September.  ");
        await plugin.ActivateAsync(Host());
        await plugin.DownloadModelAsync(VoxtralLocalModel.MiniQ4.Id, null, default);
        plugin.SelectModel(VoxtralLocalModel.MiniQ4.Id);

        var result = await plugin.TranscribePcmAsync(new float[70 * SampleRate], "de", false, default);

        var recognizer = Assert.Single(recognizers);
        Assert.Equal([70 * SampleRate], recognizer.Lengths);
        Assert.Equal("Der Termin ist am 12. September.", result.Text);
        Assert.Null(result.DetectedLanguage);
        Assert.Equal(70, result.DurationSeconds, 3);
        Assert.Equal(Path.Combine(ModelDirectory(), "model.gguf"), recognizer.ModelPath);
        Assert.Equal(RuntimeDirectory(), recognizer.RuntimeDirectory);
    }

    [Fact]
    public async Task TranslationAndInvalidSamplesAreRejected()
    {
        using var plugin = Plugin(Server([]), out _);
        await plugin.ActivateAsync(Host());

        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.TranscribePcmAsync(new float[SampleRate], "de", true, default));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.TranscribePcmAsync(new[] { float.NaN }, "de", false, default));
        Assert.Equal("", (await plugin.TranscribePcmAsync(ReadOnlyMemory<float>.Empty, "de", false, default)).Text);
    }

    [Theory]
    [InlineData(TranscriptionAccelerationPreference.Auto, TranscriptionAccelerationPreference.Auto)]
    [InlineData(TranscriptionAccelerationPreference.Cpu, TranscriptionAccelerationPreference.Cpu)]
    [InlineData(TranscriptionAccelerationPreference.AmdVulkan, TranscriptionAccelerationPreference.AmdVulkan)]
    [InlineData(TranscriptionAccelerationPreference.NvidiaCuda, TranscriptionAccelerationPreference.Auto)]
    [InlineData(TranscriptionAccelerationPreference.AmdRocm, TranscriptionAccelerationPreference.Auto)]
    public async Task ProcessingDeviceReachesTheRecognizer(TranscriptionAccelerationPreference chosen, TranscriptionAccelerationPreference expected)
    {
        var status = new TranscriptionAccelerationStatus(TranscriptionAccelerationBackend.AmdVulkan, "Test GPU");
        using var plugin = Plugin(Server([]), out var recognizers, status: status);
        await plugin.ActivateAsync(Host());
        await plugin.DownloadModelAsync(VoxtralLocalModel.MiniQ4.Id, null, default);
        plugin.SetAccelerationPreference(chosen);

        Assert.Equal(expected, plugin.AccelerationPreference);
        await plugin.LoadModelAsync(VoxtralLocalModel.MiniQ4.Id, default);

        Assert.Equal(expected, Assert.Single(recognizers).Preference);
        Assert.Same(status, plugin.AccelerationStatus);
        await plugin.UnloadModelAsync();
        Assert.True(recognizers[0].Disposed);
        Assert.NotSame(status, plugin.AccelerationStatus);
    }

    [Fact]
    public async Task StatusWarnsThatTheProcessorIsSlow()
    {
        using var plugin = new VoxtralLocalPlugin();

        Assert.Equal(TranscriptionAccelerationBackend.AmdVulkan, plugin.AccelerationStatus.ActiveBackend);
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.Equal(TranscribeCppVoxtral.SlowOnProcessor, plugin.AccelerationStatus.Detail);

        var fallback = TranscribeCppVoxtral.CpuStatus("No graphics card with Vulkan support was found.");
        Assert.StartsWith("No graphics card", fallback.Detail);
        Assert.EndsWith(TranscribeCppVoxtral.SlowOnProcessor, fallback.Detail);
        var integrated = TranscribeCppVoxtral.GpuStatus(new(1, "Vulkan0", "AMD Radeon(TM) Graphics", "vulkan", 2, 0));
        Assert.Equal(TranscriptionAccelerationBackend.AmdVulkan, integrated.ActiveBackend);
        Assert.Equal(TranscribeCppVoxtral.SlowOnIntegratedGraphics, integrated.Detail);
        var dedicated = TranscribeCppVoxtral.GpuStatus(new(0, "Vulkan1", "NVIDIA GeForce RTX 4060 Ti", "vulkan", 1, 0));
        Assert.Equal("NVIDIA GeForce RTX 4060 Ti", dedicated.DisplayText);
        Assert.Null(dedicated.Detail);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task FailedLoadIsShownAsTheProcessingDeviceStatus()
    {
        var plugin = new VoxtralLocalPlugin(new HttpClient(new FileHandler([], RuntimeArchive)), TestModel(), RuntimeAsset(RuntimeArchive),
            (_, _, _) => throw new InvalidOperationException("No graphics card with Vulkan support was found."));
        using (plugin)
        {
            await plugin.ActivateAsync(Host());
            await plugin.DownloadModelAsync(VoxtralLocalModel.MiniQ4.Id, null, default);
            plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.AmdVulkan);

            await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync(VoxtralLocalModel.MiniQ4.Id, default));

            Assert.Equal("GPU unavailable", plugin.AccelerationStatus.DisplayText);
            Assert.Contains("Vulkan", plugin.AccelerationStatus.Detail);
        }
    }

    [Fact]
    public async Task SelectionSurvivesRestartAndRemovalKeepsTheRuntime()
    {
        var host = Host();
        using (var plugin = Plugin(Server([]), out var recognizers))
        {
            await plugin.ActivateAsync(host);
            await plugin.DownloadModelAsync(VoxtralLocalModel.MiniQ4.Id, null, default);
            plugin.SelectModel(VoxtralLocalModel.MiniQ4.Id);
            await plugin.LoadModelAsync(VoxtralLocalModel.MiniQ4.Id, default);
            Assert.True(plugin.IsConfigured);
            await plugin.DeactivateAsync();
            Assert.True(Assert.Single(recognizers).Disposed);
        }

        using var restarted = Plugin(Server([]), out var restartedRecognizers);
        await restarted.ActivateAsync(host);
        Assert.Equal(VoxtralLocalModel.MiniQ4.Id, restarted.SelectedModelId);
        await restarted.LoadModelAsync(VoxtralLocalModel.MiniQ4.Id, default);

        Assert.Equal("remove-model", Assert.Single(restarted.SettingsActions).Id);
        await restarted.ExecuteSettingsActionAsync("remove-model", default);

        Assert.True(Assert.Single(restartedRecognizers).Disposed);
        Assert.Null(restarted.SelectedModelId);
        Assert.False(restarted.IsConfigured);
        Assert.False(Directory.Exists(ModelDirectory()));
        Assert.True(File.Exists(Path.Combine(RuntimeDirectory(), "transcribe.dll")));
        Assert.Contains("not downloaded", await restarted.ExecuteSettingsActionAsync("remove-model", default));
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.ExecuteSettingsActionAsync("other", default));
    }

    [Fact]
    public async Task ProcessingDeviceIsASavedChoiceThatReloadsTheModel()
    {
        var host = Host();
        using (var plugin = Plugin(Server([]), out var recognizers, status: new(TranscriptionAccelerationBackend.AmdVulkan, "NVIDIA GeForce RTX 4060 Ti")))
        {
            await plugin.ActivateAsync(host);
            var setting = Assert.Single(plugin.TextSettings);
            Assert.Equal("acceleration", setting.Id);
            Assert.Equal("Auto", setting.Value);
            Assert.True(setting.SaveChoiceOnChange);
            Assert.Equal(PluginSettingsSection.Transcription, setting.Section);
            Assert.Equal(["Auto", "AmdVulkan", "Cpu"], setting.Choices.Select(choice => choice.Value));
            Assert.DoesNotContain("RTX", setting.Description);

            await plugin.DownloadModelAsync(VoxtralLocalModel.MiniQ4.Id, null, default);
            await plugin.LoadModelAsync(VoxtralLocalModel.MiniQ4.Id, default);
            Assert.Contains("NVIDIA GeForce RTX 4060 Ti (Vulkan)", plugin.TextSettings[0].Description);

            await plugin.SaveTextSettingAsync("acceleration", "Cpu", default);

            Assert.True(Assert.Single(recognizers).Disposed);
            Assert.Equal(TranscriptionAccelerationPreference.Cpu, plugin.AccelerationPreference);
            Assert.Equal("Cpu", plugin.TextSettings[0].Value);
            await plugin.TranscribePcmAsync(new float[SampleRate], null, false, default);
            Assert.Equal(TranscriptionAccelerationPreference.Cpu, recognizers[^1].Preference);
            await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("acceleration", "NvidiaCuda", default));
            await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("other", "Cpu", default));
        }

        using var restarted = Plugin(Server([]), out _);
        await restarted.ActivateAsync(host);
        Assert.Equal(TranscriptionAccelerationPreference.Cpu, restarted.AccelerationPreference);
    }

    [Fact]
    public void WavFilesAreReadFromTheirDataChunk()
    {
        var wav = Wav(SampleRate, [0, 16384, -32768]);

        Assert.Equal([0f, 0.5f, -1f], VoxtralLocalPlugin.DecodeWav(wav));
        Assert.Throws<ArgumentException>(() => VoxtralLocalPlugin.DecodeWav(Wav(44100, [0])));
        Assert.Throws<ArgumentException>(() => VoxtralLocalPlugin.DecodeWav(Encoding.ASCII.GetBytes("not a wav file")));
    }

    private VoxtralLocalPlugin Plugin(FileHandler server, out List<FakeRecognizer> recognizers, Func<float[], string>? transcribe = null,
        VoxtralLocalModel? model = null, TranscriptionAccelerationStatus? status = null)
    {
        var created = new List<FakeRecognizer>();
        recognizers = created;
        var plugin = new VoxtralLocalPlugin(new HttpClient(server), model ?? TestModel(), RuntimeAsset(RuntimeArchive),
            (modelPath, runtimeDirectory, preference) =>
            {
                var recognizer = new FakeRecognizer(transcribe ?? (_ => ""), status ?? new(TranscriptionAccelerationBackend.Cpu, "Using CPU"))
                    { ModelPath = modelPath, RuntimeDirectory = runtimeDirectory, Preference = preference };
                created.Add(recognizer);
                return recognizer;
            });
        plugin.AvailableBytes = _ => null;
        return plugin;
    }

    // The real model's id and display name with the test file in place of the 2.8 GB download.
    private static VoxtralLocalModel TestModel() => VoxtralLocalModel.MiniQ4 with
    {
        FileName = "model.gguf", DownloadUrl = "https://example.invalid/model.gguf", SizeBytes = Gguf.Length, Sha256 = Sha256(Gguf)
    };

    private static FileHandler Server(List<string> requests) => new(requests, RuntimeArchive);

    private VocabularyHostServices Host() => new(Path.Combine(_root, "data"));

    private string ModelDirectory() => Path.Combine(_root, "data", "Models", VoxtralLocalModel.MiniQ4.Id);

    private string RuntimeDirectory() => Path.Combine(_root, "data", "Runtimes", "transcribe-cpp", "0.3.1-cpu-vulkan");

    private static byte[] Archive()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            void Add(string name, string content) =>
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, Root + "/"));
            Add(Root + "/contract.json", Contract);
            Add(Root + "/transcribe.dll", "native library");
            Add(Root + "/ggml.dll", "ggml");
            Add(Root + "/licenses/LICENSE", "MIT");
        }
        return buffer.ToArray();
    }

    private static TranscribeCppAsset RuntimeAsset(byte[] archive) =>
        new("Test runtime", "https://example.invalid/runtime.tar.gz", Sha256(archive), archive.Length, "transcribe.dll", Root);

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Wav(int sampleRate, short[] samples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples) writer.Write(sample);
        return stream.ToArray();
    }

    internal sealed class FakeRecognizer(Func<float[], string> transcribe, TranscriptionAccelerationStatus status) : IVoxtralRecognizer
    {
        public List<int> Lengths { get; } = [];
        public string? ModelPath { get; init; }
        public string? RuntimeDirectory { get; init; }
        public TranscriptionAccelerationPreference Preference { get; init; }
        public bool Disposed { get; private set; }
        public TranscriptionAccelerationStatus Status => status;

        public string Transcribe(float[] samples, CancellationToken cancellationToken)
        {
            Lengths.Add(samples.Length);
            return transcribe(samples);
        }

        public void Dispose() => Disposed = true;
    }

    // Serves the runtime archive and the model file and records the requested paths.
    internal sealed class FileHandler(List<string> requests, byte[] archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (requests) requests.Add(path);
            return Task.FromResult(path switch
            {
                "/runtime.tar.gz" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) },
                "/model.gguf" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Gguf) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
