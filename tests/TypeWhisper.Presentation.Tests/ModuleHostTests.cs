using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.ModuleKit;
using TypeWhisper.WinUI;
using Xunit;
using static TypeWhisper.Presentation.Tests.ManagedPolicySnapshots;
using Report = TypeWhisper.Presentation.SupportDiagnosticsReport;

namespace TypeWhisper.Presentation.Tests;

public sealed class ModuleKitTests
{
    [Theory]
    [InlineData(1, 0, 1, 0, true)]
    [InlineData(1, 2, 1, 1, true)]
    [InlineData(1, 1, 1, 2, false)]
    [InlineData(2, 0, 1, 0, false)]
    [InlineData(1, 0, 2, 0, false)]
    public void SupportsTheSameMajorAndAnEarlierOrEqualMinor(int major, int minor, int requiredMajor, int requiredMinor, bool supported)
    {
        Assert.Equal(supported, new ModuleKitVersion(major, minor).Supports(new(requiredMajor, requiredMinor)));
        Assert.True(ModuleKitVersion.Current.Supports(ModuleKitVersion.Current));
        Assert.True(new ModuleKitVersion(1, 2).CompareTo(new(1, 10)) < 0);
        Assert.Equal("1.2", new ModuleKitVersion(1, 2).ToString());
    }

    [Fact]
    public void BooleanValuePrefersEnforcedThenUserThenRecommendationThenDefault()
    {
        var on = new ModuleManagedPolicyValue.Boolean(true);
        var off = new ModuleManagedPolicyValue.Boolean(false);

        Assert.False(new ModuleManagedPolicyState(Enforced: off, Recommended: on).BooleanValue(true, userPreference: true));
        Assert.True(new ModuleManagedPolicyState(Recommended: off).BooleanValue(false, userPreference: true));
        Assert.False(new ModuleManagedPolicyState(Recommended: off).BooleanValue(true));
        Assert.True(ModuleManagedPolicyState.Unmanaged.BooleanValue(true));
        // A value of another kind does not count.
        Assert.True(new ModuleManagedPolicyState(Enforced: new ModuleManagedPolicyValue.Enumeration(0)).BooleanValue(true));
    }

    [Fact]
    public void ListValuesCompareByTheirItems()
    {
        Assert.Equal(new ModuleManagedPolicyValue.List(["a", "b"]), new ModuleManagedPolicyValue.List(new List<string> { "a", "b" }));
        Assert.NotEqual(new ModuleManagedPolicyValue.List(["a", "b"]), new ModuleManagedPolicyValue.List(["b", "a"]));
    }

    [Fact]
    public void TranslationsFallBackToEnglishAndFormat()
    {
        var translations = new ModuleTranslations(new Dictionary<string, string> { ["Hello {0}"] = "Hallo {0}" });

        Assert.Equal("Hallo Welt", translations.T("Hello {0}", "Welt"));
        Assert.Equal("Untranslated", translations.T("Untranslated"));
        Assert.Equal("Hello", ModuleTranslations.Load(typeof(ModuleKitTests).Assembly, "de").T("Hello"));
        Assert.Equal("Hello", ModuleTranslations.Load(typeof(ModuleKitTests).Assembly, "en").T("Hello"));
    }
}

public sealed class ModulePolicyKeysTests
{
    private static (ModulePolicyKeys.Resolution Resolution, List<(string? Name, string Reason)> Rejected) Resolve(params ModuleManagedPolicyKey[] keys)
    {
        var rejected = new List<(string? Name, string Reason)>();
        return (ModulePolicyKeys.Resolve(keys, (name, reason) => rejected.Add((name, reason))), rejected);
    }

