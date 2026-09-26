using System.Runtime.CompilerServices;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK.Models;
using Whisper.net;

namespace TypeWhisper.PluginSystem.Tests;

public partial class WhisperCppPluginTests
{
    [Fact]
    public async Task ConfigurationRequiresAnActivatedDownloadedSelectedModel()
    {
        using var temp = new TempDirectory();
        using var plugin = new WhisperCppPlugin();
        Assert.False(plugin.IsConfigured);
        var host = new FakePluginHostServices(temp.Path);
        host.SetSetting("selectedModel", "unknown-model");
        await plugin.ActivateAsync(host);
        Assert.False(plugin.IsConfigured);
        plugin.SelectModel("tiny");
        Assert.False(plugin.IsConfigured);
        Directory.CreateDirectory(Path.Join(temp.Path, "Models"));
        var modelPath = Path.Join(temp.Path, "Models", "ggml-tiny.bin");
        CreateModelFixture(modelPath);
        Assert.True(plugin.IsConfigured);
        File.Delete(modelPath);
        Assert.False(plugin.IsConfigured);
        await plugin.DeactivateAsync();
        Assert.False(plugin.IsConfigured);
    }

    [Theory]
    [InlineData("tiny.en", "de", "en")]
    [InlineData("base.en", null, "en")]
    [InlineData("medium.en", "auto", "en")]
    [InlineData("large-v3-turbo", " DE ", "de")]
    [InlineData("tiny", null, "auto")]
    public void DecodeLanguageRespectsTheSelectedWeights(string model, string? language, string expected) =>
        Assert.Equal(expected, WhisperCppPlugin.ResolveDecodeLanguage(model, language));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadingDoesNotPublishSelectionAndCancelledNativeLoadIsReleased(bool cancel)
    {
        using var temp = new TempDirectory();
        using var cts = new CancellationTokenSource();
        var host = new FakePluginHostServices(temp.Path);
        host.SetSetting("selectedModel", "tiny");
        var released = 0;
        using var plugin = new WhisperCppPlugin
        {
            CreateFactory = _ =>
            {
                if (cancel) cts.Cancel();
                return (WhisperFactory)RuntimeHelpers.GetUninitializedObject(typeof(WhisperFactory));
            },
            ReleaseFactory = _ => released++
        };
        await plugin.ActivateAsync(host);
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        Directory.CreateDirectory(Path.Join(temp.Path, "Models"));
        CreateModelFixture(Path.Join(temp.Path, "Models", "ggml-base.bin"));
        // A load must never persist the new selection; the host commits it after cancellation checks.
        host.FailSetting = true;
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.LoadModelAsync("base", cts.Token));
            Assert.Null(GetPrivateField<WhisperFactory>(plugin, "_factory"));
            Assert.Null(GetPrivateField<string>(plugin, "_loadedModelId"));
            Assert.Equal(1, released);
        }
        else
        {
            await plugin.LoadModelAsync("base", cts.Token);
            Assert.Equal("base", GetPrivateField<string>(plugin, "_loadedModelId"));
            Assert.Equal(0, released);
        }
        Assert.Equal("tiny", plugin.SelectedModelId);
        Assert.Equal("tiny", host.GetSetting<string>("selectedModel"));
    }

    [Theory]
    [InlineData(" Hello", " world.", "Hello world.")]
    [InlineData("你好", "世界。", "你好世界。")]
    [InlineData("こんにちは", "世界。", "こんにちは世界。")]
    public async Task DecodePreservesSegmentSpacingAndTiming(string first, string second, string expected)
    {
        var result = await WhisperCppPlugin.CollectResultAsync(Segments(
            Segment(first, 0, 1.25, 0.1f), Segment(second, 1.25, 2.5, 0.2f)), default);
        Assert.Equal(expected, result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(2.5, result.DurationSeconds);
        Assert.Equal(new PluginTranscriptionSegment(first.Trim(), 0, 1.25), result.Segments[0]);
        Assert.Equal(new PluginTranscriptionSegment(second.Trim(), 1.25, 2.5), result.Segments[1]);
    }

    [Fact]
    public async Task SilentTailDoesNotOverrideSpeechAndInvalidProbabilitiesAreIgnored()
    {
        var result = await WhisperCppPlugin.CollectResultAsync(Segments(
            Segment(" Speech.", 0, 1, 0.02f), Segment("", 1, 2, 0.99f),
            Segment("", 2, 3, float.NaN), Segment("", 3, 4, -1), Segment("", 4, 5, 2)), default);
        Assert.Equal(0.02f, result.NoSpeechProbability);
        Assert.Single(result.Segments);
        Assert.Equal(5, result.DurationSeconds);
        var unknown = await WhisperCppPlugin.CollectResultAsync(Segments(Segment("Text", 0, 1, float.NaN)), default);
        Assert.Null(unknown.NoSpeechProbability);
    }

    [Fact]
    public async Task UnloadPreservesSelectionAndRemovalClearsItPersistently()
    {
        using var temp = new TempDirectory();
        var host = new FakePluginHostServices(temp.Path);
        using var plugin = new WhisperCppPlugin();
        await plugin.ActivateAsync(host);
        Directory.CreateDirectory(Path.Join(temp.Path, "Models"));
        CreateModelFixture(Path.Join(temp.Path, "Models", "ggml-tiny.bin"));
        plugin.SelectModel("tiny");
        await plugin.UnloadModelAsync();
        Assert.Equal("tiny", plugin.SelectedModelId);
        Assert.True(plugin.IsConfigured);
        await plugin.RemoveModelAsync("tiny", default);
        Assert.Null(plugin.SelectedModelId);
        Assert.Null(host.GetSetting<string>("selectedModel"));
        await plugin.DeactivateAsync();
        await plugin.ActivateAsync(host);
        Assert.Null(plugin.SelectedModelId);
    }

    [Fact]
    public async Task FailedSelectionPersistenceKeepsPreviousModel()
    {
        using var temp = new TempDirectory();
        var host = new FakePluginHostServices(temp.Path);
        using var plugin = new WhisperCppPlugin();
        await plugin.ActivateAsync(host);
        plugin.SelectModel("tiny");
        host.FailSetting = true;
        Assert.Throws<IOException>(() => plugin.SelectModel("base"));
        Assert.Equal("tiny", plugin.SelectedModelId);
        Assert.Equal("tiny", host.GetSetting<string>("selectedModel"));
    }

    [Theory]
    [InlineData("tiny.en", false)]
    [InlineData("small.en", false)]
    [InlineData("large-v3-turbo", false)]
    [InlineData("large-v3-turbo-q5_0", false)]
    [InlineData("tiny", true)]
    [InlineData("medium", true)]
    [InlineData("medium-q5_0", true)]
    public void TranslationRequiresTranslationTrainedWeights(string model, bool supported)
    {
        using var plugin = new WhisperCppPlugin();
        plugin.SelectModel(model);
        Assert.Equal(supported, plugin.SupportsTranslation);
    }

    [Theory]
    [InlineData("tiny.en")]
    [InlineData("large-v3-turbo")]
    [InlineData("large-v3-turbo-q5_0")]
    public async Task UnsupportedTranslationFailsBeforeLoadingModel(string model)
    {
        using var plugin = new WhisperCppPlugin();
        plugin.SelectModel(model);
        var pcmError = await Assert.ThrowsAsync<NotSupportedException>(() =>
            plugin.TranscribePcmAsync(new float[160], "de", true, default));
        Assert.Contains("cannot translate to English", pcmError.Message);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            plugin.TranscribeAsync([], "de", true, null, default));
    }

    [Fact]
    public async Task CudaInstallerUsesPersistentAssetsAndOwnsTheDownloadTimeout()
    {
        using var temp = new TempDirectory();
        using var plugin = new WhisperCppPlugin();
        await plugin.ActivateAsync(new FakePluginHostServices(temp.Path));
        var installer = GetPrivateField<IWhisperCppCudaRuntimeInstaller>(plugin, "_cudaRuntimeInstaller");
        Assert.Equal(Path.Join(temp.Path, "runtimes", "cuda", "win-x64"), installer!.RuntimeDirectory);
        Assert.Equal(Timeout.InfiniteTimeSpan, GetPrivateField<System.Net.Http.HttpClient>(plugin, "_httpClient")!.Timeout);
    }

    [Fact]
    public void CudaStagingReusesPersistentDownloadsWithoutMutatingPackages()
    {
        using var temp = new TempDirectory();
        var package = Path.Join(temp.Path, "package");
        var runtime = Path.Join(package, "runtimes", "cuda", "win-x64");
        var assets = Path.Join(temp.Path, "assets");
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Join(runtime, "whisper.dll"), "native");
        File.WriteAllText(Path.Join(assets, "cublas64_13.dll"), "downloaded");
        foreach (var version in new[] { "first", "updated" })
        {
            var cache = Path.Join(temp.Path, "cache", version);
            WhisperCppPlugin.StageCudaRuntime(package, assets, cache);
            WhisperCppPlugin.StageCudaRuntime(package, assets, cache);
            var staged = Path.Join(cache, "runtimes", "cuda", "win-x64");
            Assert.Equal("native", File.ReadAllText(Path.Join(staged, "whisper.dll")));
            Assert.Equal("downloaded", File.ReadAllText(Path.Join(staged, "cublas64_13.dll")));
            Assert.Empty(Directory.GetFiles(staged, "*.tmp"));
        }
        Assert.Single(Directory.GetFiles(runtime));
        Assert.Equal("downloaded", File.ReadAllText(Path.Join(assets, "cublas64_13.dll")));
    }

    private static SegmentData Segment(string text, double start, double end, float noSpeech) =>
        new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), 0, 1, 1, noSpeech, "en", []);

    private static async IAsyncEnumerable<SegmentData> Segments(params SegmentData[] segments)
    {
        foreach (var segment in segments) yield return segment;
        await Task.CompletedTask;
    }
}
