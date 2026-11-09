using TypeWhisper.PluginSDK;
using TypeWhisper.Plugin.Reson8;
namespace PortableMigration.Tests;
public sealed class PortableKeyTests
{
    [Fact]
    public async Task FailedSecretMutation_KeepsPreviouslyConfiguredKey()
    {
        using var fixture=new PortableFixture(); using var plugin=new Reson8Plugin(new HttpClient(new NoNetwork()));
        await plugin.ActivateAsync(fixture.Host); IApiKeyPlugin key=plugin;
        await key.SetApiKeyAsync("previous"); fixture.Secrets.FailWrites=true;
        await Assert.ThrowsAsync<IOException>(() => key.SetApiKeyAsync("replacement"));
        Assert.True(key.IsConfigured); Assert.Contains("previous",fixture.Secrets.Values.Values);
        await Assert.ThrowsAsync<IOException>(() => key.SetApiKeyAsync("")); Assert.True(key.IsConfigured);
        fixture.Secrets.FailWrites=false; await key.SetApiKeyAsync(""); Assert.False(key.IsConfigured);
        await plugin.DeactivateAsync();
    }

    // The internal constructor leaves the background refresh off; any request fails the test.
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected request to {request.RequestUri}.");
    }
}