    [Fact]
    public void NewKeysBecomeDefinitionsWithTheirShape()
    {
        var (resolution, rejected) = Resolve(
            new("AllowExample", ModuleManagedPolicyKind.Boolean, AllowsRecommendation: true),
            new("ExampleMode", ModuleManagedPolicyKind.Enumeration, EnumerationMaximum: 2),
            new("ExampleToken", ModuleManagedPolicyKind.Text, Sensitive: true),
            new("ExampleTargets", ModuleManagedPolicyKind.List),
            new("BooleanWithMaximum", ModuleManagedPolicyKind.Boolean, EnumerationMaximum: 5));

        Assert.Empty(rejected);
        Assert.Equal(
            [
                new ManagedPolicyDefinition("AllowExample", ManagedPolicyValueKind.Boolean, AllowsRecommendation: true),
                new ManagedPolicyDefinition("ExampleMode", ManagedPolicyValueKind.Enumeration, EnumerationMaximum: 2),
                new ManagedPolicyDefinition("ExampleToken", ManagedPolicyValueKind.Text, Sensitive: true),
                new ManagedPolicyDefinition("ExampleTargets", ManagedPolicyValueKind.List),
                new ManagedPolicyDefinition("BooleanWithMaximum", ManagedPolicyValueKind.Boolean)
            ], resolution.Definitions);
        Assert.Equal(["AllowExample", "BooleanWithMaximum", "ExampleMode", "ExampleTargets", "ExampleToken"], resolution.DeclaredNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AHostKeyWithTheSameKindIsReadableButKeepsTheHostDefinition()
    {
        var (resolution, rejected) = Resolve(
            new(ManagedPolicyKeys.AllowCloudTranscription, ModuleManagedPolicyKind.Boolean, AllowsRecommendation: true),
            new(ManagedPolicyKeys.AllowedPlugins, ModuleManagedPolicyKind.List));

        Assert.Empty(rejected);
        Assert.Empty(resolution.Definitions);
        Assert.Equal([ManagedPolicyKeys.AllowCloudTranscription, ManagedPolicyKeys.AllowedPlugins], resolution.DeclaredNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ConflictingAndInvalidKeysAreReportedAndLeftOut()
    {
        var (resolution, rejected) = Resolve(
            new(ManagedPolicyKeys.AllowCloudTranscription, ModuleManagedPolicyKind.Text),
            new("allowcloudtranscription", ModuleManagedPolicyKind.Boolean),
            new(ManagedPolicyKeys.AllowedPlugins + "Configured", ModuleManagedPolicyKind.Boolean),
            new("AllowedPluginsConfiguredConfigured", ModuleManagedPolicyKind.Boolean),
            new("Recommended", ModuleManagedPolicyKind.Boolean),
            new("ExampleMode", ModuleManagedPolicyKind.Enumeration),
            new("1Example", ModuleManagedPolicyKind.Boolean),
            new("Example-Key", ModuleManagedPolicyKind.Boolean),
            new("", ModuleManagedPolicyKind.Boolean),
            new(new string('A', 129), ModuleManagedPolicyKind.Boolean),
            new("Example", (ModuleManagedPolicyKind)99),
            new("Shared", ModuleManagedPolicyKind.Boolean),
            new("Shared", ModuleManagedPolicyKind.Boolean),
            new("Shared", ModuleManagedPolicyKind.Text),
            new("SHARED", ModuleManagedPolicyKind.Boolean),
            new("Shared", ModuleManagedPolicyKind.Boolean, Sensitive: true),
            new("Targets", ModuleManagedPolicyKind.List),
            new("TargetsConfigured", ModuleManagedPolicyKind.Boolean),
            new("Mode", ModuleManagedPolicyKind.Boolean),
            new("Mode", ModuleManagedPolicyKind.List));

        Assert.Equal(["AllowedPluginsConfiguredConfigured", "Shared", "Targets", "Mode"], resolution.Definitions.Select(definition => definition.Name));
        Assert.Equal(["AllowedPluginsConfiguredConfigured", "Mode", "Shared", "Targets"], resolution.DeclaredNames.Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                (ManagedPolicyKeys.AllowCloudTranscription, "kind-conflict"),
                ("allowcloudtranscription", "case-conflict"),
                (ManagedPolicyKeys.AllowedPlugins + "Configured", "marker-conflict"),
                ("Recommended", "reserved-name"),
                ("ExampleMode", "enumeration-maximum"),
                (null, "invalid-name"),
                (null, "invalid-name"),
                (null, "invalid-name"),
                (null, "invalid-name"),
                ("Example", "unknown-kind"),
                ("Shared", "kind-conflict"),
                ("SHARED", "case-conflict"),
                ("Shared", "declaration-conflict"),
                ("TargetsConfigured", "marker-conflict"),
                ("Mode", "kind-conflict")
            ], rejected);
    }

    [Fact]
    public void AListWhoseMarkerIsTakenIsLeftOut()
    {
        var (resolution, rejected) = Resolve(
            new("ExampleConfigured", ModuleManagedPolicyKind.Boolean),
            new("Example", ModuleManagedPolicyKind.List));

        Assert.Equal(["ExampleConfigured"], resolution.Definitions.Select(definition => definition.Name));
        Assert.Equal([("Example", "marker-conflict")], rejected);
    }

    [Fact]
    public void StateReadsOnlyDeclaredKeys()
    {
        var snapshot = ManagedPolicyValidator.Validate(new(true, null, ManagedPolicyRawSection.Of(
                [
                    KeyValuePair.Create("AllowExample", ManagedPolicyRawValue.FromNumber(0)),
                    KeyValuePair.Create("ExampleMode", ManagedPolicyRawValue.FromNumber(9)),
                    KeyValuePair.Create("ExampleTargetsConfigured", ManagedPolicyRawValue.FromNumber(1)),
                    KeyValuePair.Create(ManagedPolicyKeys.AllowCloudTranscription, ManagedPolicyRawValue.FromNumber(0))
                ],
                [KeyValuePair.Create("ExampleTargets", new ManagedPolicyRawList(["a", "a", "b"]))]),
            ManagedPolicyRawSection.Of([KeyValuePair.Create("AllowExample", ManagedPolicyRawValue.FromNumber(1))])),
            1, DateTimeOffset.UtcNow,
            [
                new("AllowExample", ManagedPolicyValueKind.Boolean, AllowsRecommendation: true),
                new("ExampleMode", ManagedPolicyValueKind.Enumeration, EnumerationMaximum: 2),
                new("ExampleTargets", ManagedPolicyValueKind.List)
            ]);
        var declared = new HashSet<string>(["AllowExample", "ExampleMode", "ExampleTargets", "Unset"]);

        Assert.Equal(new ModuleManagedPolicyState(new ModuleManagedPolicyValue.Boolean(false), new ModuleManagedPolicyValue.Boolean(true)),
            ModulePolicyKeys.State(snapshot, declared, "AllowExample"));
        Assert.Equal(new ModuleManagedPolicyState(IsBlocked: true), ModulePolicyKeys.State(snapshot, declared, "ExampleMode"));
        Assert.Equal(new ModuleManagedPolicyState(new ModuleManagedPolicyValue.List(["a", "b"])), ModulePolicyKeys.State(snapshot, declared, "ExampleTargets"));
        Assert.Same(ModuleManagedPolicyState.Unmanaged, ModulePolicyKeys.State(snapshot, declared, "Unset"));
        // Host keys stay unmanaged for modules unless declared, and names compare exactly.
        Assert.Same(ModuleManagedPolicyState.Unmanaged, ModulePolicyKeys.State(snapshot, declared, ManagedPolicyKeys.AllowCloudTranscription));
        Assert.Same(ModuleManagedPolicyState.Unmanaged, ModulePolicyKeys.State(snapshot, declared, "allowexample"));
        Assert.True(ModulePolicyKeys.State(Unreadable(), declared, "Unset").IsBlocked);
        Assert.False(ModulePolicyKeys.State(Unreadable(), declared, "Undeclared").IsBlocked);
    }

    [Fact]
    public void PolicyReadingDeliversChangesThroughPostUntilDisposed()
    {
        Action<ManagedPolicySnapshot>? listeners = null;
        var posted = new List<Action>();
        var refreshed = 0;
        var reading = new ModuleManagedPolicyReading(() => Build(), () => refreshed++,
            listener => listeners += listener, listener => listeners -= listener, posted.Add, (stage, _) => Assert.Fail(stage));
        var calls = 0;

        Assert.Same(ModuleManagedPolicyState.Unmanaged, reading.State("AllowExample"));
        var subscription = reading.ObserveChanges(() => calls++);
        listeners!(ManagedPolicySnapshot.Empty);
        Assert.Equal(0, calls);
        posted.Single()();
        Assert.Equal(1, calls);

        listeners!(ManagedPolicySnapshot.Empty);
        subscription.Dispose();
        subscription.Dispose();
        posted[1]();
        Assert.Equal(1, calls);
        Assert.Null(listeners);
        reading.Refresh();
        Assert.Equal(1, refreshed);
    }

    [Fact]
    public void ObserversHearWhenDeclaredKeysBecomeReadable()
    {
        var reading = new ModuleManagedPolicyReading(() => Build(), () => { }, _ => { }, _ => { }, action => action(), (stage, _) => Assert.Fail(stage));
        var calls = 0;
        using var subscription = reading.ObserveChanges(() => calls++);

        // Host keys only: the policy itself does not change, but what the module may read does.
        reading.UseDeclaredNames(new HashSet<string>([ManagedPolicyKeys.AllowCloudTranscription]));
        Assert.Equal(1, calls);

        reading.UseDeclaredNames(new HashSet<string>());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AnObserverThatThrowsIsReported()
    {
        Action<ManagedPolicySnapshot>? listeners = null;
        var reported = new List<string>();
        var reading = new ModuleManagedPolicyReading(() => Build(), () => { },
            listener => listeners += listener, listener => listeners -= listener, action => action(), (stage, _) => reported.Add(stage));

        using var subscription = reading.ObserveChanges(() => throw new InvalidOperationException());
        listeners!(ManagedPolicySnapshot.Empty);

        Assert.Equal(["modules.policy.observer-failed"], reported);
    }

    [Fact]
    public void DiagnosticsTreatModuleKeysAsKnownAndKeepSensitiveValuesOut()
    {
        var reading = new ManagedPolicyReading(true, null, ManagedPolicyRawSection.Of(
            [
                KeyValuePair.Create("ExampleToken", ManagedPolicyRawValue.FromText("example-secret-value")),
                KeyValuePair.Create("ExampleChannel", ManagedPolicyRawValue.FromText("beta")),
                KeyValuePair.Create("ExampleSecretsConfigured", ManagedPolicyRawValue.FromNumber(1)),
                KeyValuePair.Create("ExampleMode", ManagedPolicyRawValue.FromNumber(7))
            ],
            [KeyValuePair.Create("ExampleSecrets", new ManagedPolicyRawList(["hidden-entry"]))]),
            ManagedPolicyRawSection.Missing);
        var (resolution, _) = Resolve(
            new("ExampleToken", ModuleManagedPolicyKind.Text, Sensitive: true),
            new("ExampleChannel", ModuleManagedPolicyKind.Text),
            new("ExampleSecrets", ModuleManagedPolicyKind.List, Sensitive: true),
            new("ExampleMode", ModuleManagedPolicyKind.Enumeration, EnumerationMaximum: 1));

        var policy = Report.SummarizePolicy(ManagedPolicyValidator.Validate(reading, 1, DateTimeOffset.UtcNow, resolution.Definitions), watching: true);

        Assert.Equal(["ExampleChannel", "ExampleSecrets", "ExampleToken"], policy.Enforced.Select(value => value.Name));
        Assert.Equal("beta", policy.Enforced[0].Value);
        Assert.Equal(["[set]"], policy.Enforced[1].Items);
        Assert.Equal("[set]", policy.Enforced[2].Value);
        Assert.Equal(["ExampleMode"], policy.InvalidEnforced);
        Assert.DoesNotContain(policy.Issues, issue => issue.Message.StartsWith("Unknown", StringComparison.Ordinal));
        var json = (new Report(DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, []) with { Policy = policy }).ToJson();
        Assert.DoesNotContain("example-secret-value", json);
        Assert.DoesNotContain("hidden-entry", json);
    }

    [Fact]
    public void DiagnosticsKeepSensitiveNumbersOut()
    {
        var reading = new ManagedPolicyReading(true, null, ManagedPolicyRawSection.Of(
            [
                KeyValuePair.Create("ExampleSwitch", ManagedPolicyRawValue.FromNumber(1)),
                KeyValuePair.Create("ExampleLevel", ManagedPolicyRawValue.FromNumber(2))
            ], []),
            ManagedPolicyRawSection.Missing);
        var (resolution, _) = Resolve(
            new("ExampleSwitch", ModuleManagedPolicyKind.Boolean, Sensitive: true),
            new("ExampleLevel", ModuleManagedPolicyKind.Enumeration, EnumerationMaximum: 3, Sensitive: true));

        var policy = Report.SummarizePolicy(ManagedPolicyValidator.Validate(reading, 1, DateTimeOffset.UtcNow, resolution.Definitions), watching: true);

        Assert.Equal(["ExampleLevel", "ExampleSwitch"], policy.Enforced.Select(value => value.Name));
        Assert.All(policy.Enforced, value => Assert.Equal("[set]", value.Value));
    }
}

public sealed class ModuleHistoryTests
{
    private static TranscriptionRecord Record(string id, string text = "text") => new()
    {
        Id = id, Timestamp = new DateTime(2026, 11, 2, 9, 30, 0, DateTimeKind.Utc), RawText = text, FinalText = text
    };

    [Fact]
    public void ChangesReportAddedReplacedAndRemovedRecords()
    {
        var changes = new ModuleHistoryChanges();
        var first = Record("a");
        var second = Record("b");
        var third = Record("c");
        changes.Reset([first, second, third]);

        Assert.Empty(changes.Update([first, second, third]));

        var replaced = second with { FinalText = "edited" };
        var update = changes.Update([first, replaced, Record("d")]);

        Assert.Equal(2, update.Count);
        Assert.Equal(["c"], Assert.IsType<ModuleHistoryChange.Removed>(update[0]).Ids.Order(StringComparer.Ordinal));
        Assert.Equal(["b", "d"], Assert.IsType<ModuleHistoryChange.Upserted>(update[1]).Ids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EmptyingHistoryIsReportedAsClearedOnce()
    {
        var changes = new ModuleHistoryChanges();
        changes.Reset([Record("a")]);

        Assert.IsType<ModuleHistoryChange.Cleared>(Assert.Single(changes.Update([])));
        Assert.Empty(changes.Update([]));
        Assert.Equal(["a"], Assert.IsType<ModuleHistoryChange.Upserted>(Assert.Single(changes.Update([Record("a")]))).Ids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WithoutABaselineEveryRecordIsNew()
    {
        var changes = new ModuleHistoryChanges();

        Assert.Equal(["a"], Assert.IsType<ModuleHistoryChange.Upserted>(Assert.Single(changes.Update([Record("a")]))).Ids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RecordsMapToTheModuleShape()
    {
        var record = Record("a", "raw") with
        {
            FinalText = "", SourceKind = null, Language = "de", DurationSeconds = 2.5, AppName = "Editor", AudioFileName = "a.wav"
        };

        var mapped = ModuleHistoryReading.ToModuleRecord(record, name => name == "a.wav" ? @"C:\audio\a.wav" : null);

        Assert.Equal(new ModuleHistoryRecord("a", new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero), "dictation", "raw", "de", 2.5, "Editor", @"C:\audio\a.wav"), mapped);
        Assert.Null(ModuleHistoryReading.ToModuleRecord(record with { AudioFileName = "gone.wav", SourceKind = "file" }, _ => null).AudioFilePath);
        Assert.Equal("file", ModuleHistoryReading.ToModuleRecord(record with { SourceKind = "file" }, _ => null).Source);
        Assert.Null(ModuleHistoryReading.ToModuleRecord(record with { AudioFileName = null }, _ => throw new InvalidOperationException()).AudioFilePath);
    }

    [Fact]
    public void TimestampsWithoutAKindAreUtc()
    {
        var unspecified = new DateTime(2026, 11, 2, 9, 30, 0, DateTimeKind.Unspecified);
        var local = new DateTime(2026, 11, 2, 9, 30, 0, DateTimeKind.Local);

        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero), ModuleHistoryReading.RecordedAt(unspecified));
        Assert.Equal(new DateTimeOffset(local), ModuleHistoryReading.RecordedAt(local));
        Assert.Equal(TimeSpan.Zero, ModuleHistoryReading.RecordedAt(local).Offset);
    }

    [Fact]
    public async Task RecordsLeaveOutUnfinishedOrFailedRecordsButTheyStillExist()
    {
        var history = new FakeHistory(
            Record("done"),
            Record("failed") with { Status = TranscriptionRecordStatus.WorkflowPostProcessingFailed },
            Record("importing") with { ProcessingState = "importing" },
            Record("synced") with { ProcessingState = "ready", SourceKind = "recording" });
        var reading = new ModuleHistoryReading(history, _ => null, _ => { }, action => action(), (stage, _) => Assert.Fail(stage));
        string[] ids = ["done", "failed", "importing", "synced", "missing"];

        var records = await reading.RecordsAsync(ids);
        var existing = await reading.ExistingRecordIdsAsync(ids);

        Assert.Equal(["done", "synced"], records.Select(record => record.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["done", "failed", "importing", "synced"], existing.Order(StringComparer.Ordinal));
        Assert.Empty(await reading.RecordsAsync([]));
        Assert.True(history.Loaded);
    }

    [Fact]
    public async Task ObserversReceiveChangesAfterTheFirstRead()
    {
        var first = Record("a");
        var history = new FakeHistory(first);
        var posted = new Queue<Action>();
        var reading = new ModuleHistoryReading(history, _ => null, _ => { }, posted.Enqueue, (stage, _) => Assert.Fail(stage));
        var received = new List<ModuleHistoryChange>();
        void RunPosted() { while (posted.TryDequeue(out var action)) action(); }

        var subscription = reading.ObserveChanges(received.Add);
        await Task.Yield();
        RunPosted();
        Assert.Equal(["a"], Assert.IsType<ModuleHistoryChange.Current>(Assert.Single(received)).Ids.Order(StringComparer.Ordinal));

        // Both saves are compared once, after the second.
        var second = Record("b");
        history.Replace(first, second);
        history.Replace(second);
        RunPosted();
        Assert.Equal(3, received.Count);
        Assert.Equal(["a"], Assert.IsType<ModuleHistoryChange.Removed>(received[1]).Ids.Order(StringComparer.Ordinal));
        Assert.Equal(["b"], Assert.IsType<ModuleHistoryChange.Upserted>(received[2]).Ids.Order(StringComparer.Ordinal));

        subscription.Dispose();
        history.Replace();
        RunPosted();
        Assert.Equal(3, received.Count);
    }

    [Fact]
    public async Task ASaveBeforeTheBaselineCallbackRunsIsStillDelivered()
    {
        var first = Record("a");
        var history = new FakeHistory(first);
        var posted = new Queue<Action>();
        var reading = new ModuleHistoryReading(history, _ => null, _ => { }, posted.Enqueue, (stage, _) => Assert.Fail(stage));
        var received = new List<ModuleHistoryChange>();

        reading.ObserveChanges(received.Add);
        await Task.Yield();
        // History is saved while the baseline callback still waits on the UI thread.
        history.Replace(first, Record("b"));
        while (posted.TryDequeue(out var action)) action();

        Assert.Equal(["a", "b"], Assert.IsType<ModuleHistoryChange.Current>(Assert.Single(received)).Ids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ASaveBeforeTheBaselineIsReadIsPartOfCurrent()
    {
        var first = Record("a");
        var history = new FakeHistory(first);
        var posted = new Queue<Action>();
        var reading = new ModuleHistoryReading(history, _ => null, _ => { }, posted.Enqueue, (stage, _) => Assert.Fail(stage));
        var received = new List<ModuleHistoryChange>();
        // The save lands after loading but before the baseline is read, so the baseline already holds it.
        history.DuringLoad = () => history.Replace(first, Record("b"));

        reading.ObserveChanges(received.Add);
        await Task.Yield();
        while (posted.TryDequeue(out var action)) action();

        Assert.Equal(["a", "b"], Assert.IsType<ModuleHistoryChange.Current>(Assert.Single(received)).Ids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RemovingEverythingBeforeTheBaselineGivesAnEmptyCurrent()
    {
        var history = new FakeHistory(Record("a"));
        var posted = new Queue<Action>();
        var reading = new ModuleHistoryReading(history, _ => null, _ => { }, posted.Enqueue, (stage, _) => Assert.Fail(stage));
        var received = new List<ModuleHistoryChange>();
        history.DuringLoad = () => history.Replace();

        reading.ObserveChanges(received.Add);
        await Task.Yield();
        while (posted.TryDequeue(out var action)) action();

        Assert.Empty(Assert.IsType<ModuleHistoryChange.Current>(Assert.Single(received)).Ids);
    }

    [Fact]
    public async Task ALaterObserverGetsCurrentBeforeAComparisonQueuedEarlier()
    {
        var first = Record("a");
        var history = new FakeHistory(first);
        var posted = new Queue<Action>();
        var reading = new ModuleHistoryReading(history, _ => null, _ => { }, posted.Enqueue, (stage, _) => Assert.Fail(stage));
        void RunPosted() { while (posted.TryDequeue(out var action)) action(); }
        reading.ObserveChanges(_ => { });
        await Task.Yield();
        RunPosted();

        // A save queues a comparison, then an observer joins before it runs.
        history.Replace(first, Record("b"));
        var late = new List<ModuleHistoryChange>();
        reading.ObserveChanges(late.Add);
        RunPosted();

        Assert.Equal(["a", "b"], Assert.IsType<ModuleHistoryChange.Current>(Assert.Single(late)).Ids.Order(StringComparer.Ordinal));
        history.Replace(first);
        RunPosted();
        Assert.Equal(["b"], Assert.IsType<ModuleHistoryChange.Removed>(late[1]).Ids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryObserverGetsCurrentOnce()
    {
        var history = new FakeHistory(Record("a"));
        var posted = new Queue<Action>();
        var reading = new ModuleHistoryReading(history, _ => null, _ => { }, posted.Enqueue, (stage, _) => Assert.Fail(stage));
        var early = new List<ModuleHistoryChange>();
        var late = new List<ModuleHistoryChange>();
        void RunPosted() { while (posted.TryDequeue(out var action)) action(); }

        reading.ObserveChanges(early.Add);
        await Task.Yield();
        // Registered before the baseline callback runs: it shares the first Current.
        var between = new List<ModuleHistoryChange>();
        reading.ObserveChanges(between.Add);
        RunPosted();
        reading.ObserveChanges(late.Add);
        RunPosted();

        Assert.IsType<ModuleHistoryChange.Current>(Assert.Single(early));
        Assert.IsType<ModuleHistoryChange.Current>(Assert.Single(between));
        Assert.Equal(["a"], Assert.IsType<ModuleHistoryChange.Current>(Assert.Single(late)).Ids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ShowRecordPassesTheIdentifierOn()
    {
        var shown = new List<string>();
        var reading = new ModuleHistoryReading(new FakeHistory(), _ => null, shown.Add, action => action(), (stage, _) => Assert.Fail(stage));

        reading.ShowRecord("a");
        reading.ShowRecord("");

        Assert.Equal(["a"], shown);
    }

    // Raises RecordsChanged like History: once per saved change, without comparing on its own.
    private sealed class FakeHistory(params TranscriptionRecord[] records) : IHistoryService
    {
        private TranscriptionRecord[] _records = records;
        public bool Loaded { get; private set; }
        public Action? DuringLoad { get; set; }
        public IReadOnlyList<TranscriptionRecord> Records => [.. _records];
        public event Action? RecordsChanged;
        public void Replace(params TranscriptionRecord[] records)
        {
            _records = records;
            RecordsChanged?.Invoke();
        }
        public Task EnsureLoadedAsync()
        {
            Loaded = true;
            DuringLoad?.Invoke();
            return Task.CompletedTask;
        }
        public void AddRecord(TranscriptionRecord record) => throw new NotSupportedException();
        public void UpdateRecord(string id, string finalText) => throw new NotSupportedException();
        public void DeleteRecord(string id) => throw new NotSupportedException();
        public void ClearAll() => throw new NotSupportedException();
        public IReadOnlyList<TranscriptionRecord> Search(string query) => throw new NotSupportedException();
        public void PurgeOldRecords(TimeSpan? retention) => throw new NotSupportedException();
        public int TotalRecords => _records.Length;
        public int TotalWords => 0;
        public double TotalDuration => 0;
        public IReadOnlyList<string> GetDistinctApps() => [];
        public string ExportToText(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null) => throw new NotSupportedException();
        public string ExportToCsv(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null) => throw new NotSupportedException();
        public string ExportToMarkdown(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null) => throw new NotSupportedException();
        public string ExportToJson(IReadOnlyList<TranscriptionRecord> records) => throw new NotSupportedException();
    }
}

public sealed class ModulePremiumAndUserDataTests
{
    [Theory]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(true, false, false, true, true, false)]
    [InlineData(false, true, true, true, false, true)]
    [InlineData(true, false, true, true, true, false)]
    public void PremiumRequirementsFollowTheHostsRules(bool commercial, bool premium, bool signedIn,
        bool any, bool meetsCommercial, bool meetsPremiumAccount)
    {
        var access = new PremiumAccessState(Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.txt"),
            () => new PremiumAccess(commercial, premium, signedIn));
        var reading = new ModulePremiumAccessReading(access, () => { }, action => action(), (stage, _) => Assert.Fail(stage));

        Assert.Equal(any, reading.HasPremiumAccess);
        Assert.Equal(any, reading.Meets(ModulePremiumRequirement.CommercialOrPremium));
        Assert.Equal(meetsCommercial, reading.Meets(ModulePremiumRequirement.Commercial));
        Assert.Equal(meetsPremiumAccount, reading.Meets(ModulePremiumRequirement.PremiumAccount));
        Assert.Throws<ArgumentOutOfRangeException>(() => reading.Meets((ModulePremiumRequirement)99));
    }

    [Fact]
    public void PremiumChangesReachObserversUntilDisposed()
    {
        var access = new PremiumAccessState(Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.txt"), () => new PremiumAccess());
        var shown = 0;
        var reading = new ModulePremiumAccessReading(access, () => shown++, action => action(), (stage, _) => Assert.Fail(stage));
        var calls = 0;

        var subscription = reading.ObserveChanges(() => calls++);
        access.NotifyActualAccessChanged();
        subscription.Dispose();
        access.NotifyActualAccessChanged();
        reading.ShowPremiumAccess();

        Assert.Equal(1, calls);
        Assert.Equal(1, shown);
    }

    [Theory]
    [InlineData("example", true)]
    [InlineData("Example.Module_2-b", true)]
    [InlineData(".hidden", true)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("trailing.", false)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData(@"a\b", false)]
    [InlineData("C:", false)]
    [InlineData("with space", false)]
    [InlineData("nul", false)]
    [InlineData("COM1.log", false)]
    [InlineData("console", true)]
    public void ModuleIdentifiersAreSafeFolderNames(string id, bool valid)
    {
        Assert.Equal(valid, ModuleUserDataLifecycle.IsValidId(id));
    }

    [Fact]
    public void DataFoldersAreCreatedInsideTheRoot()
    {
        var root = Path.Join(Path.GetTempPath(), "tw-mod-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var lifecycle = new ModuleUserDataLifecycle(root, (stage, _) => Assert.Fail(stage));

            var folder = lifecycle.DataFolder("example");

            Assert.Equal(Path.Join(root, "example"), folder);
            Assert.True(Directory.Exists(folder));
            Assert.Equal(folder, lifecycle.DataFolder("example"));
            Assert.Throws<ArgumentException>(() => lifecycle.DataFolder(".."));
            Assert.Throws<ArgumentException>(() => lifecycle.DataFolder(new string('a', 129)));
            Assert.Throws<ArgumentException>(() => lifecycle.Register(new ModuleUserData("../x", () => Task.CompletedTask)));
            Assert.Throws<ArgumentNullException>(() => lifecycle.Register(new ModuleUserData("x", null!)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EraseIsSettledWhenEveryRegistrationFinishesOrFails()
    {
        var lifecycle = new ModuleUserDataLifecycle(Path.GetTempPath(), (_, _) => { }, TimeSpan.FromSeconds(5));
        lifecycle.Register(new("done", () => Task.CompletedTask));
        lifecycle.Register(new("failed", () => throw new IOException()));

        // A failure ends the work; only work still running after its timeout leaves the deletion unsettled.
        Assert.True(await lifecycle.EraseAllAsync());
    }

    [Fact]
    public async Task EraseRunsEveryRegistrationOnceAndReportsFailures()
    {
        var reported = new List<string>();
        var lifecycle = new ModuleUserDataLifecycle(Path.GetTempPath(), (stage, _) => reported.Add(stage), TimeSpan.FromMilliseconds(50));
        var calls = new List<string>();
        var hang = new TaskCompletionSource();

        lifecycle.Register(new("first", () => { calls.Add("replaced"); return Task.CompletedTask; }));
        lifecycle.Register(new("second", () => throw new IOException()));
        lifecycle.Register(new("third", () => { calls.Add("third"); return hang.Task; }));
        lifecycle.Register(new("fourth", async () => { await Task.Yield(); calls.Add("fourth"); }));
        lifecycle.Register(new("first", () => { calls.Add("first"); return Task.CompletedTask; }));

        Assert.False(await lifecycle.EraseAllAsync());

        Assert.Equal(["first", "third", "fourth"], calls);
        Assert.Equal(["modules.user-data.erase-failed module=second", "modules.user-data.erase-failed module=third"], reported);        // Erase work that is still running cannot recreate a module folder once deletion has started.
        Assert.Throws<InvalidOperationException>(() => lifecycle.DataFolder("third"));
    }
}
