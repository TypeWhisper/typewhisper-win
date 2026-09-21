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
        var engine = (ITranscriptionEnginePlugin)plugin;
        Assert.Equal(100,engine.DictionaryTermsBudget.MaxTerms);
        Assert.Equal(4000,engine.DictionaryTermsBudget.MaxTotalChars);
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
    [Theory]
    [InlineData(12287)]
    [InlineData(12288)]
    [InlineData(12289)]
    [InlineData(36866)]
    public async Task TurboUploadStreamsExactBase64AcrossChunkBoundaries(int length)
    {
        var audio = new byte[length];
        new Random(42).NextBytes(audio);
        using var content = new TurboAudioContent(audio, "de", "Grüße, \"quoted\"");
        using var stream = new BoundedWriteStream();
        await content.CopyToAsync(stream);
        Assert.Equal(stream.Length,content.Headers.ContentLength);
        Assert.InRange(stream.LargestWrite,1,16384);
        using var document = JsonDocument.Parse(stream.ToArray());
        Assert.Equal(audio,document.RootElement.GetProperty("audio").GetBytesFromBase64());
        Assert.Equal("Grüße, \"quoted\"",document.RootElement.GetProperty("initial_prompt").GetString());
        Assert.Equal("de",document.RootElement.GetProperty("language").GetString());
        Assert.Equal("transcribe",document.RootElement.GetProperty("task").GetString());
    }

    [Fact]
    public async Task TurboUploadHonorsCancellation()
    {
        using var content = new TurboAudioContent(new byte[50000],null,null);
        using var cancellation = new CancellationTokenSource();
        using var stream = new BoundedWriteStream { AfterWrite = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => content.CopyToAsync(stream,cancellation.Token));
        Assert.True(stream.Length < content.Headers.ContentLength);
    }

    private sealed class BoundedWriteStream : MemoryStream
    {
        internal int LargestWrite { get; private set; }
        internal Action? AfterWrite { get; init; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LargestWrite = Math.Max(LargestWrite,buffer.Length);
            await base.WriteAsync(buffer,cancellationToken);
            AfterWrite?.Invoke();
        }
    }

}
