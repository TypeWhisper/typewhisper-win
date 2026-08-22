using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class OpenAiChatHelperTests
{
    [Fact]
    public async Task SendChatCompletionAsync_ScalesOutputBudgetForLongWorkflowInput()
    {
        string? capturedBody = null;
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {
                  "choices": [
                    { "message": { "content": "Complete result" }, "finish_reason": "stop" }
                  ]
                }
                """);
        }));
        var longTranscript = string.Join(' ', Enumerable.Repeat("dictated workflow input", 1_000));

        var result = await OpenAiChatHelper.SendChatCompletionAsync(
            httpClient,
            "https://provider.example",
            "test-key",
            "test-model",
            "Preserve every detail.",
            longTranscript,
            CancellationToken.None);

        Assert.Equal("Complete result", result);
        using var body = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.True(
            body.RootElement.GetProperty("max_tokens").GetInt32() > 2048,
            "Long workflow input must receive more than the fixed legacy output budget.");
    }

    [Fact]
    public async Task SendChatCompletionAsync_RejectsTokenLimitedPartialResponse()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => Task.FromResult(JsonResponse("""
            {
              "choices": [
                {
                  "message": { "content": "This result ends in the middle" },
                  "finish_reason": "length"
                }
              ]
            }
            """))));

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiChatHelper.SendChatCompletionAsync(
                httpClient,
                "https://provider.example",
                "test-key",
                "test-model",
                "Preserve every detail.",
                "Long dictated input",
                CancellationToken.None));

        Assert.Contains("token", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PluginRequestFailureKind.OutputTruncated, error.FailureKind);
        Assert.False(error.IsTransient);
    }

    [Theory]
    [InlineData(100, 2048)]
    [InlineData(8_000, 4096)]
    [InlineData(100_000, 8192)]
    public void OutputTokenBudget_ScalesAndRemainsBounded(int inputLength, int expectedTokens)
    {
        var budget = LlmOutputTokenBudget.Calculate("", new string('a', inputLength));

        Assert.Equal(expectedTokens, budget);
    }

    [Fact]
    public void SendChatCompletionAsync_PreservesLegacySevenParameterOverload()
    {
        var parameterTypes = new[]
        {
            typeof(HttpClient),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(CancellationToken)
        };

        var method = typeof(OpenAiChatHelper).GetMethod(
            nameof(OpenAiChatHelper.SendChatCompletionAsync),
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: parameterTypes,
            modifiers: null);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<string>), method!.ReturnType);
    }

    [Fact]
    public void SendChatCompletionAsync_PreservesLegacyElevenParameterOverload()
    {
        var parameterTypes = new[]
        {
            typeof(HttpClient),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(CancellationToken),
            typeof(int?),
            typeof(string),
            typeof(string),
            typeof(double?)
        };

        var method = typeof(OpenAiChatHelper).GetMethod(
            nameof(OpenAiChatHelper.SendChatCompletionAsync),
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: parameterTypes,
            modifiers: null);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<string>), method!.ReturnType);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request);
    }
}
