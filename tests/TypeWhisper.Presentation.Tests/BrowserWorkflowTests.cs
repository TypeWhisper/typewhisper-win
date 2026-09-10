using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using TypeWhisper.Core.Interfaces;
using Moq;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class BrowserWorkflowTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CapturedHostUsesExistingHistoryPrivacyAtStartAndDelivery(bool saveAtStart, bool saveNow)
    {
        var record = new TranscriptionRecord
        { Id = "record", Timestamp = DateTime.UtcNow, RawText = "raw", FinalText = "final", AppUrl = BrowserWorkflowContext.NormalizeHost("https://example.com/private?token=secret") };
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        if (saveAtStart && saveNow)
        {
            history.Setup(service => service.EnsureLoadedAsync()).Returns(Task.CompletedTask);
            history.Setup(service => service.TryAddRecord(record)).Returns(true);
        }
        var result = await new DictationOutputDelivery(history.Object).DeliverAsync(record,
            new() { AutoPaste = false, SaveToHistory = saveAtStart }, () => new() { AutoPaste = false, SaveToHistory = saveNow },
            () => throw new InvalidOperationException("This test never pastes."));
        Assert.Equal(saveAtStart && saveNow, result.Saved);
        Assert.Equal("example.com", result.Record.AppUrl);
        history.Verify(service => service.TryAddRecord(It.IsAny<TranscriptionRecord>()), saveAtStart && saveNow ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("https://mail.example.com/private?token=secret#fragment", "mail.example.com")]
    [InlineData("EXAMPLE.com/path", "example.com")]
    [InlineData("https://bücher.de/a", "xn--bcher-kva.de")]
    [InlineData("https://user:secret@example.com", null)]
    [InlineData("file:///private/document.html", null)]
    [InlineData("chrome://settings", null)]
    [InlineData("about:blank", null)]
    [InlineData("search for example.com", null)]
    [InlineData("https://127.0.0.1", null)]
    [InlineData("https://example.com\\evil", null)]
    public void AddressNormalizationRetainsOnlyVerifiedWebHost(string address, string? expected) =>
        Assert.Equal(expected, BrowserWorkflowContext.NormalizeHost(address));

    [Theory]
    [InlineData("chrome", "view_1012", "view_1000", "Ctrl+L", true)]
    [InlineData("msedge", "view_1012", "view_1000", "Ctrl+L", true)]
    [InlineData("brave", "view_1012", "view_1000", "Ctrl+L", true)]
    [InlineData("firefox", "urlbar-input", "nav-bar", "", true)]
    [InlineData("chrome", "search-field", "view_1000", "Ctrl+L", false)]
    [InlineData("chrome", "view_1012", "page-toolbar", "Ctrl+L", false)]
    [InlineData("chrome", "view_1012", "view_1000", "", false)]
    [InlineData("unknown", "view_1012", "view_1000", "Ctrl+L", false)]
    public void AddressBarRequiresVerifiedBrowserChromeIdentity(string process, string id, string toolbar, string shortcut, bool expected) =>
        Assert.Equal(expected, BrowserWorkflowContext.IsAddressBar(process, id, toolbar, shortcut, false, false, false));

    [Fact]
    public void DocumentSubtreesAndEditedPasswordOrHiddenAddressFieldsAreExcluded()
    {
        Assert.False(BrowserWorkflowContext.CanInspectSubtree(50030, "arbitrary-document"));
        Assert.False(BrowserWorkflowContext.CanInspectSubtree(50033, "RootWebArea"));
        Assert.True(BrowserWorkflowContext.CanInspectSubtree(50021, "view_1000"));
        Assert.False(BrowserWorkflowContext.IsAddressBar("chrome", "view_1012", "view_1000", "Ctrl+L", true, false, false));
        Assert.False(BrowserWorkflowContext.IsAddressBar("chrome", "view_1012", "view_1000", "Ctrl+L", false, true, false));
        Assert.False(BrowserWorkflowContext.IsAddressBar("chrome", "view_1012", "view_1000", "Ctrl+L", false, false, true));
    }

    [Fact]
    public void SharedMatcherKeepsAppWebsitePrecedenceAndDomainBoundaries()
    {
        var global = Rule("global", WorkflowTrigger.Global(), -100);
        var app = Rule("app", WorkflowTrigger.App("chrome"), -10);
        var website = Rule("website", WorkflowTrigger.Website("example.com"), 0);
        var both = Rule("both", WorkflowTrigger.App("chrome") with { WebsitePatterns = ["example.com"] }, 100);
        var rules = new[] { global, app, website, both };
        Assert.Equal("both", AutomaticWorkflowSnapshot.Select(rules, "chrome", "mail.example.com")!.Id);
        Assert.Equal("website", AutomaticWorkflowSnapshot.Select(rules, "firefox", "example.com")!.Id);
        Assert.Equal("app", AutomaticWorkflowSnapshot.Select(rules, "chrome", "notexample.com")!.Id);
        Assert.Equal("app", AutomaticWorkflowSnapshot.Select(rules, "chrome", null)!.Id);
        Assert.Equal("global", AutomaticWorkflowSnapshot.Select(rules, "firefox", null)!.Id);
        Assert.Equal(WorkflowService.MatchSnapshot(rules, "chrome", "mail.example.com")!.Workflow.Id,
            AutomaticWorkflowSnapshot.Select(rules, "chrome", "mail.example.com")!.Id);
    }

    [Theory]
    [InlineData(WorkflowContextMatchMode.All, null, false)]
    [InlineData(WorkflowContextMatchMode.Any, null, true)]
    [InlineData(WorkflowContextMatchMode.All, "example.com", true)]
    [InlineData(WorkflowContextMatchMode.Any, "example.com", true)]
    public void AllRequiresBothComponentsWhileAnyCanUseTheAppWithoutDomain(WorkflowContextMatchMode mode, string? domain, bool matches)
    {
        var rule = Rule("combined", WorkflowTrigger.Website("example.com") with { ProcessNames = ["chrome"], ContextMatchMode = mode });
        Assert.Equal(matches, AutomaticWorkflowSnapshot.Select([rule], "chrome", domain) is not null);
        Assert.Null(AutomaticWorkflowSnapshot.Select([rule with { IsEnabled = false }], "chrome", domain));
    }

    [Fact]
    public async Task WebsiteEditorPersistsMatchModeAndSnapshotSurvivesLaterTabOrRuleChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "website-workflows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "workflows.json");
            var rule = Rule("website", WorkflowTrigger.Website("example.com") with { ProcessNames = ["chrome"] });
            var draft = WorkflowDraft.FromStored(rule) with { WebsiteDomains = "BÜCHER.de, *.example.com", ContextMatchMode = WorkflowContextMatchMode.Any };
            var stored = draft.ToStored();
            new ManualWorkflowStore(path).Save(stored, allowAutomatic: true);
            var restarted = Assert.Single(new ManualWorkflowStore(path).Read());
            Assert.Equal(["xn--bcher-kva.de", "*.example.com"], restarted.Trigger.WebsitePatterns);
            Assert.Equal(WorkflowContextMatchMode.Any, restarted.Trigger.ContextMatchMode);
            Assert.Equal("chrome", Assert.Single(restarted.Trigger.ProcessNames));
            var snapshot = AutomaticWorkflowSnapshot.Select([restarted], "firefox", "bücher.de")!;
            new ManualWorkflowStore(path).Delete(restarted.Id, allowAutomatic: true);
            var output = await snapshot.ProcessAsync("source", "en", "en", (_, _) => true,
                (_, prompt, text, _, _) => Task.FromResult(text + " processed"), default);
            Assert.Equal("source processed", output);
            Assert.Equal("website", snapshot.Id);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CancellationReturnsPromptlyAndDoesNotCreateAnotherWorkerWhileOldProbeIsRunning()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new BrowserCaptureTarget(123, 456, "chrome");
        var calls = 0;
        var capture = new BrowserTargetCapture((actual, _) =>
        {
            Assert.Equal(target, actual); Interlocked.Increment(ref calls); entered.SetResult();
            try { if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return "https://late.example.com/private"; }
            finally { finished.SetResult(); }
        }, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var first = capture.CaptureAsync(target, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Null(await capture.CaptureAsync(target, default));
            Assert.Equal(1, calls);
        }
        finally { release.Set(); await finished.Task; }
    }

    [Fact]
    public async Task TimeoutDiscardsLateValueAndUnknownBrowserNeverCallsBackend()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var capture = new BrowserTargetCapture((_, _) =>
        {
            Interlocked.Increment(ref calls); entered.SetResult();
            try { if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return "late.example.com"; }
            finally { finished.SetResult(); }
        }, TimeSpan.FromMilliseconds(500));
        Assert.Null(await capture.CaptureAsync(new(123, 456, "unknown"), default));
        var task = capture.CaptureAsync(new(123, 456, "chrome"), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Null(await task);
            Assert.Null(await capture.CaptureAsync(new(123, 456, "chrome"), default));
            Assert.Equal(1, calls);
        }
        finally { release.Set(); await finished.Task; }
    }

    private static Workflow Rule(string id, WorkflowTrigger trigger, int order = 0) => new()
    {
        Id = id, Name = id, Template = WorkflowTemplate.Custom, Trigger = trigger, SortOrder = order,
        Behavior = new() { ProviderOverride = "provider", ModelOverride = "model", FineTuning = "Preserve the meaning." }
    };
}
