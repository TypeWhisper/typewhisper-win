using System.Reflection;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.ModuleKit;
using TypeWhisper.Modules;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

// The contribution points as the host binds them, with test contributions instead of real modules. Content factories
// are never called here, because they need a running WinUI app.
public sealed class ModuleContributionPointsTests : IDisposable
{
    private static readonly Assembly StubAssembly = typeof(ModuleEntry).Assembly;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "module-points-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _reports = [];
    private readonly List<(string Stage, Exception Error)> _failures = [];

    public ModuleContributionPointsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void EveryContributionPointIsCombinedAcrossModulesAndAThrowingModuleAddsNothing()
    {
        var first = new TestModule("first", (_, contributions) => FillEveryPoint(contributions, "first"));
        var broken = new TestModule("broken", (_, contributions) =>
        {
            FillEveryPoint(contributions, "broken");
            throw new InvalidOperationException("module failure");
        });
        var second = new TestModule("second", (_, contributions) => FillEveryPoint(contributions, "second"));

        var modules = HostModules.Load(new TestHost(), StubAssembly, ModuleKitVersion.Current, () => [first, broken, second],
            (stage, error) => _failures.Add((stage, error)), _reports.Add);

        Assert.Equal([first, second], modules.Modules);
        Assert.Equal("modules.contribute.failed", Assert.Single(_failures).Stage);
        Assert.Empty(_reports);
        var contributions = modules.Contributions;
        Assert.Equal(["FirstKey", "SecondKey"], contributions.ManagedPolicyKeys.Select(item => item.Name));
        Assert.Equal(["first", "second"], contributions.HotkeyActions.Select(item => item.Id));
        Assert.Equal(["first", "second"], contributions.MenuItems.Select(item => item.Id));
        Assert.Equal(["first", "second"], contributions.Panels.Select(item => item.Id));
        Assert.Equal(["first", "second"], contributions.RecordActions.Select(item => item.Id));
        Assert.Equal(["first", "second"], contributions.PremiumCards.Select(item => item.Id));
        Assert.Equal(["first", "second"], contributions.SettingsSections.Select(item => item.Id));
        Assert.Equal(["first", "second"], contributions.FileTranscriptionOptions.Select(item => item.Id));
        Assert.Equal(["first", "second"], contributions.FileTranscriptionCompleted.Select(item => item.Id));
        var catalog = modules.Catalog;
        Assert.Equal(["first", "second"], catalog.HotkeyActions.Select(item => item.Id));
        Assert.Equal(["first", "second"], catalog.MenuItems.Select(item => item.Id));
        Assert.Equal(["first", "second"], catalog.Panels.Select(item => item.Id));
        Assert.Equal(["first", "second"], catalog.RecordActions.Select(item => item.Id));
        Assert.Equal(["first", "second"], catalog.PremiumCards.Select(item => item.Id));
        Assert.Equal(["first", "second"], catalog.SectionsFor(ModuleSettingsPage.Advanced).Select(item => item.Id));
        Assert.Empty(catalog.SectionsFor(ModuleSettingsPage.Premium));
        Assert.Equal(["first", "second"], catalog.FileTranscriptionOptions.Select(item => item.Id));
        Assert.Equal(["first", "second"], catalog.FileTranscriptionHooks.Select(item => item.Id));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("Example.Action-1_b", true)]
    [InlineData("..", true)]
    [InlineData("", false)]
    [InlineData("with space", false)]
    [InlineData("slash/inside", false)]
    [InlineData("colon:inside", false)]
    [InlineData("umlaut-ä", false)]
    [InlineData("line\n", false)]
    [InlineData(null, false)]
    public void IdentifiersAreAsciiLettersDigitsDotsHyphensAndUnderscores(string? id, bool valid) =>
        Assert.Equal(valid, ModuleContributionCatalog.IsValidId(id));

    [Fact]
    public void IdentifiersHaveAtMost128Characters()
    {
        Assert.True(ModuleContributionCatalog.IsValidId(new string('a', 128)));
        Assert.False(ModuleContributionCatalog.IsValidId(new string('a', 129)));
    }

