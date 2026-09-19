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
        await File.WriteAllBytesAsync(modelPath, [1]);
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
        await File.WriteAllBytesAsync(Path.Join(temp.Path, "Models", "ggml-base.bin"), [1]);
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

    private static SegmentData Segment(string text, double start, double end, float noSpeech) =>
        new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), 0, 1, 1, noSpeech, "en", []);

    private static async IAsyncEnumerable<SegmentData> Segments(params SegmentData[] segments)
    {
        foreach (var segment in segments) yield return segment;
        await Task.CompletedTask;
    }
}
