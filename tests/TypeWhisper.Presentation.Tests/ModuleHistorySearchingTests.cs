using TypeWhisper.Core.Models;
using TypeWhisper.ModuleKit;
using TypeWhisper.PluginSDK;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ModuleHistorySearchingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TypeWhisper-HistorySearchTests", Guid.NewGuid().ToString("N"), HistorySearchIndex.DirectoryName);
    private readonly HistorySearchIndex _index;
    private readonly HistorySearchTestData.FakeHistory _history = new();
    private readonly HistorySearchIndexer _indexer;

    public ModuleHistorySearchingTests()
    {
        _index = new(_directory);
        // The indexer never runs on its own here, so the index can hold records History no longer has.
        _indexer = new(_history, _index, (stage, _) => Assert.Fail(stage), startupDelay: TimeSpan.FromHours(1));
    }

    public void Dispose()
    {
        _indexer.StopAsync().GetAwaiter().GetResult();
        _index.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_directory)!, recursive: true); }
        catch (IOException) { }
    }

    private ModuleHistorySearching Searching() => new(_indexer, _index, _history);

    [Fact]
    public async Task WithoutAnIndexReadsFindNothing()
    {
        _history.Replace(HistorySearchTestData.Record("a", "budget"));
        var searching = Searching();

        Assert.Empty(await searching.SearchAsync(new(["budget"])));
        Assert.Equal(0, (await searching.NewestRecordsAsync(new(), 5)).Total);
        Assert.Null(await searching.TranscriptAsync("a"));
        Assert.Same(ModuleHistoryVocabulary.Empty, await searching.VocabularyAsync());
    }

    [Fact]
    public async Task ReadsReturnOnlyRecordsHistoryStillHas()
    {
        var kept = HistorySearchTestData.Record("kept", "the budget meeting", recordedAt: 2_000);
        _index.Upsert([HistorySearchRecord.From(kept)!, HistorySearchRecord.From(HistorySearchTestData.Record("deleted", "the budget secret"))!]);
        _history.Replace(kept);
        _indexer.Start();
        var searching = Searching();

        var hits = await searching.SearchAsync(new(["budget"]) { Scope = new() { Sources = new HashSet<string> { "dictation" } } });

        var hit = Assert.Single(hits);
        Assert.Equal(("kept", "dictation", DateTimeOffset.FromUnixTimeSeconds(2_000)), (hit.RecordId, hit.Source, hit.RecordedAt));
        Assert.True(hit.Score > 0);
        Assert.Equal(1, (await searching.NewestRecordsAsync(new(), 10)).Total);
        Assert.Null(await searching.TranscriptAsync("deleted"));
        var transcript = (await searching.TranscriptAsync("kept"))!;
        var passage = Assert.Single(transcript.Passages);
        Assert.Equal(new ModuleHistoryPassage(ModuleHistoryPassageKind.Transcript, "the budget meeting") { TextSpan = new(0, 18) }, passage);

        await searching.SetSummaryAsync("kept", "Approved.", "provider", transcript.ContentHash, "");
        await searching.SetSummaryAsync("deleted", "Secret.", "provider", "any", "");
        Assert.Equal(new ModuleHistorySummary("Approved.", "provider"), await searching.SummaryAsync("kept", ""));
        Assert.Null(await searching.SummaryAsync("deleted", ""));
    }

    [Fact]
    public void ScopesAndPassagesMapToTheIndex()
    {
        var scope = ModuleHistorySearching.Scope(new()
        {
            Period = new(DateTimeOffset.FromUnixTimeSeconds(10), DateTimeOffset.FromUnixTimeSeconds(20)),
            AppProcessNames = new HashSet<string> { "winword" },
            RecordIds = new HashSet<string>()
        });
        var passage = ModuleHistorySearching.ToModulePassage(new(HistoryPassageKind.Result, "text", ["S1"], 1, 2, 3, 7));

        Assert.Equal((DateTimeOffset.FromUnixTimeSeconds(10), DateTimeOffset.FromUnixTimeSeconds(20)), (scope.PeriodStart!.Value, scope.PeriodEnd!.Value));
        Assert.Equal(["winword"], scope.AppProcessNames);
        Assert.Empty(scope.RecordIds!);
        Assert.Equal(new ModuleHistoryPassage(ModuleHistoryPassageKind.Result, "text")
            { Speakers = ["S1"], StartSeconds = 1, EndSeconds = 2, TextSpan = new(3, 7) }, passage);
    }

    [Fact]
    public void ModuleQueriesSplitWordsLikeTheIndexAndScopesFilterHits()
    {
        Assert.Equal(HistoryFullTextQuery.Words("Grüße, 42x — Ärger!"), ModuleHistorySearchQuery.WordsIn("Grüße, 42x — Ärger!"));
        var hit = new ModuleHistorySearchHit("a", DateTimeOffset.FromUnixTimeSeconds(15), "file", 0, 0, new(ModuleHistoryPassageKind.Transcript, "t"))
            { Domain = "example.com" };

        Assert.True(new ModuleHistorySearchScope { Period = new(DateTimeOffset.FromUnixTimeSeconds(15), DateTimeOffset.FromUnixTimeSeconds(16)) }.Includes(hit));
        Assert.False(new ModuleHistorySearchScope { Period = new(DateTimeOffset.FromUnixTimeSeconds(10), DateTimeOffset.FromUnixTimeSeconds(15)) }.Includes(hit));
        Assert.True(new ModuleHistorySearchScope { Domains = new HashSet<string> { "example.com" }, Sources = new HashSet<string> { "file" } }.Includes(hit));
        Assert.False(new ModuleHistorySearchScope { AppProcessNames = new HashSet<string> { "winword" } }.Includes(hit));
        Assert.False(new ModuleHistorySearchScope { RecordIds = new HashSet<string>() }.Includes(hit));
    }
}