    [Fact]
    public void InvalidDuplicateAndIncompleteContributionsAreLeftOutAndReported()
    {
        var contributions = new ModuleContributions();
        contributions.HotkeyActions.Add(Hotkey("open"));
        contributions.HotkeyActions.Add(Hotkey("OPEN"));
        contributions.HotkeyActions.Add(Hotkey("bad id"));
        contributions.HotkeyActions.Add(new ModuleHotkeyAction("untitled", " ", () => { }));
        // The same identifier in another kind is no duplicate.
        contributions.RecordActions.Add(RecordAction("open"));
        contributions.RecordActions.Add(RecordAction("none") with { MaximumRecordCount = 0 });
        contributions.MenuItems.Add(new ModuleMenuItem("menu", "Menu", () => { }, HotkeyActionId: "missing"));
        contributions.MenuItems.Add(new ModuleMenuItem("bound", "Bound", () => { }, HotkeyActionId: "Open"));
        contributions.Panels.Add(null!);

        var catalog = ModuleContributionCatalog.Resolve(contributions, _reports.Add);

        Assert.Equal(["open"], catalog.HotkeyActions.Select(item => item.Id));
        Assert.Equal(["open"], catalog.RecordActions.Select(item => item.Id));
        Assert.Equal(new string?[] { null, "Open" }, catalog.MenuItems.Select(item => item.HotkeyActionId));
        Assert.Empty(catalog.Panels);
        Assert.Equal(
        [
            "modules.contribution.rejected kind=hotkey-action id=OPEN reason=duplicate",
            "modules.contribution.rejected kind=hotkey-action id=untitled reason=incomplete",
            "modules.contribution.rejected kind=hotkey-action reason=invalid-id",
            "modules.contribution.rejected kind=menu-item id=menu reason=unknown-hotkey-action",
            "modules.contribution.rejected kind=panel reason=missing",
            "modules.contribution.rejected kind=record-action id=none reason=incomplete"
        ], _reports.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void HotkeyActionsBeyondTheIdRangesAreLeftOut()
    {
        var contributions = new ModuleContributions();
        for (var index = 0; index <= ModuleHotkeys.MaximumCount; index++) contributions.HotkeyActions.Add(Hotkey("action" + index));

        var catalog = ModuleContributionCatalog.Resolve(contributions, _reports.Add);

        Assert.Equal(ModuleHotkeys.MaximumCount, catalog.HotkeyActions.Count);
        Assert.Equal([$"modules.contribution.rejected kind=hotkey-action id=action{ModuleHotkeys.MaximumCount} reason=limit"], _reports);
    }

    [Theory]
    [InlineData(null, 1, 1, true, true, "One")]
    [InlineData(null, 1, 2, false, false, "One")]
    [InlineData("Several", 1, 2, true, false, "Several")]
    [InlineData("Several", 3, 3, true, true, "Several")]
    [InlineData("Several", 3, 4, true, false, "Several")]
    [InlineData("Several", 3, 0, false, false, "One")]
    public void RecordActionsFollowTheNumberOfChosenRecords(string? selectionTitle, int maximum, int count, bool visible, bool enabled, string title)
    {
        var action = RecordAction("act") with { Title = "One", SelectionTitle = selectionTitle, MaximumRecordCount = maximum };

        var state = ModuleRecordActionState.For(action, count);

        Assert.Equal((visible, enabled, title), (state.Visible, state.Enabled, state.Title));
        Assert.Equal(visible && !enabled, state.Help is not null);
    }

    [Fact]
    public void ADisabledRecordActionExplainsItsLimit()
    {
        var own = RecordAction("act") with { SelectionTitle = "Several", MaximumRecordCount = 2, SelectionLimitHelp = "Two at most." };
        Assert.Equal("Two at most.", ModuleRecordActionState.For(own, 3).Help);
        Assert.Equal("Select at most 2 entries.", ModuleRecordActionState.For(own with { SelectionLimitHelp = null }, 3).Help);
        Assert.Equal("Select only one entry.", ModuleRecordActionState.For(own with { SelectionLimitHelp = null, MaximumRecordCount = 1 }, 2).Help);
    }

    [Fact]
    public void RecordActionsRunWithTheChosenIdsAndReportFailures()
    {
        IReadOnlyList<string>? received = null;
        var action = RecordAction("act") with { SelectionTitle = "Several", MaximumRecordCount = 2, Perform = ids => received = ids };
        ModuleRecordActionState.Perform(action, ["a", "b"], Report);
        Assert.Equal(["a", "b"], received);

        received = null;
        ModuleRecordActionState.Perform(action, ["a", "b", "c"], Report);
        Assert.Null(received);

        ModuleRecordActionState.Perform(action with { Perform = _ => throw new InvalidOperationException() }, ["a"], Report);
        Assert.Equal("modules.record-action.failed id=act", Assert.Single(_failures).Stage);
    }

    [Fact]
    public void HotkeyActionsMapToTheirOwnSettingsKeyFileAndIdRange()
    {
        Assert.Equal("ModuleHotkeys:example.open", ModuleHotkeys.SettingsKey("example.open"));
        Assert.True(ModuleHotkeys.TryActionId(ModuleHotkeys.SettingsKey("example.open"), out var id));
        Assert.Equal("example.open", id);
        Assert.False(ModuleHotkeys.TryActionId(GlobalShortcuts.Recorder, out _));
        Assert.Equal("module-example.open-hotkeys.txt", ModuleHotkeys.FileName("example.open"));

        var bases = Enumerable.Range(0, ModuleHotkeys.MaximumCount).Select(ModuleHotkeys.IdBase).ToArray();
        // The host's last shortcut range starts at 0x8A00 and is 0x200 wide; RegisterHotKey takes ids below 0xC000.
        Assert.True(bases[0] >= 0x8A00 + 0x200);
        Assert.True(bases[^1] + ModuleHotkeys.IdSpacing <= 0xC000);
        Assert.Equal(bases.Length, bases.Distinct().Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => ModuleHotkeys.IdBase(ModuleHotkeys.MaximumCount));
    }

    [Fact]
    public void AModuleShortcutIsSavedUnderItsOwnFileAndLoadedAgain()
    {
        var path = Path.Combine(_root, ModuleHotkeys.FileName("example.open"));
        var backend = new FakeBackend();
        Assert.Null(new PersistedShortcut(path, backend, _ => null, "Example shortcuts").Save("CTRL+ALT+K"));
        Assert.Equal("CTRL+ALT+K", File.ReadAllText(path));

        var restored = new FakeBackend();
        Assert.Null(new PersistedShortcut(path, restored, _ => null, "Example shortcuts").Initialize());
        Assert.Equal("CTRL+ALT+K", restored.Value);
    }

    [Fact]
    public void ModuleShortcutsTakePartInTheGlobalConflictCheck()
    {
        var key = ModuleHotkeys.SettingsKey("example.open");
        GlobalShortcut[] modules = [new(key, GlobalShortcutRole.Action, "Open example")];
        var registered = new Dictionary<string, string> { [GlobalShortcuts.Recorder] = "CTRL+ALT+R", [key] = "CTRL+ALT+K" };

        Assert.Equal("Already used by Recorder. Change that shortcut first.",
            GlobalShortcuts.FindConflict(key, "CTRL+ALT+R", value => registered.GetValueOrDefault(value, ""), modules));
        Assert.Equal("Already used by Open example. Change that shortcut first.",
            GlobalShortcuts.FindConflict(GlobalShortcuts.Recorder, "CTRL+ALT+K", value => registered.GetValueOrDefault(value, ""), modules));
        Assert.Null(GlobalShortcuts.FindConflict(key, "CTRL+ALT+J", value => registered.GetValueOrDefault(value, ""), modules));
    }

    [Fact]
    public async Task AFileResultSavedToHistoryReachesHooksWithItsRecordSourceAndSegments()
    {
        var history = new HistoryService(Path.Combine(_root, "history.json"));
        await history.EnsureLoadedAsync();
        var record = new TranscriptionRecord
        {
            Id = "record-1", Timestamp = DateTime.UtcNow, RawText = "Hello there", FinalText = "Hello there.", DurationSeconds = 3.5,
            SourceKind = "file", EngineUsed = "provider", ModelUsed = "model"
        };
        var output = new FileTranscriptionOutput("Hello there.", "provider", "model", 3.5,
            [new TranscriptionSegment("Hello", 0, 1.25), new TranscriptionSegment("there", 1.25, 3.5)])
        { Language = "en", PendingHistory = record };

        Assert.Null(FileTranscriptionAcceptance.Commit(output, history, new() { SaveToHistory = true }, _ => null, out var saved));
        var transcription = ModuleFileTranscriptions.Map(output, @"C:\Media\talk.wav", saved);

        Assert.Equal("record-1", transcription.RecordId);
        Assert.Equal(@"C:\Media\talk.wav", transcription.SourcePath);
        Assert.Equal("Hello there.", transcription.Text);
        Assert.Equal("en", transcription.Language);
        Assert.Equal(3.5, transcription.DurationSeconds);
        Assert.Equal([new ModuleTimedText("Hello", 0, 1.25), new ModuleTimedText("there", 1.25, 3.5)], transcription.Segments);
    }

    [Fact]
    public async Task AFileResultWithoutHistoryHasNoRecordId()
    {
        var history = new HistoryService(Path.Combine(_root, "history.json"));
        await history.EnsureLoadedAsync();
        var output = new FileTranscriptionOutput("Text", "provider", "model", 1, []) { Language = " " };

        Assert.Null(FileTranscriptionAcceptance.Commit(output, history, new() { SaveToHistory = false }, _ => null, out var saved));
        var transcription = ModuleFileTranscriptions.Map(output, "talk.wav", saved);

        Assert.Null(transcription.RecordId);
        Assert.Null(transcription.Language);
        Assert.Empty(transcription.Segments);
    }

    [Fact]
    public void EveryFileTranscriptionHookRunsAfterOneFails()
    {
        var received = new List<string>();
        var transcription = new ModuleFileTranscription(null, "talk.wav", "Text", null, 1, []);
        ModuleFileTranscriptionHook[] hooks =
        [
            new("failing", _ => throw new InvalidOperationException()),
            new("working", value => received.Add(value.Text))
        ];

        ModuleFileTranscriptions.Notify(hooks, transcription, Report);

        Assert.Equal(["Text"], received);
        Assert.Equal("modules.file-transcription-hook.failed id=failing", Assert.Single(_failures).Stage);
    }

    [Fact]
    public void ModuleCallsReportFailuresWithoutThrowing()
    {
        Assert.False(ModuleCalls.Run("menu-item", "item", () => throw new InvalidOperationException(), Report));
        Assert.Null(ModuleCalls.Get<string>("premium-card.status", "bad id", () => throw new InvalidOperationException(), Report));
        Assert.True(ModuleCalls.Run("menu-item", "item", () => { }, Report));
        Assert.Equal("value", ModuleCalls.Get("premium-card.status", "card", () => "value", Report));
        Assert.Equal(["modules.menu-item.failed id=item", "modules.premium-card.status.failed"], _failures.Select(failure => failure.Stage));
    }

    [Fact]
    public void APanelCreatesItsWindowOnTheFirstShowOnlyAndTogglesIt()
    {
        var events = new List<string>();
        var panel = Panel("notes") with { DidShow = () => events.Add("shown"), DidHide = () => events.Add("hidden") };
        var opened = new List<FakeSurface>();
        var panels = new ModulePanels([panel], (_, hidden) => { var surface = new FakeSurface(hidden); opened.Add(surface); return surface; }, _reports.Add, Report);

        Assert.False(panels.IsVisible("notes"));
        Assert.Empty(opened);
        panels.Toggle("notes");
        Assert.True(panels.IsVisible("NOTES"));
        panels.Show("notes");
        panels.Toggle("notes");
        Assert.False(panels.IsVisible("notes"));
        panels.Hide("notes");
        panels.Show("notes");

        Assert.Single(opened);
        Assert.Equal(["shown", "hidden", "shown"], events);
        Assert.Empty(_reports);
    }

    [Fact]
    public void APanelClosedByTheUserReportsItsHideAndShowsAgain()
    {
        var events = new List<string>();
        FakeSurface? surface = null;
        var panels = new ModulePanels([Panel("notes") with { DidHide = () => events.Add("hidden") }],
            (_, hidden) => surface = new FakeSurface(hidden), _reports.Add, Report);
        panels.Show("notes");

        surface!.CloseByUser();

        Assert.False(panels.IsVisible("notes"));
        Assert.Equal(["hidden"], events);
        panels.Show("notes");
        Assert.True(panels.IsVisible("notes"));
    }

    [Fact]
    public void APanelWhoseContentFailsStaysHiddenAndTheNextShowTriesAgain()
    {
        var attempts = 0;
        var panels = new ModulePanels([Panel("notes")], (_, hidden) => ++attempts == 1 ? null : new FakeSurface(hidden), _reports.Add, Report);

        panels.Show("notes");
        Assert.False(panels.IsVisible("notes"));
        panels.Show("notes");

        Assert.True(panels.IsVisible("notes"));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void PanelCallbacksThatThrowAreReportedAndUnknownPanelsIgnored()
    {
        var panel = Panel("notes") with { DidShow = () => throw new InvalidOperationException(), DidHide = () => throw new InvalidOperationException() };
        var panels = new ModulePanels([panel], (_, hidden) => new FakeSurface(hidden), _reports.Add, Report);

        panels.Show("notes");
        panels.Hide("notes");
        panels.Show("missing");
        panels.Toggle("bad id");

        Assert.False(panels.IsVisible("missing"));
        Assert.Equal(["modules.panel.did-show.failed id=notes", "modules.panel.did-hide.failed id=notes"], _failures.Select(failure => failure.Stage));
        Assert.Equal(["modules.panel.unknown id=missing", "modules.panel.unknown"], _reports);
    }

    [Fact]
    public void ClosedPanelsIgnoreLaterRequests()
    {
        var opened = new List<FakeSurface>();
        var panels = new ModulePanels([Panel("notes")], (_, hidden) => { var surface = new FakeSurface(hidden); opened.Add(surface); return surface; }, _reports.Add, Report);
        panels.Show("notes");

        panels.CloseAll();
        panels.Show("notes");

        Assert.True(Assert.Single(opened).Closed);
        Assert.False(panels.IsVisible("notes"));
    }

    private void Report(string stage, Exception error) => _failures.Add((stage, error));

    private static void FillEveryPoint(ModuleContributions contributions, string id)
    {
        contributions.ManagedPolicyKeys.Add(new(char.ToUpperInvariant(id[0]) + id[1..] + "Key", ModuleManagedPolicyKind.Boolean));
        contributions.HotkeyActions.Add(Hotkey(id));
        contributions.MenuItems.Add(new ModuleMenuItem(id, "Menu", () => { }, "\uE8A5", id));
        contributions.Panels.Add(Panel(id));
        contributions.RecordActions.Add(RecordAction(id));
        contributions.PremiumCards.Add(new ModulePremiumCard(id, "Card", "Summary", "\uE8A5"));
        contributions.SettingsSections.Add(new ModuleSettingsSection(id, ModuleSettingsPage.Advanced, "Section", () => throw new InvalidOperationException("never created")));
        contributions.FileTranscriptionOptions.Add(new ModuleFileTranscriptionOption(id, () => throw new InvalidOperationException("never created")));
        contributions.FileTranscriptionCompleted.Add(new ModuleFileTranscriptionHook(id, _ => { }));
    }

    private static ModuleHotkeyAction Hotkey(string id) => new(id, "Action " + id, () => { });

    private static ModuleRecordAction RecordAction(string id) => new(id, "Act", "\uE8A5", _ => { });

    private static ModulePanel Panel(string id) => new(id, "Panel", () => throw new InvalidOperationException("never created"));

    private sealed class FakeSurface(Action hiddenByUser) : IModulePanelSurface
    {
        public bool IsVisible { get; private set; }
        public bool Closed { get; private set; }
        public void Show() => IsVisible = !Closed;
        public void Hide() => IsVisible = false;
        public void Close() { Closed = true; IsVisible = false; }

        public void CloseByUser()
        {
            IsVisible = false;
            hiddenByUser();
        }
    }

    private sealed class FakeBackend : IShortcutRegistrationBackend
    {
        public string Value { get; private set; } = "";

        public string? TryChange(string value)
        {
            Value = value;
            return null;
        }
    }

    private sealed class TestHost : IModuleHost
    {
        public string HostVersion => "0.0.1-test";
        public string InterfaceLanguage => "en";
        public IHistoryReading? HistoryReading => null;
        public IManagedPolicyReading? ManagedPolicyReading => null;
        public IPremiumAccessReading? PremiumAccessReading => null;
        public IUserDataLifecycle? UserDataLifecycle => null;
        public void ShowPanel(string id) { }
        public void HidePanel(string id) { }
        public void TogglePanel(string id) { }
        public bool IsPanelVisible(string id) => false;
    }

    private sealed class TestModule(string id, Action<IModuleHost, ModuleContributions> contribute) : IHostModule
    {
        public string Id => id;
        public void Contribute(IModuleHost host, ModuleContributions contributions) => contribute(host, contributions);
    }
}
