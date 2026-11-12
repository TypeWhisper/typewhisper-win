using System.Net;
using System.Net.Http;
using TypeWhisper.Plugin.Reson8;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public partial class Reson8PluginTests
{
    [Theory]
    [InlineData("https://reson8.example.test", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("wss://reson8.example.test", true)]
    [InlineData("ws://localhost:8080", true)]
    [InlineData("http://192.168.1.20:8080", false)]
    [InlineData("http://reson8.lan", false)]
    [InlineData("ws://reson8.lan", false)]
    public void ApiKeyTravelsOnlyOverTlsOrToThisComputer(string url, bool secure)
    {
        Assert.Equal(secure, Reson8Plugin.IsSecureServer(new Uri(url)));
    }

    [Fact]
    public async Task HttpServerOnAnotherComputerIsRejectedAndKeepsTheSavedUrl()
    {
        var host = new TestPluginHostServices();
        var sut = new Reson8Plugin();
        await sut.ActivateAsync(host);
        sut.SetCustomBaseUrl("https://proxy.example.test");

        var error = Assert.Throws<ArgumentException>(() => sut.SetCustomBaseUrl("http://192.168.1.20:8080"));

        Assert.Contains("HTTPS", error.Message);
        Assert.Equal("https://proxy.example.test", sut.CustomBaseUrl);
        Assert.Equal("https://proxy.example.test", host.GetSetting<string>("customBaseURL"));
        sut.SetCustomBaseUrl("http://localhost:8080");
        Assert.Equal("http://localhost:8080", sut.CustomBaseUrl);
    }

    [Fact]
    public async Task SavedHttpServerOnAnotherComputerNeverReceivesTheKey()
    {
        var sent = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            Interlocked.Increment(ref sent);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "reson-key";
        // A URL saved by an earlier version, before HTTP to other computers was rejected.
        host.SetSetting("customBaseURL", "http://192.168.1.20:8080");
        using var sut = new Reson8Plugin(new HttpClient(handler));
        await sut.ActivateAsync(host);

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => sut.FetchModelsAsync());
        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
        await Assert.ThrowsAsync<PluginRequestException>(() => sut.FetchCustomModelsAsync());
        await Assert.ThrowsAsync<PluginRequestException>(() => sut.ValidateApiKeyAsync("reson-key"));
        await Assert.ThrowsAsync<PluginRequestException>(() => Reson8StreamingSession.ConnectAsync(
            "reson-key", "Authorization", new Uri("ws://192.168.1.20:8080/v1/speech-to-text/realtime"), null, default));
        Assert.Equal(0, Volatile.Read(ref sent));
        await sut.DeactivateAsync();
    }
}
