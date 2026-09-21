using System.Text.Json;
using TypeWhisper.Plugin.CloudflareAsr;
using TypeWhisper.PluginSDK;

public sealed partial class ProviderTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("auto")]
    public async Task TurboSendsAudioAndLanguageWithoutTranslation(string language)
    {
        using var http = new HttpClient(new Handler((request, body) =>
        {
            Assert.EndsWith("/ai/run/@cf/openai/whisper-large-v3-turbo", request.RequestUri!.AbsolutePath);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var payload = JsonDocument.Parse(body!);
            var root = payload.RootElement;
            Assert.Equal(Audio(), Convert.FromBase64String(root.GetProperty("audio").GetString()!));
            Assert.Equal("transcribe", root.GetProperty("task").GetString());
            if (language == "auto") Assert.False(root.TryGetProperty("language", out _));
            else Assert.Equal(language, root.GetProperty("language").GetString());
            Assert.Equal("TypeWhisper, Marco", root.GetProperty("initial_prompt").GetString());
            return Json("""{"success":true,"result":{"text":" Hallo Welt ","transcription_info":{"language":"de","duration":1.25}}}""");
        }));
        using var plugin = new CloudflareAsrPlugin(http);
        await plugin.ActivateAsync(new Host()); await Configure(plugin);
        plugin.SelectModel("whisper-large-v3-turbo");
        var result = await plugin.TranscribeAsync(Audio(), language, false, "TypeWhisper, Marco", default);
        Assert.Equal("Hallo Welt", result.Text); Assert.Equal("de", result.DetectedLanguage); Assert.Equal(1.25, result.DurationSeconds);
    }

    [Fact]
    public async Task ModelCapabilitiesAndSelectionSurviveRestart()
    {
        var host = new Host();
        using var plugin = new CloudflareAsrPlugin();
        await plugin.ActivateAsync(host); await Configure(plugin);
        Assert.Empty(plugin.SupportedLanguages); Assert.False(plugin.SupportsDictionaryTerms);
        plugin.SelectModel("whisper-large-v3-turbo");
        Assert.Contains("de", plugin.SupportedLanguages); Assert.True(plugin.SupportsDictionaryTerms);
        Assert.Equal(74_000_000, plugin.MaximumAudioUploadBytes);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal("whisper-large-v3-turbo", plugin.SelectedModelId);
        plugin.SelectModel("whisper"); Assert.Empty(plugin.SupportedLanguages); Assert.Equal(100_000_000,plugin.MaximumAudioUploadBytes);
    }

    [Fact]
    public async Task UnsupportedLanguageAndTranslationDoNotSendTurboRequests()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_,_) => { calls++; return Json("{}"); }));
        using var plugin = new CloudflareAsrPlugin(http);
        await plugin.ActivateAsync(new Host()); await Configure(plugin); plugin.SelectModel("whisper-large-v3-turbo");
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.TranscribeAsync(Audio(), "invalid", false, null, default));
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.TranscribeAsync(Audio(), "de", true, null, default));
        Assert.Equal(0,calls);
    }
}
