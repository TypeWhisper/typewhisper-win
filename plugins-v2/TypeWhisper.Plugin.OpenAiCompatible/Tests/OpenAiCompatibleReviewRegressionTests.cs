using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.OpenAiCompatible.Portable.Tests;

public partial class OpenAiCompatiblePluginTests
{
    [Fact]
    public void LanguageHintsNormalizeBeforeFilteringTheAutomaticSentinel()
    {
        Assert.Equal(["de", "en"], OpenAiCompatiblePlugin.NormalizeHints([" auto ", "AUTO", " de ", "DE", "", "  ", "en", null!]));
    }

    [Theory]
    [InlineData("{\"text\":42}")]
    [InlineData("[]")]
    [InlineData("{invalid")]
    public void InvalidTranscriptionTextHasAStructuredFailure(string json)
    {
        var error = Assert.Throws<PluginRequestException>(() => CompatibleTranscriptionHelper.ParseTranscriptionResponse(json));
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, error.FailureKind);
    }

    [Fact]
    public void MalformedOptionalTranscriptionMetadataDoesNotDiscardText()
    {
        var result = CompatibleTranscriptionHelper.ParseTranscriptionResponse("""
            {"text":" Hello ","language":[],"duration":"unknown","segments":[null,5,
              {"text":false,"start":{},"end":null,"no_speech_prob":"no"},
              {"text":"Hello","start":1,"end":2,"no_speech_prob":0.2}]}
            """);
        Assert.Equal("Hello", result.Text);
        Assert.Null(result.DetectedLanguage);
        Assert.Equal(0, result.DurationSeconds);
        Assert.Equal(2, result.Segments.Count);
    }

    [Fact]
    public async Task CatalogKeepsValidEntriesAndDraftUsesTheSameParser()
    {
        using var plugin = new OpenAiCompatiblePlugin(new HttpClient(new CapturingHandler((_, _) =>
            JsonResponse("""{"data":[null,5,{"id":3},{"id":" "},{"id":"valid","owned_by":42},{"id":"VALID"},{"id":"second","owned_by":"owner"}]}"""))));
        await plugin.ActivateAsync(new TestPluginHostServices());
        plugin.SetBaseUrl("http://localhost:1234");
        Assert.Equal(["second", "valid"], (await plugin.FetchModelsAsync()).Select(m => m.Id));
        await plugin.ExecuteProfileActionAsync(Default, Default + "/refresh", new Dictionary<string, string>(), null, default);
        Assert.Equal(["second", "valid"], plugin.TextSettings.Single(f => f.Id == Default + "/text").Suggestions);
    }

    [Fact]
    public async Task DiscoveryPreservesConfigurationFailures()
    {
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(new TestPluginHostServices());
        plugin.SetBaseUrl("file:///invalid");
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.FetchModelsAsync());
        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
    }

    [Fact]
    public async Task ModelSelectionCanonicalizesProfileIds()
    {
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(new TestPluginHostServices());
        plugin.SelectModelForProfile("  OPENAI-COMPATIBLE  ", "speech-model");
        Assert.Equal("speech-model", plugin.Profiles[0].SelectedModelId);
        Assert.Throws<ArgumentException>(() => plugin.SelectModelForProfile("missing", "speech-model"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileDeletionRetriesOnlyUnreferencedSecretsAfterRestart(bool failProfileWrite)
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(host);
        var id = plugin.AddProfile("Fixture").Id;
        await plugin.SetApiKeyAsync("fixture-key", id);
        var secretName = Assert.Single(host.Secrets).Key;
        host.FailSecretDeletes = true;
        host.FailProfileWrites = failProfileWrite;
        if (failProfileWrite) await Assert.ThrowsAsync<IOException>(() => plugin.DeleteProfileAsync(id));
        else Assert.True(await plugin.DeleteProfileAsync(id));
        Assert.Contains(secretName, host.GetSetting<List<string>>("pendingSecretDeletions")!);
        host.FailSecretDeletes = host.FailProfileWrites = false;
        using var restarted = new OpenAiCompatiblePlugin();
        await restarted.ActivateAsync(host);
        Assert.Equal(failProfileWrite, host.Secrets.ContainsKey(secretName));
        Assert.Equal(failProfileWrite, restarted.Profiles.Any(p => p.Id == id));
        Assert.Empty(host.GetSetting<List<string>>("pendingSecretDeletions")!);
        if (failProfileWrite) Assert.Equal("fixture-key", restarted.GetApiKey(id));
    }

    [Theory]
    [InlineData("speech-deployment", "whisper", false)]
    [InlineData("contains-whisper", "live", true)]
    public async Task ExplicitRealtimeProtocolSurvivesRestartAndControlsPayload(string model, string protocol, bool live)
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(host);
        await plugin.SaveProfileSettingsAsync(Default, new Dictionary<string, string>
        { [Default + "/transcription"] = model, [Default + "/transport"] = "realtime", [Default + "/realtime-protocol"] = protocol }, null, default);
        using var restarted = new OpenAiCompatiblePlugin();
        await restarted.ActivateAsync(host);
        var profile = restarted.Profiles[0];
        Assert.Equal(protocol, profile.RealtimeProtocol);
        using var json = JsonDocument.Parse(CompatibleRealtimeStreamingSession.CreateSessionUpdatePayload(model, ["de", "en"], "Names", protocol: profile.RealtimeProtocol));
        var transcription = json.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("transcription");
        Assert.Equal(live, transcription.TryGetProperty("languages", out _));
        Assert.Equal(!live, transcription.TryGetProperty("language", out _));
        Assert.Equal(live, transcription.TryGetProperty("prompt", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementKeyCleanupRetriesWithoutDeletingTheCommittedKey(bool failProfileWrite)
    {
        var host = new TestPluginHostServices();
        using var plugin = new OpenAiCompatiblePlugin();
        await plugin.ActivateAsync(host);
        await plugin.SaveProfileSettingsAsync(Default, new Dictionary<string, string>(), "original-key", default);
        host.FailSecretDeletes = true;
        host.FailProfileWrites = failProfileWrite;
        var save = plugin.SaveProfileSettingsAsync(Default, new Dictionary<string, string>(), "replacement-key", default);
        if (failProfileWrite) await Assert.ThrowsAsync<IOException>(() => save);
        else await save;
        Assert.Equal(2, host.Secrets.Count);
        Assert.Single(host.GetSetting<List<string>>("pendingSecretDeletions")!);
        host.FailSecretDeletes = host.FailProfileWrites = false;
        using var restarted = new OpenAiCompatiblePlugin();
        await restarted.ActivateAsync(host);
        var expected = failProfileWrite ? "original-key" : "replacement-key";
        Assert.Equal(expected, restarted.GetApiKey());
        Assert.Equal(expected, Assert.Single(host.Secrets).Value);
        Assert.Empty(host.GetSetting<List<string>>("pendingSecretDeletions")!);
    }

    [Fact]
    public void RealtimeEventsEmitOnlyTheCurrentItemAndIgnoreRepeatedFinals()
    {
        var collector = new CompatibleRealtimeTranscriptCollector();
        Assert.True(collector.ApplyEvent("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"one","transcript":"Hello"}""", out var first));
        Assert.Equal("Hello", first!.Text);
        Assert.True(collector.ApplyEvent("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"two","delta":"wor"}""", out var partial));
        Assert.Equal("wor", partial!.Text);
        const string second = """{"type":"conversation.item.input_audio_transcription.completed","item_id":"two","transcript":"world"}""";
        Assert.True(collector.ApplyEvent(second, out var final));
        Assert.Equal("world", final!.Text);
        Assert.False(collector.ApplyEvent(second, out _));
        Assert.False(collector.ApplyEvent("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"two","delta":"late"}""", out _));
        Assert.Equal("Hello world", collector.CurrentText);
    }
}
