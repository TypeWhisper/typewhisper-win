using System.Net;
using System.Text;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Cloud;

namespace TypeWhisper.Core.Tests.Services;

public class CloudProviderTests
{
    private sealed class TestProvider : CloudProviderBase
    {
        public override string Id => "test";
        public override string DisplayName => "Test Provider";
        public override string BaseUrl { get; }
        public override string? TranslationModel => "test-llm";
        public override IReadOnlyList<CloudModelInfo> TranscriptionModels { get; } =
        [
            new() { Id = "whisper-test", DisplayName = "Whisper Test", ApiModelName = "whisper-test" }
        ];

        public TestProvider(string baseUrl = "https://api.test.com/openai") : base()
        {
            BaseUrl = baseUrl;
        }

        public TestProvider(HttpClient httpClient, string baseUrl = "https://api.test.com/openai")
            : base(httpClient)
        {
            BaseUrl = baseUrl;
        }
    }

    private static TestProvider CreateProvider(HttpMessageHandler handler, string baseUrl = "https://api.test.com/openai")
    {
        var provider = new TestProvider(new HttpClient(handler), baseUrl);
        provider.Configure("test-key");
        provider.SelectTranscriptionModel("whisper-test");
        return provider;
    }

    [Fact]
    public void IsModelLoaded_ReturnsFalse_WhenNotConfigured()
    {
        var provider = new TestProvider();
        Assert.False(provider.IsModelLoaded);
    }

    [Fact]
    public void IsModelLoaded_ReturnsTrue_WhenConfigured()
    {
        var provider = new TestProvider();
        provider.Configure("key");
        provider.SelectTranscriptionModel("whisper-test");
        Assert.True(provider.IsModelLoaded);
    }

    [Fact]
    public async Task TranscribeAsync_ThrowsWhenNotConfigured()
    {
        var provider = new TestProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.TranscribeAsync([0.1f, 0.2f]));
    }

    [Fact]
    public async Task TranscribeAsync_Success_ReturnsTranscription()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                    "text": "Hallo Welt",
                    "language": "de",
                    "duration": 2.5
                }
                """, Encoding.UTF8, "application/json")
        });

        var provider = CreateProvider(handler);
        var result = await provider.TranscribeAsync([0.1f, 0.2f, 0.3f]);

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(2.5, result.Duration);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://api.test.com/openai/v1/audio/transcriptions", handler.LastRequest.RequestUri?.ToString());
        Assert.Equal("Bearer test-key", handler.LastRequest.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task TranscribeAsync_Translation_UsesTranslationEndpoint()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"text": "Hello World", "language": "en"}""",
                Encoding.UTF8, "application/json")
        });

        var provider = CreateProvider(handler);
        var result = await provider.TranscribeAsync([0.1f], task: TranscriptionTask.Translate);

        Assert.Contains("/v1/audio/translations", handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal("Hello World", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_SendsWavFile()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"text": "test"}""", Encoding.UTF8, "application/json")
        });

        var provider = CreateProvider(handler);
        await provider.TranscribeAsync([0.5f, -0.5f]);

        Assert.NotNull(handler.LastRequest?.Content);
        Assert.IsType<MultipartFormDataContent>(handler.LastRequest!.Content);
        Assert.Contains("/v1/audio/transcriptions", handler.LastRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task TranscribeAsync_401_ThrowsWithApiKeyMessage()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });

        var provider = CreateProvider(handler);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.TranscribeAsync([0.1f]));
        Assert.Contains("API-Key", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_429_ThrowsWithRateLimitMessage()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });

        var provider = CreateProvider(handler);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.TranscribeAsync([0.1f]));
        Assert.Contains("Rate-Limit", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_500_ThrowsWithApiError()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"error": {"message": "Internal error"}}""",
                Encoding.UTF8, "application/json")
        });

        var provider = CreateProvider(handler);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.TranscribeAsync([0.1f]));
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_IncludesLanguage_WhenNotAuto()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"text": "Hallo"}""", Encoding.UTF8, "application/json")
        });

        var provider = CreateProvider(handler);
        await provider.TranscribeAsync([0.1f], language: "de");

        var content = handler.LastRequest?.Content as MultipartFormDataContent;
        Assert.NotNull(content);
    }

    [Fact]
    public void SupportsTranslation_ReturnsFalse_WhenNotConfigured()
    {
        var provider = new TestProvider();
        Assert.False(provider.SupportsTranslation);
    }

    [Fact]
    public void SupportsTranslation_ReturnsTrue_WhenConfigured()
    {
        var provider = new TestProvider();
        provider.Configure("key");
        Assert.True(provider.SupportsTranslation);
    }

    [Fact]
    public void SelectTranscriptionModel_ThrowsForUnknownModel()
    {
        var provider = new TestProvider();
        provider.Configure("key");
        Assert.Throws<ArgumentException>(() => provider.SelectTranscriptionModel("nonexistent"));
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public HttpRequestMessage? LastRequest { get; private set; }

        public MockHttpHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_response);
        }
    }
}