public sealed class ModuleLanguageModelAnsweringTests
{
    private const string Blocked = "Your organization's policy could not be read.";

    private sealed class Fake
    {
        internal WorkflowLlmSelection? Enforced;
        internal string? BlockedReason;
        internal WorkflowLlmSelection? Preferred = new("local-llm", "small");
        internal List<ModuleLlmProviderState> Providers =
        [
            new("local-llm", "Local model", "com.example.local", IsLocal: true, Ready: true, ["small", "large"]),
            new("cloud-llm", "Cloud model", "com.example.cloud", IsLocal: false, Ready: true, ["fast"])
        ];
        internal Func<string, string?, string?> Refusal = (_, _) => null;
        internal Func<CancellationToken, Task<string>> Reply = _ => Task.FromResult("  An answer.  ");
        internal (string Provider, string System, string Text, string Model)? Request;
        internal List<(string Provider, string Model)> Prewarmed = [];
        internal int SettingsShown;
        internal List<string> Failures = [];

        internal ModuleLanguageModelAnswering Create() => new(new(
            () => Enforced, () => BlockedReason, () => Preferred, () => Providers, (id, model) => Refusal(id, model),
            (provider, system, text, model, ct) => { Request = (provider, system, text, model); return Reply(ct); },
            (provider, model, _) => { lock (Prewarmed) Prewarmed.Add((provider, model)); return Task.CompletedTask; },
            () => SettingsShown++), (stage, _) => Failures.Add(stage));
    }

    private static async Task<ModuleLanguageModelErrorKind> Failure(Task task) =>
        (await Assert.ThrowsAsync<ModuleLanguageModelException>(() => task)).Kind;

    [Fact]
    public void TheUsersDefaultIsTheProviderUnlessThePolicyEnforcesOne()
    {
        var fake = new Fake();

        var provider = Assert.Single(fake.Create().Providers());
        Assert.Equal(new ModuleLanguageModelProvider("local-llm", "Local model", ModuleLanguageModelKind.Local)
            { PluginId = "com.example.local", ModelId = "small" }, provider);

        fake.Enforced = new("CLOUD-LLM", "fast");
        Assert.Equal(new ModuleLanguageModelProvider("cloud-llm", "Cloud model", ModuleLanguageModelKind.Cloud)
            { PluginId = "com.example.cloud", ModelId = "fast" }, Assert.Single(fake.Create().Providers()));

        fake.Enforced = null;
        fake.Preferred = new("uninstalled", "model");
        Assert.Equal(ModuleLanguageModelKind.Cloud, Assert.Single(fake.Create().Providers()).Kind);
        fake.Preferred = null;
        Assert.Empty(fake.Create().Providers());
    }

    [Fact]
    public void AnEnforcedProviderThatCannotBeUsedOrABlockedPolicyIsARefusal()
    {
        var fake = new Fake { Enforced = new("missing", "model") };
        Assert.Equal(ModuleLanguageModelErrorKind.Refused, Assert.Throws<ModuleLanguageModelException>(() => fake.Create().Providers()).Kind);

        fake.Enforced = new("cloud-llm", "fast");
        fake.Refusal = (_, _) => "Cloud text processing is turned off by your organization.";
        var refused = Assert.Throws<ModuleLanguageModelException>(() => fake.Create().Providers());
        Assert.Equal((ModuleLanguageModelErrorKind.Refused, "Cloud text processing is turned off by your organization."), (refused.Kind, refused.Message));

        fake.BlockedReason = Blocked;
        Assert.Equal(Blocked, Assert.Throws<ModuleLanguageModelException>(() => fake.Create().Providers()).Message);
    }

