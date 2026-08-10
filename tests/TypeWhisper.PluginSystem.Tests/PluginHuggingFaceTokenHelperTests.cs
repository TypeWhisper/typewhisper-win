using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class PluginHuggingFaceTokenHelperTests
{
    [Fact]
    public async Task SaveLoadAndClearTokenAsync_UsesPluginScopedSecretStorage()
    {
        var host = new TestPluginHostServices();

        var saved = await PluginHuggingFaceTokenHelper.SaveTokenAsync(host, "  hf_test  ");

        Assert.Equal("hf_test", saved);
        Assert.Equal("hf_test", await PluginHuggingFaceTokenHelper.LoadTokenAsync(host));
        Assert.Equal("hf_test", host.Secrets[PluginHuggingFaceTokenHelper.StorageKey]);

        await PluginHuggingFaceTokenHelper.ClearTokenAsync(host);

        Assert.DoesNotContain(PluginHuggingFaceTokenHelper.StorageKey, host.Secrets);
    }

    [Fact]
    public void NormalizeToken_RejectsEmbeddedWhitespace()
    {
        Assert.Throws<ArgumentException>(() =>
            PluginHuggingFaceTokenHelper.NormalizeToken("hf_bad token"));
    }

    [Fact]
    public async Task ValidateTokenAsync_UsesHuggingFaceIdentityEndpointAndBearerToken()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"name\":\"typewhisper\"}", Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler);

        var isValid = await PluginHuggingFaceTokenHelper.ValidateTokenAsync(
            "  hf_valid  ",
            client,
            CancellationToken.None);

        Assert.True(isValid);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://huggingface.co/api/whoami-v2", capturedRequest.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("hf_valid", capturedRequest.Headers.Authorization?.Parameter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{\"error\":\"invalid token\"}")]
    [InlineData(HttpStatusCode.OK, "not-json")]
    [InlineData(HttpStatusCode.OK, "[]")]
    public async Task ValidateTokenAsync_RejectsInvalidResponses(HttpStatusCode status, string body)
    {
        using var client = new HttpClient(new CapturingHandler(_ =>
            new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            }));

        Assert.False(await PluginHuggingFaceTokenHelper.ValidateTokenAsync(
            "hf_invalid",
            client,
            CancellationToken.None));
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        public Dictionary<string, string> Secrets { get; } = [];
        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();

        public Task StoreSecretAsync(string key, string value)
        {
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) => default;
        public void SetSetting<T>(string key, T value) { }
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
