using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class OpenAiApiHelperTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, PluginRequestFailureKind.Timeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, PluginRequestFailureKind.RateLimit, true)]
    [InlineData(HttpStatusCode.InternalServerError, PluginRequestFailureKind.ServerError, true)]
    [InlineData(HttpStatusCode.Unauthorized, PluginRequestFailureKind.Authentication, false)]
    [InlineData(HttpStatusCode.Forbidden, PluginRequestFailureKind.Permission, false)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, PluginRequestFailureKind.RequestTooLarge, false)]
    [InlineData(HttpStatusCode.BadRequest, PluginRequestFailureKind.InvalidRequest, false)]
    public async Task SendWithErrorHandlingAsync_ClassifiesHttpFailures(
        HttpStatusCode statusCode,
        PluginRequestFailureKind expectedKind,
        bool expectedTransient)
    {
        using var client = CreateClient(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{\"error\":{\"message\":\"provider failure\"}}")
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/request");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(client, request, CancellationToken.None));

        Assert.Equal(expectedKind, error.FailureKind);
        Assert.Equal((int)statusCode, error.HttpStatusCode);
        Assert.Equal(expectedTransient, error.IsTransient);
    }

    [Fact]
    public async Task SendWithErrorHandlingAsync_PreservesRetryAfter()
    {
        using var client = CreateClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limited")
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
            return response;
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/request");

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(client, request, CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(12), error.RetryAfter);
    }

    [Fact]
    public async Task SendChatCompletionAsync_ClassifiesEmptyResponseAsTransient()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        });

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiChatHelper.SendChatCompletionAsync(
                client,
                "https://provider.example",
                "test-key",
                "test-model",
                "system",
                "input",
                CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.EmptyResponse, error.FailureKind);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task SendChatCompletionAsync_ClassifiesBlankAssistantContentAsTransient()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"content":"   "}}]}""")
        });

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiChatHelper.SendChatCompletionAsync(
                client,
                "https://provider.example",
                "test-key",
                "test-model",
                "system",
                "input",
                CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.EmptyResponse, error.FailureKind);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task SendWithErrorHandlingAsync_ClassifiesNetworkAndTimeoutWithoutSwallowingUserCancellation()
    {
        using var networkClient = new HttpClient(new ThrowingHandler(
            _ => new HttpRequestException("offline")));
        using var networkRequest = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/network");
        var network = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(
                networkClient,
                networkRequest,
                CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.Network, network.FailureKind);
        Assert.True(network.IsTransient);

        using var timeoutClient = new HttpClient(new ThrowingHandler(
            _ => new TaskCanceledException("timeout")));
        using var timeoutRequest = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/timeout");
        var timeout = await Assert.ThrowsAsync<PluginRequestException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(
                timeoutClient,
                timeoutRequest,
                CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.Timeout, timeout.FailureKind);
        Assert.True(timeout.IsTransient);

        using var cancelledClient = new HttpClient(new ThrowingHandler(
            token => new OperationCanceledException(token)));
        using var cancelledRequest = new HttpRequestMessage(HttpMethod.Get, "https://provider.example/cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OpenAiApiHelper.SendWithErrorHandlingAsync(
                cancelledClient,
                cancelledRequest,
                cancellation.Token));
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHandler(responder));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class ThrowingHandler(Func<CancellationToken, Exception> exceptionFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exceptionFactory(cancellationToken));
    }
}