    [Fact]
    public async Task ThePromptIsSentAsItIsAndTheReplyTrimmed()
    {
        var fake = new Fake();
        var answering = fake.Create();

        var reply = await answering.AnswerAsync("Instructions", "  The prompt\n", answering.Providers()[0]);

        Assert.Equal("An answer.", reply);
        Assert.Equal(("local-llm", "Instructions", "  The prompt\n", "small"), fake.Request);
        Assert.Null(await answering.InputTokensAsync("Instructions", "prompt", answering.Providers()[0]));
    }

    [Fact]
    public async Task PolicyAndSetupAreCheckedBeforeTheProviderRuns()
    {
        var fake = new Fake();
        var answering = fake.Create();
        var local = answering.Providers()[0];

        fake.Refusal = (id, model) => id == "local-llm" && model == "small" ? "The text model provider local-llm is not allowed by your organization." : null;
        Assert.Equal(ModuleLanguageModelErrorKind.Refused, await Failure(answering.AnswerAsync("i", "p", local)));
        fake.Refusal = (_, _) => null;

        fake.Enforced = new("cloud-llm", "fast");
        Assert.Equal(ModuleLanguageModelErrorKind.Refused, await Failure(answering.AnswerAsync("i", "p", local)));
        fake.Enforced = null;

        Assert.Equal(ModuleLanguageModelErrorKind.NotSetUp, await Failure(answering.AnswerAsync("i", "p", local with { ModelId = "missing" })));
        Assert.Equal(ModuleLanguageModelErrorKind.NotSetUp, await Failure(answering.AnswerAsync("i", "p", local with { Id = "uninstalled" })));
        fake.Providers[0] = fake.Providers[0] with { Ready = false };
        Assert.Equal(ModuleLanguageModelErrorKind.NotSetUp, await Failure(answering.AnswerAsync("i", "p", local)));
        Assert.Null(fake.Request);
    }

    [Fact]
    public async Task ProviderFailuresBecomeModuleErrors()
    {
        var fake = new Fake();
        var answering = fake.Create();
        var local = answering.Providers()[0];
        async Task<ModuleLanguageModelErrorKind> Kind(Exception error)
        {
            fake.Reply = _ => Task.FromException<string>(error);
            return await Failure(answering.AnswerAsync("i", "p", local));
        }

        Assert.Equal(ModuleLanguageModelErrorKind.Refused, await Kind(new ManagedPolicyDeniedException("llm", "Refused by policy.")));
        Assert.Equal(ModuleLanguageModelErrorKind.ContextExceeded, await Kind(new PluginRequestException("Too long.", PluginRequestFailureKind.RequestTooLarge)));
        Assert.Equal(ModuleLanguageModelErrorKind.NotSetUp, await Kind(new PluginRequestException("No key.", PluginRequestFailureKind.Authentication)));
        Assert.Equal(ModuleLanguageModelErrorKind.EmptyReply, await Kind(new PluginRequestException("Empty.", PluginRequestFailureKind.EmptyResponse)));
        Assert.Equal(ModuleLanguageModelErrorKind.Failed, await Kind(new PluginRequestException("Down.", PluginRequestFailureKind.ServerError)));
        Assert.Equal(ModuleLanguageModelErrorKind.Failed, await Kind(new OperationCanceledException()));
        Assert.Equal(ModuleLanguageModelErrorKind.Failed, await Kind(new InvalidOperationException("LLM provider changed.")));

        fake.Reply = _ => Task.FromResult(" \n ");
        Assert.Equal(ModuleLanguageModelErrorKind.EmptyReply, await Failure(answering.AnswerAsync("i", "p", local)));
    }

    [Fact]
    public async Task CancellingARequestStaysACancellation()
    {
        var fake = new Fake { Reply = async ct => { await Task.Delay(Timeout.Infinite, ct); return ""; } };
        var answering = fake.Create();
        using var cancel = new CancellationTokenSource();

        var request = answering.AnswerAsync("i", "p", answering.Providers()[0], cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task PrewarmLoadsOnlyALocalFirstProviderAndSettingsOpen()
    {
        var fake = new Fake();
        var answering = fake.Create();

        answering.Prewarm();
        fake.Preferred = new("cloud-llm", "fast");
        answering.Prewarm();
        fake.BlockedReason = Blocked;
        answering.Prewarm();
        answering.ShowSettings();

        await Task.Delay(50);
        Assert.Equal([("local-llm", "small")], fake.Prewarmed);
        Assert.Equal(1, fake.SettingsShown);
        Assert.Empty(fake.Failures);
    }
}
