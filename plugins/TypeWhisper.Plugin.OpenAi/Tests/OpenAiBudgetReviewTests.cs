using System.Net;
using System.Text.Json;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenAiPluginTests
{
    [Theory]
    [InlineData("o1-mini", false)]
    [InlineData("o1-preview", false)]
    [InlineData("o1-pro", true)]
    [InlineData("o3-pro", true)]
    [InlineData("o3-pro-2025-06-10", true)]
    public async Task FixedReasoningModelsUseTheirEndpointAndReserve(string model, bool responses)
    {
        using var client = new HttpClient(new CapturingHandler((request, body) =>
        {
            Assert.EndsWith(responses ? "/v1/responses" : "/v1/chat/completions", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(body!);
            Assert.Equal(LlmOutputTokenBudget.CalculateWithReasoningReserve("", "Hello"),
                json.RootElement.GetProperty(responses ? "max_output_tokens" : "max_completion_tokens").GetInt32());
            Assert.False(json.RootElement.TryGetProperty(responses ? "reasoning" : "reasoning_effort", out _));
            return Task.FromResult(JsonResponse(responses
                ? """{"output":[{"content":[{"type":"output_text","text":"Done"}]}]}"""
                : """{"choices":[{"message":{"content":"Done"},"finish_reason":"stop"}]}"""));
        }));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "fixture-key";
        using var plugin = new OpenAiPlugin(client, _ => new FakeTtsPlaybackSession());
        await plugin.ActivateAsync(host);
        Assert.Equal("Done", await plugin.ProcessAsync("", "Hello", model, default));
    }

    [Fact]
    public async Task TwoMinutesOfMonoPcmAreAcceptedAndOneMoreSampleIsNot()
    {
        static string[] Seconds(int seconds, int extraBytes) =>
        [
            .. Enumerable.Range(0, seconds).Select(_ => SpeechSocketFixture.Audio(new byte[24_000 * 2])),
            .. extraBytes > 0 ? [SpeechSocketFixture.Audio(new byte[extraBytes])] : Array.Empty<string>(),
            SpeechSocketFixture.Done,
        ];
        var sockets = new Queue<SpeechSocketFixture>([new(Seconds(120, 0)), new(Seconds(120, 2))]);
        using var client = new HttpClient(new CapturingHandler((_, _) => Task.FromResult(JsonResponse("{}"))));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "fixture-key";
        using var plugin = new OpenAiPlugin(client, bytes =>
        {
            Assert.Equal(5_760_000, bytes.Length);
            return new FakeTtsPlaybackSession();
        }, ttsSocketFactory: (_, _) => Task.FromResult<System.Net.WebSockets.WebSocket>(sockets.Dequeue()));
        await plugin.ActivateAsync(host);
        var playback = await plugin.SpeakAsync(new("Long speech"), default);
        Assert.True(playback.IsActive);
        playback.Stop();
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.SpeakAsync(new("Long speech"), default));
    }
}
