using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LocalApiBrowserContextTests
{
    private const int OwnProcessId = 4242;
    private BrowserForegroundProcess? _foreground = new(100, "chrome");
    private DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private BrowserExtensionContextStore Store() => new(() => _foreground, OwnProcessId, () => _now);

    [Fact]
    public void FocusedReportBelongsToTheForegroundProcessOnly()
    {
        var store = Store();
        var report = store.Report("chrome-client", true, "https://mail.example.com/inbox", "Inbox");
        Assert.Equal(new BrowserExtensionReport("chrome-client", 100, "chrome", "https://mail.example.com/inbox", "Inbox", _now), report);
        Assert.Equal(report, store.Current(100, "chrome"));
        Assert.Equal(report, store.Current(100, "Chrome"));
        Assert.Null(store.Current(101, "chrome"));
        Assert.Null(store.Current(100, "msedge"));

        _foreground = new(200, "msedge");
        var edge = store.Report("edge-client", true, "https://example.org", null);
        Assert.Null(store.Current(100, "chrome"));
        Assert.Equal(edge, store.Current(200, "msedge"));
    }

    [Fact]
    public void ReportsAreIgnoredWhileTypeWhisperOrNoWindowIsInFront()
    {
        var store = Store();
        var stored = store.Report("client", true, "https://example.com", null);
        _foreground = new(OwnProcessId, "TypeWhisper");
        Assert.Null(store.Report("client", true, "https://other.example", null));
        _foreground = null;
        Assert.Null(store.Report("client", true, "https://other.example", null));
        // An ignored report leaves the previous one in place.
        Assert.Equal(stored, store.Current(100, "chrome"));
        Assert.Null(store.Current(OwnProcessId, "TypeWhisper"));
    }

    [Fact]
    public void LosingFocusClearsOnlyTheSameClientsReport()
    {
        var store = Store();
        _foreground = new(200, "msedge");
        var edge = store.Report("edge-client", true, "https://example.org", null);
        // Chrome reports losing focus after Edge came to the front.
        Assert.Null(store.Report("chrome-client", false, null, null));
        Assert.Equal(edge, store.Current(200, "msedge"));
        Assert.Null(store.Report("edge-client", false, null, null));
        Assert.Null(store.Current(200, "msedge"));
    }

    [Fact]
    public void ReportsExpireAfterThreeMinutes()
    {
        var store = Store();
        Assert.NotNull(store.Report("client", true, "https://example.com", null));
        _now += TimeSpan.FromSeconds(180);
        Assert.NotNull(store.Current(100, "chrome"));
        _now += TimeSpan.FromMilliseconds(1);
        Assert.Null(store.Current(100, "chrome"));
        Assert.NotNull(store.Report("client", true, "https://example.com", null));
        Assert.NotNull(store.Current(100, "chrome"));
    }

    [Fact]
    public async Task ReportWithoutUrlMeansNoWebsiteAndSkipsTheAddressBarRead()
    {
        var store = Store();
        store.Report("client", true, null, "New Tab");
        var reads = 0;
        Assert.Null(await store.CaptureHostAsync(100, "chrome", () => { reads++; return Task.FromResult<string?>("example.com"); }));
        Assert.Equal(0, reads);
        // Without a report for the target, the address bar read decides.
        Assert.Equal("example.com", await store.CaptureHostAsync(101, "chrome", () => { reads++; return Task.FromResult<string?>("example.com"); }));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ReportedUrlBeatsTheAddressBarReadAndMatchesAWebsiteWorkflow()
    {
        // Vivaldi is not a browser the UI Automation read knows.
        _foreground = new(300, "vivaldi");
        var store = Store();
        store.Report("vivaldi-client", true, "https://docs.example.com/document/1?token=secret", "Document");
        var host = await store.CaptureHostAsync(300, "vivaldi",
            () => throw new InvalidOperationException("The address bar must not be read for a reported process."));
        Assert.Equal("docs.example.com", host);
        var workflows = new[]
        {
            Rule("global", WorkflowTrigger.Global(), -100),
            Rule("website", WorkflowTrigger.Website("example.com"), 0)
        };
        Assert.Equal("website", AutomaticWorkflowSnapshot.Select(workflows, "vivaldi", host)!.Id);
    }

    [Fact]
    public void HandlerStoresAValidReport()
    {
        var store = Store();
        var response = Put(store, """{"client_id":"  ext-1  ","focused":true,"url":" https://example.com/a?b=c ","title":"  Example  "}""");
        Assert.Equal(200, response.StatusCode);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal("active", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("chrome", json.RootElement.GetProperty("app_bundle_id").GetString());
        Assert.Equal("https://example.com/a?b=c", json.RootElement.GetProperty("url").GetString());
        Assert.Equal(new BrowserExtensionReport("ext-1", 100, "chrome", "https://example.com/a?b=c", "Example", _now), store.Current(100, "chrome"));
    }

    [Theory]
    [InlineData("chrome://settings")]
    [InlineData("chrome://newtab/")]
    [InlineData("about:blank")]
    [InlineData("edge://favorites")]
    [InlineData("https://example.com/with space")]
    [InlineData("")]
    public void NonWebsiteUrlsAreStoredAsNoUrl(string url)
    {
        var store = Store();
        var response = Put(store, JsonSerializer.Serialize(new { client_id = "ext", focused = true, url }));
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal("active", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("url").ValueKind);
        Assert.Null(store.Current(100, "chrome")!.Url);
    }

    [Fact]
    public void UrlRulesFollowTheMacApp()
    {
        Assert.Equal("file:///C:/Docs/page.html", BrowserExtensionContextStore.NormalizeUrl("file:///C:/Docs/page.html"));
        Assert.Equal("http://example.com", BrowserExtensionContextStore.NormalizeUrl("http://example.com"));
        var longest = "https://example.com/" + new string('a', 2047 - 20);
        Assert.Equal(longest, BrowserExtensionContextStore.NormalizeUrl(longest));
        Assert.Null(BrowserExtensionContextStore.NormalizeUrl(longest + "a"));
        Assert.Null(BrowserExtensionContextStore.NormalizeUrl("ftp://example.com"));
        Assert.Null(BrowserExtensionContextStore.NormalizeUrl(null));
    }

    [Fact]
    public void TitleIsTrimmedAndCut()
    {
        var store = Store();
        Put(store, JsonSerializer.Serialize(new { client_id = "ext", focused = true, url = "https://example.com", title = "  " + new string('t', 600) + "  " }));
        Assert.Equal(new string('t', 512), store.Current(100, "chrome")!.Title);
        Put(store, JsonSerializer.Serialize(new { client_id = "ext", focused = true, url = "https://example.com", title = "   " }));
        Assert.Null(store.Current(100, "chrome")!.Title);
        Assert.Equal(new string('t', 511), BrowserExtensionContextStore.NormalizeTitle(new string('t', 511) + "😀"));
    }

    [Fact]
    public void HandlerReportsIgnoredAndCleared()
    {
        var store = Store();
        _foreground = new(OwnProcessId, "TypeWhisper");
        AssertStatus(Put(store, """{"client_id":"ext","focused":true,"url":"https://example.com","title":null}"""), "ignored");
        _foreground = new(100, "chrome");
        AssertStatus(Put(store, """{"client_id":"ext","focused":true,"url":"https://example.com"}"""), "active");
        AssertStatus(Put(store, """{"client_id":"ext","focused":false}"""), "cleared");
        Assert.Null(store.Current(100, "chrome"));

        static void AssertStatus(LocalApiResponse response, string status)
        {
            using var json = JsonDocument.Parse(response.Body);
            Assert.Equal(status, json.RootElement.GetProperty("status").GetString());
            if (status != "active")
            {
                Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("app_bundle_id").ValueKind);
                Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("url").ValueKind);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"focused":true}""")]
    [InlineData("""{"client_id":"   ","focused":true}""")]
    [InlineData("""{"client_id":7,"focused":true}""")]
    [InlineData("""{"client_id":"ext"}""")]
    [InlineData("""{"client_id":"ext","focused":"true"}""")]
    [InlineData("""{"client_id":"ext","focused":true,"url":5}""")]
    [InlineData("""{"client_id":"ext","focused":true,"title":false}""")]
    [InlineData("""{"client_id":"ext","focused":true,"tab_id":3}""")]
    [InlineData("""{"client_id":"ext","client_id":"other","focused":true}""")]
    public void InvalidBodiesReturn400(string body)
    {
        var store = Store();
        var response = Put(store, body);
        Assert.Equal(400, response.StatusCode);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal("bad_request", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("error").GetProperty("message").GetString()));
        Assert.Null(store.Current(100, "chrome"));
    }

    [Fact]
    public void ClientIdIsLimitedTo128Characters()
    {
        var store = Store();
        Assert.Equal(200, Put(store, JsonSerializer.Serialize(new { client_id = new string('c', 128), focused = true })).StatusCode);
        Assert.Equal(400, Put(store, JsonSerializer.Serialize(new { client_id = new string('c', 129), focused = true })).StatusCode);
    }

    [Fact]
    public void HandlerOwnsOnlyItsRouteAndMethod()
    {
        var store = Store();
        Assert.Null(LocalApiBrowserContext.Handle(new("PUT", "/v1/rules/toggle", [], null, new Dictionary<string, string?>()), store));
        Assert.Equal(405, LocalApiBrowserContext.Handle(new("GET", "/v1/browser/context", [], null, new Dictionary<string, string?>()), store)!.StatusCode);
        Assert.Equal(400, LocalApiBrowserContext.Handle(new("PUT", "/v1/browser/context", Encoding.UTF8.GetBytes("""{"client_id":"ext","focused":true}"""),
            "application/json", new Dictionary<string, string?> { ["x"] = "1" }), store)!.StatusCode);
        Assert.True(LocalApiRouteCatalog.Contains("PUT", "/v1/browser/context"));
    }

    [Fact]
    public void StatusAnnouncesBrowserContextSupport()
    {
        using var json = JsonDocument.Parse(LocalApiStatus.Response(true, "parakeet", "model", "1.2", false, true).Body);
        Assert.True(json.RootElement.GetProperty("supports_browser_context").GetBoolean());
        Assert.Equal("ready", json.RootElement.GetProperty("status").GetString());
        Assert.True(json.RootElement.GetProperty("supports_workflow_dictation").GetBoolean());
        Assert.False(json.RootElement.GetProperty("supports_streaming").GetBoolean());
        Assert.True(json.RootElement.GetProperty("supports_translation").GetBoolean());
    }

    private static LocalApiResponse Put(BrowserExtensionContextStore store, string body) =>
        LocalApiBrowserContext.Handle(new("PUT", "/v1/browser/context", Encoding.UTF8.GetBytes(body), "application/json",
            new Dictionary<string, string?>()), store)!;

    private static Workflow Rule(string id, WorkflowTrigger trigger, int order) => new()
    {
        Id = id, Name = id, Template = WorkflowTemplate.Custom, Trigger = trigger, SortOrder = order,
        Behavior = new() { ProviderOverride = "provider", ModelOverride = "model", FineTuning = "Preserve the meaning." }
    };
}
