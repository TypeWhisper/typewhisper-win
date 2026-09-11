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
    public async Task TwoMinutesOfMonoPcmFitTheHttpBuffer()
    {
        var pcm = new byte[24_000 * 2 * 120];
        using var client = new HttpClient(new CapturingHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pcm) })));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "fixture-key";
        using var plugin = new OpenAiPlugin(client, bytes =>
        {
            Assert.Equal(5_760_000, bytes.Length);
            return new FakeTtsPlaybackSession();
        });
        await plugin.ActivateAsync(host);
        var playback = await plugin.SpeakAsync(new("Long speech"), default);
        Assert.True(playback.IsActive);
        playback.Stop();
    }
}
