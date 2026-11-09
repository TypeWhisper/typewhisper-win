using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using TypeWhisper.Plugin.Fireworks;

public sealed partial class ProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private const string NewCatalog = """{"models":[{"name":"accounts/fireworks/models/new-chat"},{"name":"accounts/fireworks/models/kimi-k3"}]}""";
    private static readonly string[] NewCatalogModels =
        ["accounts/fireworks/models/gpt-oss-120b", "accounts/fireworks/models/kimi-k3", "accounts/fireworks/models/new-chat"];

    [Fact]
    public async Task ActivationRefreshesADayOldCatalogAndSavesItsDate()
    {
        var requests = new ConcurrentQueue<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Enqueue($"{request.Headers.Authorization} {request.RequestUri}");
            return Json(NewCatalog);
        }));
        var host = Seeded("first-key", "accounts/fireworks/models/retired-chat", Now - ModelCatalogAutoRefresh.MaximumAge);
        using var plugin = new FireworksPlugin(http, refreshCatalogAutomatically: true, new TestClock(Now));

        await plugin.ActivateAsync(host);
        await plugin.CatalogRefreshTask;

        Assert.Equal("Bearer first-key https://api.fireworks.ai/inference/v1/models?page_size=200", Assert.Single(requests));
        Assert.Equal(NewCatalogModels, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(Now, plugin.CatalogFetchedAt);
        Assert.Equal(Now, SavedFetchedAt(host));
        Assert.Equal(1, host.Notifications);
    }

    [Fact]
    public async Task ActivationKeepsAnUndatedCatalogUntilTheRefreshReplacesIt()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(release.Task, _ => Json(NewCatalog));
        using var http = new HttpClient(handler);
        // Fireworks 1.1.x saved its catalog without a date.
        var host = Seeded("first-key", "accounts/fireworks/models/older-chat", fetchedAt: null);
        using var plugin = new FireworksPlugin(http, refreshCatalogAutomatically: true, new TestClock(Now));

        await plugin.ActivateAsync(host);
        await handler.Started;
        Assert.Contains(plugin.SupportedModels, m => m.Id == "accounts/fireworks/models/older-chat");
        release.SetResult();
        await plugin.CatalogRefreshTask;

        Assert.Equal(NewCatalogModels, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(Now, plugin.CatalogFetchedAt);
    }

    [Fact]
    public async Task ActivationMakesNoRequestForACatalogYoungerThanADay()
    {
        var host = Seeded("first-key", "accounts/fireworks/models/current-chat", Now.AddHours(-23));
        using var plugin = Offline(new TestClock(Now), refresh: true);

        await plugin.ActivateAsync(host);
        await plugin.CatalogRefreshTask;

        Assert.Contains(plugin.SupportedModels, m => m.Id == "accounts/fireworks/models/current-chat");
        Assert.Empty(host.Logs);
    }

    [Fact]
    public async Task ActivationWithoutAKeyStartsNoRefresh()
    {
        var host = new Host();
        host.SetSetting("configuration", new { SecretName = (string?)null, Values = new Dictionary<string, string> { ["catalog"] = "accounts/fireworks/models/older-chat" } });
        using var plugin = Offline(new TestClock(Now), refresh: true);

        await plugin.ActivateAsync(host);

        Assert.True(plugin.CatalogRefreshTask.IsCompleted);
        Assert.Empty(host.Logs);
    }

    [Fact]
    public async Task RefreshFinishingAfterDeactivationChangesNothing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(release.Task, _ => Json(NewCatalog));
        using var http = new HttpClient(handler);
        var host = Seeded("first-key", "accounts/fireworks/models/older-chat", fetchedAt: null);
        var before = JsonSerializer.Serialize(host.Settings);
        using var plugin = new FireworksPlugin(http, refreshCatalogAutomatically: true, new TestClock(Now));

        await plugin.ActivateAsync(host);
        await handler.Started;
        await plugin.DeactivateAsync();
        release.SetResult();
        await plugin.CatalogRefreshTask;

        Assert.Equal(before, JsonSerializer.Serialize(host.Settings));
        Assert.Equal(0, host.Notifications);
        Assert.DoesNotContain(host.Logs, message => message.Contains("could not be refreshed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KeyChangeDropsTheOldCatalogAndRefreshesWithTheNewKey()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keys = new ConcurrentQueue<string?>();
        var handler = new BlockingHandler(release.Task, request =>
        {
            keys.Enqueue(request.Headers.Authorization?.Parameter);
            return Json(NewCatalog);
        });
        using var http = new HttpClient(handler);
        var host = Seeded("first-key", "accounts/fireworks/models/first-account-chat", Now.AddHours(-1));
        using var plugin = new FireworksPlugin(http, refreshCatalogAutomatically: true, new TestClock(Now));
        await plugin.ActivateAsync(host);
        Assert.True(plugin.CatalogRefreshTask.IsCompleted);

        await plugin.SetApiKeyAsync("second-key");
        await handler.Started;
        // The first account's catalog is gone before the new one arrives.
        Assert.Equal(["accounts/fireworks/models/gpt-oss-120b"], plugin.SupportedModels.Select(m => m.Id));
        Assert.Null(plugin.CatalogFetchedAt);
        release.SetResult();
        await plugin.CatalogRefreshTask;

        Assert.Equal(["second-key"], keys);
        Assert.Equal(NewCatalogModels, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(Now, SavedFetchedAt(host));
    }

    [Fact]
    public async Task CatalogFetchedWithAReplacedKeyIsDiscarded()
    {
        var firstAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(
            request => request.Headers.Authorization?.Parameter == "first-key" ? firstAnswer.Task : Task.CompletedTask,
            request => Json(request.Headers.Authorization?.Parameter == "first-key"
                ? """{"models":[{"name":"accounts/fireworks/models/first-account-chat"}]}"""
                : NewCatalog));
        using var http = new HttpClient(handler);
        var host = Seeded("first-key", "accounts/fireworks/models/older-chat", fetchedAt: null);
        using var plugin = new FireworksPlugin(http, refreshCatalogAutomatically: true, new TestClock(Now));
        await plugin.ActivateAsync(host);
        await handler.Started;
        var firstRefresh = plugin.CatalogRefreshTask;

        await plugin.SetApiKeyAsync("second-key");
        await plugin.CatalogRefreshTask;
        // The first key's answer arrives last.
        firstAnswer.SetResult();
        await firstRefresh;

        Assert.Equal(NewCatalogModels, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(NewCatalogModels[1..], SavedValue(host, "catalog")!.Split('\n'));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(503)]
    public async Task FailedRefreshKeepsTheCatalogAndLogsNeitherKeyNorBody(int status)
    {
        using var http = new HttpClient(new Handler((_, _) =>
            new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("""{"error":"detail for first-key"}""") }));
        var host = Seeded("first-key", "accounts/fireworks/models/current-chat", Now - ModelCatalogAutoRefresh.MaximumAge);
        var before = JsonSerializer.Serialize(host.Settings);
        using var plugin = new FireworksPlugin(http, refreshCatalogAutomatically: true, new TestClock(Now));

        await plugin.ActivateAsync(host);
        await plugin.CatalogRefreshTask;

        Assert.Equal(before, JsonSerializer.Serialize(host.Settings));
        var warning = Assert.Single(host.Logs, message => message.Contains("could not be refreshed", StringComparison.Ordinal));
        Assert.Contains($"HTTP {status}", warning);
        Assert.DoesNotContain("first-key", warning);
        Assert.DoesNotContain("detail", warning);
    }

    [Theory]
    [InlineData("""{"models":[]}""")]
    [InlineData("""{"models":[{"name":"accounts/fireworks/models/whisper-v3"},{"name":"nomic-embedding","kind":"EMBEDDING_MODEL"}]}""")]
    public async Task EmptyRefreshKeepsTheCatalog(string json)
    {
        using var http = new HttpClient(new Handler((_, _) => Json(json)));
        var host = Seeded("first-key", "accounts/fireworks/models/current-chat", fetchedAt: null);
        var before = JsonSerializer.Serialize(host.Settings);
        using var plugin = new FireworksPlugin(http, refreshCatalogAutomatically: true, new TestClock(Now));

        await plugin.ActivateAsync(host);
        await plugin.CatalogRefreshTask;

        Assert.Equal(before, JsonSerializer.Serialize(host.Settings));
        Assert.Contains(plugin.SupportedModels, m => m.Id == "accounts/fireworks/models/current-chat");
        Assert.Single(host.Logs, message => message.Contains("could not be refreshed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshWithAFailedLaterPageKeepsTheCatalog()
    {
        var pages = 0;
        using var http = new HttpClient(new Handler((_, _) => Interlocked.Increment(ref pages) == 1
            ? Json("""{"models":[{"name":"accounts/fireworks/models/page-one"}],"nextPageToken":"two"}""")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }));
        var host = Seeded("first-key", "accounts/fireworks/models/current-chat", fetchedAt: null);
        var before = JsonSerializer.Serialize(host.Settings);
        using var plugin = new FireworksPlugin(http, refreshCatalogAutomatically: true, new TestClock(Now));

        await plugin.ActivateAsync(host);
        await plugin.CatalogRefreshTask;

        Assert.Equal(2, pages);
        Assert.Equal(before, JsonSerializer.Serialize(host.Settings));
        Assert.DoesNotContain(plugin.SupportedModels, m => m.Id == "accounts/fireworks/models/page-one");
    }

    [Fact]
    public async Task RefreshKeepsTheSavedTextModelThatTheCatalogLacks()
    {
        using var http = new HttpClient(new Handler((_, _) => Json(NewCatalog)));
        var host = Seeded("first-key", "accounts/fireworks/models/older-chat", fetchedAt: null, llmModel: "accounts/me/deployedModels/my-deployment");
        using var plugin = new FireworksPlugin(http, refreshCatalogAutomatically: true, new TestClock(Now));

        await plugin.ActivateAsync(host);
        await plugin.CatalogRefreshTask;

        Assert.Equal("accounts/me/deployedModels/my-deployment", plugin.SupportedModels[0].Id);
        Assert.True(plugin.SupportedModels[0].IsRecommended);
        Assert.Contains(plugin.SupportedModels, m => m.Id == "accounts/fireworks/models/new-chat");
        Assert.Equal("accounts/me/deployedModels/my-deployment", SavedValue(host, "llmModel"));
    }

    [Fact]
    public async Task RefreshButtonSavesTheFetchDateAndStopsTheBackgroundRefresh()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        // The background refresh waits; the button's request answers at once.
        var handler = new BlockingHandler(_ => Interlocked.Increment(ref calls) == 1 ? release.Task : Task.CompletedTask, _ => Json(NewCatalog));
        using var http = new HttpClient(handler);
        var host = Seeded("first-key", "accounts/fireworks/models/older-chat", fetchedAt: null);
        using var plugin = new FireworksPlugin(http, refreshCatalogAutomatically: true, new TestClock(Now));
        await plugin.ActivateAsync(host);
        await handler.Started;
        var background = plugin.CatalogRefreshTask;

        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        var notifications = host.Notifications;
        release.SetResult();
        await background;

        Assert.Equal(Now, plugin.CatalogFetchedAt);
        Assert.Equal(Now, SavedFetchedAt(host));
        Assert.Equal(NewCatalogModels, plugin.SupportedModels.Select(m => m.Id));
        // The cancelled background refresh saves nothing more.
        Assert.Equal(notifications, host.Notifications);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-11-09")]
    public async Task DamagedFetchDateCountsAsNoDate(string value)
    {
        var host = Seeded("first-key", "accounts/fireworks/models/current-chat", fetchedAt: null, rawFetchedAt: value);
        using var plugin = Offline(new TestClock(Now));

        await plugin.ActivateAsync(host);

        Assert.Null(plugin.CatalogFetchedAt);
    }

    private static Host Seeded(string key, string? catalog, DateTimeOffset? fetchedAt, string? llmModel = null, string? rawFetchedAt = null)
    {
        var host = new Host();
        host.Secrets["fixture"] = key;
        var values = new Dictionary<string, string>();
        if (catalog is not null) values["catalog"] = catalog;
        if (fetchedAt is { } at) values["catalogFetchedAtUtc"] = at.ToString("O", CultureInfo.InvariantCulture);
        if (rawFetchedAt is not null) values["catalogFetchedAtUtc"] = rawFetchedAt;
        if (llmModel is not null) values["llmModel"] = llmModel;
        host.SetSetting("configuration", new { SecretName = "fixture", Values = values });
        return host;
    }

    private static string? SavedValue(Host host, string id) =>
        host.Settings["configuration"].GetProperty("Values").TryGetProperty(id, out var value) ? value.GetString() : null;

    private static DateTimeOffset? SavedFetchedAt(Host host) =>
        SavedValue(host, "catalogFetchedAtUtc") is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : null;

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BlockingHandler(Func<HttpRequestMessage, Task> wait, Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingHandler(Task release, Func<HttpRequestMessage, HttpResponseMessage> respond) : this(_ => release, respond) { }

        public Task Started => _started.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            // Ignores cancellation, so the answer can arrive after deactivation or a key change, like a slow server.
            await wait(request);
            return respond(request);
        }
    }
}
