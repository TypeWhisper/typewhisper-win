using System.Net;
using System.Text.Json;
using TypeWhisper.Plugin.Webhook;
namespace PortableMigration.Tests;

// Destination URLs often carry their own access token, so they live in encrypted storage, not in settings.json.
public sealed class UrlStorageTests
{
    private const string PrivateUrl = "https://hooks.example.test/services/T000/B000/private-token";

    private static string SettingsFile(PortableFixture f) => Path.Join(f.Host.PluginDataDirectory, "settings.json");

    [Fact]
    public async Task SavedUrlIsEncryptedAndRestoredAfterRestart()
    {
        using var f = new PortableFixture(); var transport = new Transport();
        using var p = new WebhookPlugin(new HttpClient(transport)); await p.ActivateAsync(f.Host);
        await p.ExecuteSettingsActionAsync("add", default); var id = p.ConnectionIdentity;
        await p.SaveProfileSettingsAsync(id, new Dictionary<string, string> { { id + ":url", PrivateUrl }, { id + ":enabled", "true" } }, null, default);

        Assert.DoesNotContain("private-token", File.ReadAllText(SettingsFile(f)));
        Assert.Equal(PrivateUrl, Assert.Single(f.Secrets.Values, s => s.Key.StartsWith("url-" + id + "-", StringComparison.Ordinal)).Value);
        await p.DeactivateAsync();

        using var restarted = new WebhookPlugin(new HttpClient(transport)); await restarted.ActivateAsync(f.Host);
        Assert.Equal(PrivateUrl, restarted.TextSettings.Single(x => x.Id == id + ":url").Value);
        Assert.Equal("hello", await restarted.ProcessAsync("hello", new(), default));
        Assert.Equal(PrivateUrl, transport.LastUrl);
        await restarted.DeactivateAsync();
    }

    [Fact]
    public async Task ChangingOrClearingTheUrlReplacesItsSecret()
    {
        using var f = new PortableFixture(); using var p = new WebhookPlugin(); await p.ActivateAsync(f.Host);
        await p.ExecuteSettingsActionAsync("add", default); var id = p.ConnectionIdentity;
        await p.SaveTextSettingAsync(id + ":url", PrivateUrl, default);
        await p.SaveTextSettingAsync(id + ":name", "Renamed", default);
        var first = Assert.Single(f.Secrets.Values, s => s.Key.StartsWith("url-", StringComparison.Ordinal)).Key;

        await p.SaveTextSettingAsync(id + ":url", "https://hooks.example.test/other", default);
        var second = Assert.Single(f.Secrets.Values, s => s.Key.StartsWith("url-", StringComparison.Ordinal));
        Assert.NotEqual(first, second.Key);
        Assert.Equal("https://hooks.example.test/other", second.Value);

        await p.SaveTextSettingAsync(id + ":url", "", default);
        Assert.DoesNotContain(f.Secrets.Values, s => s.Key.StartsWith("url-", StringComparison.Ordinal));
        await p.ExecuteSettingsActionAsync("remove:" + id, default);
        Assert.Empty(f.Secrets.Values);
    }

    [Fact]
    public async Task UrlsSavedByEarlierVersionsMoveToEncryptedStorage()
    {
        using var f = new PortableFixture(); var id = Guid.NewGuid().ToString("N");
        f.Host.SetSetting("endpoints.v2", new[] { new { Id = id, Name = "Chat", Url = PrivateUrl, Method = "POST", Enabled = true, Workflows = "" } });
        Assert.Contains("private-token", File.ReadAllText(SettingsFile(f)));

        using var p = new WebhookPlugin(); await p.ActivateAsync(f.Host);

        Assert.DoesNotContain("private-token", File.ReadAllText(SettingsFile(f)));
        Assert.Equal(PrivateUrl, Assert.Single(f.Secrets.Values).Value);
        Assert.Equal(PrivateUrl, p.TextSettings.Single(x => x.Id == id + ":url").Value);
        Assert.Equal("true", p.TextSettings.Single(x => x.Id == id + ":enabled").Value);
    }

    [Fact]
    public async Task WithoutEncryptedStorageTheSavedUrlStaysUntilTheNextStart()
    {
        using var f = new PortableFixture(); var id = Guid.NewGuid().ToString("N");
        f.Host.SetSetting("endpoints.v2", new[] { new { Id = id, Name = "Chat", Url = PrivateUrl, Method = "POST", Enabled = true, Workflows = "" } });
        var before = File.ReadAllText(SettingsFile(f));
        f.Secrets.FailWrites = true;

        using (var p = new WebhookPlugin())
        {
            await p.ActivateAsync(f.Host);
            Assert.Equal(before, File.ReadAllText(SettingsFile(f)));
            Assert.Equal(PrivateUrl, p.TextSettings.Single(x => x.Id == id + ":url").Value);
            await p.DeactivateAsync();
        }

        f.Secrets.FailWrites = false;
        using var retried = new WebhookPlugin(); await retried.ActivateAsync(f.Host);
        Assert.DoesNotContain("private-token", File.ReadAllText(SettingsFile(f)));
        Assert.Equal(PrivateUrl, retried.TextSettings.Single(x => x.Id == id + ":url").Value);
    }

    private sealed class Transport : HttpMessageHandler
    {
        internal string? LastUrl;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
