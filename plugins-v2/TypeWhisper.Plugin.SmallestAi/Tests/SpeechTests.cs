using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using TypeWhisper.Plugin.SmallestAi;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace PortableMigration.Tests;

public sealed class SpeechTests
{
    private const string Catalog = """{"voices":[{"voiceId":"ben","displayName":"Ben","tags":{"language":["english","german"],"recommendedLanguages":["german"]}}]}""";

    [Fact]
    public void Catalog_UsesRecommendedLanguageAndIgnoresInvalidEntries()
    {
        var voice = Assert.Single(SmallestAiPlugin.ParseVoices(Catalog, "lightning_v3.1_pro"));
        Assert.Equal("de", voice.Language);
        Assert.Empty(SmallestAiPlugin.ParseVoices("""{"voices":[null,{}, {"voiceId":"x"}]}""", "lightning_v3.1"));
        Assert.Throws<InvalidDataException>(() => SmallestAiPlugin.ParseVoices("{}", "lightning_v3.1"));
    }

    [Fact]
    public async Task Catalog_RefreshPersistsBothPoolsAndRestoresWithoutNetwork()
    {
        using var fixture = new PortableFixture();
        using var client = new HttpClient(new Transport((r, _) =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("Bearer test", r.Headers.Authorization?.ToString());
            Assert.EndsWith("/get_voices", r.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(Catalog));
        }));
        using var plugin = new SmallestAiPlugin(client);
        await plugin.ActivateAsync(fixture.Host);
        await ((IApiKeyPlugin)plugin).SetApiKeyAsync("test");
        await plugin.ValidateConfigurationAsync(default);
        Assert.Equal(2, plugin.AvailableVoices.Count);
        plugin.SelectVoice("lightning_v3.1_pro:ben");
        await plugin.SaveTextSettingAsync("speech-speed", "1.25", default);
        await plugin.DeactivateAsync();
        Assert.False(plugin.IsConfigured);
        using var restored = new SmallestAiPlugin();
        await restored.ActivateAsync(fixture.Host);
        Assert.Equal("lightning_v3.1_pro:ben", restored.SelectedVoiceId);
        Assert.Equal(2, restored.AvailableVoices.Count);
        Assert.Contains("1.25", restored.SettingsSummary);
    }

    [Fact]
    public async Task FailedRefresh_RetainsCachedVoices()
    {
        using var fixture = new PortableFixture();
        using var client = new HttpClient(new Transport((_, _) => Task.FromResult(Json("{}", HttpStatusCode.Unauthorized))));
        using var plugin = new SmallestAiPlugin(client);
        await plugin.ActivateAsync(fixture.Host);
        await ((IApiKeyPlugin)plugin).SetApiKeyAsync("test");
        var previous = plugin.AvailableVoices;
        await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ValidateConfigurationAsync(default));
        Assert.Equal(previous, plugin.AvailableVoices);
    }

    [Fact]
    public async Task Speech_UsesSelectedModelLanguageSpeedAndOutputDevice()
    {
        using var fixture = new PortableFixture();
        var wav = CreateWav(1);
        using var client = new HttpClient(new Transport(async (r, ct) =>
        {
            if (r.Method == HttpMethod.Get) return Json(Catalog);
            Assert.Equal("https://api.smallest.ai/waves/v1/tts", r.RequestUri!.ToString());
            Assert.Equal("audio/wav", Assert.Single(r.Headers.Accept).MediaType);
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            var body = json.RootElement;
            Assert.Equal("lightning_v3.1_pro", body.GetProperty("model").GetString());
            Assert.Equal("ben", body.GetProperty("voice_id").GetString());
            Assert.Equal("de", body.GetProperty("language").GetString());
            Assert.Equal(1.25, body.GetProperty("speed").GetDouble());
            Assert.Equal("wav", body.GetProperty("output_format").GetString());
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) };
        }));
        using var plugin = new SmallestAiPlugin(client);
        await plugin.ActivateAsync(fixture.Host);
        await ((IApiKeyPlugin)plugin).SetApiKeyAsync("test");
        await plugin.RefreshVoicesAsync(default);
        await plugin.SaveTextSettingAsync("speech-speed", "1.25", default);
        var playback = Mock.Of<ITtsPlaybackSession>();
        plugin.PlaybackFactory = (audio, device) => { Assert.Equal(wav, audio); Assert.Equal("speakers", device); return playback; };
        var previous = plugin.SelectedVoiceId;
        Assert.Same(playback, await plugin.SpeakAsync(new("Hallo", "de-DE") { VoiceId = "lightning_v3.1_pro:ben", OutputDeviceId = "speakers" }, default));
        Assert.Equal(previous, plugin.SelectedVoiceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("0.4")]
    [InlineData("2.1")]
    public async Task InvalidSpeed_DoesNotChangeSettings(string value)
    {
        using var fixture = new PortableFixture();
        using var plugin = new SmallestAiPlugin();
        await plugin.ActivateAsync(fixture.Host);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("speech-speed", value, default));
        Assert.Equal("1", Assert.Single(plugin.TextSettings).Value);
    }

    [Fact]
    public async Task Speech_RejectsErrorBodyAndCancellationWithoutPlayback()
    {
        using var fixture = new PortableFixture();
        using var client = new HttpClient(new Transport((_, _) => Task.FromResult(Json("{\"error\":\"oops\"}"))));
        using var plugin = new SmallestAiPlugin(client);
        await plugin.ActivateAsync(fixture.Host);
        await ((IApiKeyPlugin)plugin).SetApiKeyAsync("test");
        plugin.PlaybackFactory = (_, _) => throw new Exception("Playback must not start.");
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.SpeakAsync(new("Hello"), default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.SpeakAsync(new("Hello"), new(true)));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SpeakAsync(new(new string('x', 8001)), default));
    }

    [Fact]
    public void SpeechAudio_EnforcesSizeAndDurationBeforePlayback()
    {
        SmallestAiPlugin.ValidateSpeechAudio(CreateWav(120));
        Assert.Throws<InvalidDataException>(() => SmallestAiPlugin.ValidateSpeechAudio(CreateWav(121)));
        Assert.Throws<InvalidDataException>(() => SmallestAiPlugin.ValidateSpeechAudio(new byte[12 * 1024 * 1024 + 1]));
        Assert.Throws<InvalidDataException>(() => SmallestAiPlugin.ValidateSpeechAudio(CreateWav(0)));
    }

    private static byte[] CreateWav(int seconds)
    {
        using var memory = new MemoryStream();
        using (var writer = new NAudio.Wave.WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(memory), new NAudio.Wave.WaveFormat(8000, 16, 1)))
            writer.Write(new byte[seconds * 16000], 0, seconds * 16000);
        return memory.ToArray();
    }

    private static HttpResponseMessage Json(string value, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
