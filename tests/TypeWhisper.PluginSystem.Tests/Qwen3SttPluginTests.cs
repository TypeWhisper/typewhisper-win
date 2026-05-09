using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TypeWhisper.Plugin.Qwen3Stt;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class Qwen3SttPluginTests
{
    private const string IntegrationWavEnvironmentVariable = "TYPEWHISPER_QWEN3_ONNX_TEST_WAV";
    private const string IntegrationModelIdEnvironmentVariable = "TYPEWHISPER_QWEN3_ONNX_TEST_MODEL_ID";

    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.Qwen3Stt", "manifest.json"));
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var sut = new Qwen3SttPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public async Task ActivateAsync_UsesLocalOnnxDefaultsAndNoServerConfiguration()
    {
        var host = new TestPluginHostServices();

        var sut = new Qwen3SttPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.qwen3-stt", sut.PluginId);
        Assert.Equal("qwen3-stt", sut.ProviderId);
        Assert.Equal("Qwen3 ASR (ONNX)", sut.ProviderDisplayName);
        Assert.Equal(Qwen3ModelCatalog.DefaultModelId, sut.SelectedModelId);
        Assert.True(sut.SupportsModelDownload);
        Assert.False(sut.SupportsTranslation);
        Assert.False(sut.IsConfigured);
        Assert.DoesNotContain(sut.TranscriptionModels, m => m.Id == "Qwen/Qwen3-ASR");
        Assert.Equal(4, sut.TranscriptionModels.Count);
        Assert.Contains("de", sut.SupportedLanguages);
        Assert.Contains("fil", sut.SupportedLanguages);
        Assert.Contains("tl", sut.SupportedLanguages);
        Assert.DoesNotContain(host.SettingKeys, key => key.Contains("base", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(host.SettingKeys, key => key.Contains("server", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ModelCatalog_UsesExpectedOnnxTarballBundlesAndRequiredFiles()
    {
        Assert.Equal(
            [
                "qwen3-asr-0.6b-int4",
                "qwen3-asr-0.6b-fp32",
                "qwen3-asr-1.7b-int4",
                "qwen3-asr-1.7b-fp32"
            ],
            Qwen3ModelCatalog.Models.Select(model => model.Id).ToArray());

        Assert.All(Qwen3ModelCatalog.Models, model =>
        {
            Assert.EndsWith(".tar.gz", model.ArchiveFileName);
            Assert.StartsWith("https://huggingface.co/andrewleech/qwen3-asr-", model.ArchiveUrl);
            Assert.Contains("embed_tokens.bin", model.RequiredFiles);
            Assert.Contains("tokenizer.json", model.RequiredFiles);
            Assert.Contains("config.json", model.RequiredFiles);
            Assert.Contains("preprocessor_config.json", model.RequiredFiles);
        });

        var defaultModel = Qwen3ModelCatalog.GetModel(Qwen3ModelCatalog.DefaultModelId);
        Assert.True(defaultModel.IsRecommended);
        Assert.True(defaultModel.IsQuantized);
        Assert.Equal("andrewleech/qwen3-asr-0.6b-onnx", defaultModel.RepositoryId);
        Assert.Equal("qwen3-asr-0.6b-int4.tar.gz", defaultModel.ArchiveFileName);
        Assert.Contains("encoder.int4.onnx", defaultModel.RequiredFiles);
        Assert.Contains("decoder_init.int4.onnx", defaultModel.RequiredFiles);
        Assert.Contains("decoder_step.int4.onnx", defaultModel.RequiredFiles);
        Assert.Contains("decoder_weights.int4.data", defaultModel.RequiredFiles);
    }

    [Fact]
    public void DirectMlOptIn_IsDisabledByDefaultAndRequiresExplicitEnvironmentValue()
    {
        var previous = Environment.GetEnvironmentVariable(Qwen3ModelCatalog.DirectMlEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(Qwen3ModelCatalog.DirectMlEnvironmentVariable, null);
            Assert.False(Qwen3ModelCatalog.IsDirectMlOptInEnabled());

            Environment.SetEnvironmentVariable(Qwen3ModelCatalog.DirectMlEnvironmentVariable, "true");
            Assert.True(Qwen3ModelCatalog.IsDirectMlOptInEnabled());

            Environment.SetEnvironmentVariable(Qwen3ModelCatalog.DirectMlEnvironmentVariable, "1");
            Assert.True(Qwen3ModelCatalog.IsDirectMlOptInEnabled());

            Environment.SetEnvironmentVariable(Qwen3ModelCatalog.DirectMlEnvironmentVariable, "false");
            Assert.False(Qwen3ModelCatalog.IsDirectMlOptInEnabled());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Qwen3ModelCatalog.DirectMlEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task LoadModelAsync_RejectsMissingRequiredOnnxFiles()
    {
        var previousOverride = Environment.GetEnvironmentVariable(Qwen3ModelCatalog.ModelDirectoryEnvironmentVariable);
        var tempDir = Path.Combine(Path.GetTempPath(), "typewhisper-qwen3-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(Qwen3ModelCatalog.ModelDirectoryEnvironmentVariable, null);
            var host = new TestPluginHostServices { PluginDataDirectory = tempDir };

            var sut = new Qwen3SttPlugin();
            await sut.ActivateAsync(host);

            var ex = await Assert.ThrowsAsync<FileNotFoundException>(
                () => sut.LoadModelAsync(Qwen3ModelCatalog.DefaultModelId, CancellationToken.None));
            Assert.Contains("Download the model first", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Qwen3ModelCatalog.ModelDirectoryEnvironmentVariable, previousOverride);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TranscribeAsync_RetriesOnCpuWhenDirectMlInferenceFails()
    {
        var previousOverride = Environment.GetEnvironmentVariable(Qwen3ModelCatalog.ModelDirectoryEnvironmentVariable);
        var tempDir = Path.Combine(Path.GetTempPath(), "typewhisper-qwen3-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            foreach (var file in Qwen3ModelCatalog.GetModel(Qwen3ModelCatalog.DefaultModelId).RequiredFiles)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(tempDir, file))!);
                File.WriteAllText(Path.Combine(tempDir, file), "");
            }

            Environment.SetEnvironmentVariable(Qwen3ModelCatalog.ModelDirectoryEnvironmentVariable, tempDir);
            var host = new TestPluginHostServices();
            var factory = new FakeQwen3TranscriberFactory();
            var sut = new Qwen3SttPlugin(new HttpClient(), factory);
            await sut.ActivateAsync(host);
            await sut.LoadModelAsync(Qwen3ModelCatalog.DefaultModelId, CancellationToken.None);

            var result = await sut.TranscribeAsync([1, 2, 3], "de", translate: false, prompt: "TypeWhisper", CancellationToken.None);

            Assert.Equal("CPU retry worked", result.Text);
            Assert.Equal("de", result.DetectedLanguage);
            Assert.Equal(1, factory.DirectMlTranscriber.TranscribeCalls);
            Assert.Equal(1, factory.CpuTranscriber.TranscribeCalls);
            Assert.Equal(1, factory.CpuLoadCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Qwen3ModelCatalog.ModelDirectoryEnvironmentVariable, previousOverride);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Qwen3OnnxModelFact]
    public async Task Integration_TranscribesSmallWav_WhenModelDirectoryIsProvided()
    {
        var modelDir = Environment.GetEnvironmentVariable(Qwen3ModelCatalog.ModelDirectoryEnvironmentVariable)!;
        var modelId = Environment.GetEnvironmentVariable(IntegrationModelIdEnvironmentVariable)
            ?? Qwen3ModelCatalog.DefaultModelId;
        var wavPath = FindIntegrationWav(modelDir)!;
        var host = new TestPluginHostServices();

        var sut = new Qwen3SttPlugin();
        await sut.ActivateAsync(host);
        await sut.LoadModelAsync(modelId, CancellationToken.None);

        var result = await sut.TranscribeAsync(
            await File.ReadAllBytesAsync(wavPath),
            language: "en",
            translate: false,
            prompt: null,
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.Equal("en", result.DetectedLanguage);
    }

    [Fact]
    public async Task ActivateAsync_NormalizesLegacyServerModelSelectionInsidePlugin()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("selectedModel", Qwen3ModelCatalog.LegacyServerModelId);

        var sut = new Qwen3SttPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal(
            Qwen3ModelCatalog.DefaultModelId,
            Qwen3ModelCatalog.NormalizeModelId(Qwen3ModelCatalog.LegacyServerModelId));
        Assert.Equal(
            Qwen3ModelCatalog.DefaultModelId,
            Qwen3ModelCatalog.GetModel(Qwen3ModelCatalog.LegacyServerModelId).Id);
        Assert.Equal(Qwen3ModelCatalog.DefaultModelId, sut.SelectedModelId);
        Assert.Equal(Qwen3ModelCatalog.DefaultModelId, host.GetSetting<string>("selectedModel"));
    }

    [Fact]
    public void LanguageMapper_UsesQwenNamesAndDoesNotDefaultToEnglish()
    {
        Assert.Null(Qwen3LanguageMapper.ResolveLanguageName(null));
        Assert.Null(Qwen3LanguageMapper.ResolveLanguageName(""));
        Assert.Null(Qwen3LanguageMapper.ResolveLanguageName("   "));
        Assert.Null(Qwen3LanguageMapper.ResolveLanguageName("uk"));
        Assert.Equal("German", Qwen3LanguageMapper.ResolveLanguageName("de"));
        Assert.Equal("Filipino", Qwen3LanguageMapper.ResolveLanguageName("fil"));
        Assert.Equal("Filipino", Qwen3LanguageMapper.ResolveLanguageName("tl"));
        Assert.Equal("fr", Qwen3LanguageMapper.LanguageCodeForQwenLanguageName("French"));
        Assert.Null(Qwen3LanguageMapper.LanguageCodeForQwenLanguageName("French,English"));
    }

    [Fact]
    public void ContextBiasFormatter_AddsBaseInstructionAndTechnicalTerms()
    {
        var context = Qwen3ContextBiasFormatter.Format("TypeWhisper, TypeWhisper, MLX");

        Assert.Contains(Qwen3ContextBiasFormatter.BaseInstruction, context);
        Assert.Contains("Technical terms: TypeWhisper, MLX.", context);
    }

    [Fact]
    public void OutputParser_ParsesLanguageTaggedQwenOutput()
    {
        var parsed = Qwen3AsrOutputParser.Parse("language German<asr_text>Hallo Welt", userLanguage: null);

        Assert.Equal("German", parsed.LanguageName);
        Assert.Equal("Hallo Welt", parsed.Text);
        Assert.Equal("de", parsed.DetectedLanguageCode);
    }

    [Fact]
    public void OutputParser_ForcedLanguageReturnsTextOnly()
    {
        var parsed = Qwen3AsrOutputParser.Parse("Hallo Welt", userLanguage: "German");

        Assert.Equal("German", parsed.LanguageName);
        Assert.Equal("Hallo Welt", parsed.Text);
        Assert.Equal("de", parsed.DetectedLanguageCode);
    }

    [Fact]
    public void TranscriptGuard_RemovesOnlyLikelyTrailingFrenchOuiArtifact()
    {
        Assert.Equal(
            "Je vais envoyer le fichier.",
            QwenTranscriptGuard.RemovingLikelyTrailingArtifact("Je vais envoyer le fichier. oui", "French"));
        Assert.Equal(
            "Je pense que oui.",
            QwenTranscriptGuard.RemovingLikelyTrailingArtifact("Je pense que oui.", "French"));
        Assert.Equal(
            "I will send it. oui",
            QwenTranscriptGuard.RemovingLikelyTrailingArtifact("I will send it. oui", "English"));
    }

    [Fact]
    public void TranscriptGuard_DetectsLikelyLoopsAndPrefersCleanerFallback()
    {
        var looped = string.Join(' ', Enumerable.Repeat("hello", 18));

        Assert.True(QwenTranscriptGuard.IsLikelyLooped(looped));
        Assert.Equal(
            "hello world",
            QwenTranscriptGuard.PreferredTranscript(looped, "hello world"));
    }

    [Fact]
    public void DecoderInitInputs_BindPositionIdsAndAudioLength()
    {
        var audioFeatures = new DenseTensor<float>(new float[8], new[] { 1, 4, 2 });

        var inputs = Qwen3OnnxTranscriber.CreateDecoderInitInputs(
            ["input_ids", "position_ids", "audio_len", "audio_offset", "audio_features"],
            [10, 11, 12],
            audioFeatures,
            audioOffset: 2,
            audioTokenCount: 4);

        AssertLongTensor(inputs, "input_ids", [1, 3], [10, 11, 12]);
        AssertLongTensor(inputs, "position_ids", [1, 3], [0, 1, 2]);
        AssertLongTensor(inputs, "audio_len", [1], [4]);
        AssertLongTensor(inputs, "audio_offset", [1], [2]);
        Assert.Same(audioFeatures, inputs.Single(input => input.Name == "audio_features").Value);
    }

    [Fact]
    public void DecoderStepInputs_BindPositionIdsAndPastSequenceLengthBeforeCache()
    {
        var embedding = new DenseTensor<Half>(new Half[3], new[] { 1, 1, 3 });
        var cache = new DenseTensor<float>(new float[2], new[] { 1, 2 });

        var inputs = Qwen3OnnxTranscriber.CreateDecoderStepInputs(
            ["input_embeds", "position_ids", "past_seq_len", "offset", "layer0_cache"],
            embedding,
            [cache],
            positionId: 128);

        Assert.Same(embedding, inputs.Single(input => input.Name == "input_embeds").Value);
        AssertLongTensor(inputs, "position_ids", [1, 1], [128]);
        AssertLongTensor(inputs, "past_seq_len", [1], [128]);
        AssertLongTensor(inputs, "offset", [1], [128]);
        Assert.Same(cache, inputs.Single(input => input.Name == "layer0_cache").Value);
    }

    [Fact]
    public void DecoderStepInputs_UseFloatEmbeddingWhenDecoderInputRequiresSingle()
    {
        var embedding = new DenseTensor<float>(new float[3], new[] { 1, 1, 3 });

        var inputs = Qwen3OnnxTranscriber.CreateDecoderStepInputs(
            [new Qwen3OnnxInputSpec("input_embeds", typeof(float))],
            elementType => elementType == typeof(float)
                ? embedding
                : throw new InvalidOperationException($"Unexpected embedding type {elementType}."),
            [],
            positionId: 128);

        Assert.Same(embedding, inputs.Single(input => input.Name == "input_embeds").Value);
    }

    private static void AssertLongTensor(
        IReadOnlyList<NamedOnnxValue> inputs,
        string name,
        int[] dimensions,
        long[] values)
    {
        var tensor = Assert.IsAssignableFrom<Tensor<long>>(inputs.Single(input => input.Name == name).Value);
        Assert.Equal(dimensions, tensor.Dimensions.ToArray());
        Assert.Equal(values, tensor.ToArray());
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];
        public IReadOnlyList<string> SettingKeys => _settings.Keys.ToArray();

        public Task StoreSecretAsync(string key, string value)
        {
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.TryGetValue(key, out var value) ? value : null);

        public Task DeleteSecretAsync(string key)
        {
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value)
                ? value.Deserialize<T>(JsonOptions)
                : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, JsonOptions);

        public string PluginDataDirectory { get; init; } = Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class FakeQwen3TranscriberFactory : IQwen3TranscriberFactory
    {
        public FakeQwen3Transcriber DirectMlTranscriber { get; } = new(
            usesDirectMl: true,
            new InvalidOperationException("DmlExecutionProvider node_Shape_2 80070057 The parameter is incorrect."));

        public FakeQwen3Transcriber CpuTranscriber { get; } = new(
            usesDirectMl: false,
            result: new Qwen3Transcription("CPU retry worked", "de", 1.25));

        public int CpuLoadCount { get; private set; }

        public IQwen3Transcriber Load(string modelDirectory, Qwen3ModelDefinition model) =>
            DirectMlTranscriber;

        public IQwen3Transcriber LoadCpu(string modelDirectory, Qwen3ModelDefinition model)
        {
            CpuLoadCount++;
            return CpuTranscriber;
        }
    }

    private sealed class FakeQwen3Transcriber(
        bool usesDirectMl,
        Exception? error = null,
        Qwen3Transcription? result = null) : IQwen3Transcriber
    {
        public bool UsesDirectMl { get; } = usesDirectMl;
        public int TranscribeCalls { get; private set; }

        public Qwen3Transcription Transcribe(byte[] wavAudio, string? language, string? prompt, CancellationToken ct)
        {
            TranscribeCalls++;
            if (error is not null)
                throw error;
            return result ?? new Qwen3Transcription("", null, 0);
        }

        public void Dispose() { }
    }

    private sealed class Qwen3OnnxModelFactAttribute : FactAttribute
    {
        public Qwen3OnnxModelFactAttribute()
        {
            var modelDir = Environment.GetEnvironmentVariable(Qwen3ModelCatalog.ModelDirectoryEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
            {
                Skip = $"{Qwen3ModelCatalog.ModelDirectoryEnvironmentVariable} is not set to a local Qwen3 ONNX model directory.";
                return;
            }

            if (FindIntegrationWav(modelDir) is null)
                Skip = $"Set {IntegrationWavEnvironmentVariable} or place integration.wav in the Qwen3 ONNX model directory.";
        }
    }

    private static string? FindIntegrationWav(string modelDir)
    {
        var configured = Environment.GetEnvironmentVariable(IntegrationWavEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var defaultWav = Path.Combine(modelDir, "integration.wav");
        return File.Exists(defaultWav) ? defaultWav : null;
    }
}
