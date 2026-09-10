using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WorkflowShortcutCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "workflow-shortcuts-" + Guid.NewGuid());
    private string PathName => Path.Combine(_directory, "workflows.json");
    private static Workflow Draft(string id = "first", string chord = "CTRL+J") => new()
    {
        Id = id, Name = id, Template = WorkflowTemplate.Custom,
        Trigger = WorkflowTrigger.Hotkey([chord], WorkflowHotkeyBehavior.ProcessSelectedText),
        Behavior = new() { FineTuning = "Summarize accurately.", ProviderOverride = "provider", ModelOverride = "model" }
    };
    private void Seed(params Workflow[] workflows) => Assert.True(new WorkflowService(PathName).TryReplaceAll(workflows));
    private WorkflowShortcutCatalog Catalog(Backend backend, Func<IReadOnlyList<Workflow>, bool>? writer = null,
        Func<string, string?>? reserved = null) => new(new ManualWorkflowStore(PathName, writer), backend, reserved ?? (_ => null));

    [Fact]
    public void InitializeRegistersBothSupportedShortcutKindsAndPreservesDisk()
    {
        var supported = Draft();
        Seed(supported, Draft("disabled", "CTRL+K") with { IsEnabled = false },
            Draft("recording") with { Trigger = WorkflowTrigger.Hotkey(["CTRL+L"], WorkflowHotkeyBehavior.StartDictation) },
            Draft("action") with { Output = new() { TargetActionPluginId = "external" } });
        var bytes = File.ReadAllBytes(PathName);
        var backend = new Backend(); var catalog = Catalog(backend);
        Assert.Null(catalog.Initialize());
        Assert.Equal("CTRL+J,CTRL+L", backend.Value);
        Assert.Equal(WorkflowHotkeyBehavior.StartDictation, catalog.Resolve("CTRL+L")?.Trigger.HotkeyBehavior);
        Assert.Equal("first", catalog.Resolve("control+j")?.Id);
        Assert.Null(catalog.Resolve("CTRL+K"));
        Assert.Equal(bytes, File.ReadAllBytes(PathName));
    }

    [Fact]
    public void UnsupportedContextAndRecordingSemanticsAreNotReinterpreted()
    {
        Assert.False(ManualWorkflowStore.IsSelectedTextShortcut(Draft() with
        { Trigger = Draft().Trigger with { ContextMatchMode = (WorkflowContextMatchMode)99 } }));
        Assert.False(ManualWorkflowStore.IsSelectedTextShortcut(Draft() with
        { Trigger = Draft().Trigger with { ProcessNames = new[] { "editor" } } }));
        Assert.False(ManualWorkflowStore.IsSelectedTextShortcut(Draft() with
        { Behavior = Draft().Behavior with { SelectedTask = "translate" } }));
    }

    [Fact]
    public void CollisionAndReservedModifierPrefixAreRejectedBeforeRegistration()
    {
        Seed(Draft()); var backend = new Backend();
        var catalog = Catalog(backend, reserved: chord => ProcessingCancelShortcut.Conflicts(chord, "CTRL+SHIFT", true) ? "Dictation prefix" : null);
        Assert.Null(catalog.Initialize());
        var count = backend.Requests.Count;
        Assert.NotNull(catalog.ValidateDraft("other", "control+j", true));
        Assert.Equal("Dictation prefix", catalog.ValidateDraft("other", "CTRL+SHIFT+K", true));
        Assert.NotNull(catalog.ValidateDraft("other", "ALT+F4", true));
        Assert.NotNull(catalog.ValidateDraft("other", ", ,", true));
        Assert.Throws<InvalidOperationException>(() => catalog.Save(Draft("other")));
        Assert.Equal(count, backend.Requests.Count);
        Assert.Single(new ManualWorkflowStore(PathName).Read());
        Assert.NotNull(catalog.Conflict("CTRL", modifierOnly: true));
    }

    [Fact]
    public void DisabledDuplicateIsPersistedButCannotBeEnabledUntilOriginalDisabled()
    {
        Seed(Draft()); var backend = new Backend(); var catalog = Catalog(backend);
        Assert.Null(catalog.Initialize());
        Assert.Null(catalog.ValidateDraft("second", "CTRL+J", false));
        catalog.Save(Draft("second") with { IsEnabled = false });
        Assert.Throws<InvalidOperationException>(() => catalog.SetEnabled("second", true));
        Assert.False(new ManualWorkflowStore(PathName).Read().Single(w => w.Id == "second").IsEnabled);
        catalog.SetEnabled("first", false);
        Assert.Equal("", backend.Value); Assert.Null(catalog.Resolve("CTRL+J"));
        catalog.SetEnabled("second", true);
        Assert.Equal("second", catalog.Resolve("CTRL+J")?.Id);
        catalog.Delete("second");
        Assert.Equal("", backend.Value); Assert.Null(catalog.Resolve("CTRL+J"));
        Assert.Single(new ManualWorkflowStore(PathName).Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSaveRollsBackOrSuspendsEveryExecution(bool rollbackFails)
    {
        Seed(Draft()); var bytes = File.ReadAllBytes(PathName); var backend = new Backend();
        IReadOnlyList<Workflow>? attempted = null;
        var catalog = Catalog(backend, items => { attempted = items; return false; });
        Assert.Null(catalog.Initialize());
        if (rollbackFails) backend.Reject = value => value == "CTRL+J";
        Assert.Throws<InvalidOperationException>(() => catalog.Save(Draft(chord: "CTRL+K")));
        Assert.Equal("CTRL+K", Assert.Single(attempted!).Trigger.Hotkeys.Single());
        Assert.Equal(bytes, File.ReadAllBytes(PathName));
        Assert.Null(catalog.Resolve("CTRL+K"));
        if (rollbackFails) { Assert.Null(catalog.Resolve("CTRL+J")); Assert.Contains("suspended", catalog.Error); }
        else { Assert.Equal("CTRL+J", backend.Value); Assert.Equal("first", catalog.Resolve("CTRL+J")?.Id); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedDeleteOrDisableKeepsDiskAndExecutableRegistration(bool delete)
    {
        Seed(Draft()); var bytes = File.ReadAllBytes(PathName); var backend = new Backend();
        var catalog = Catalog(backend, _ => false); Assert.Null(catalog.Initialize());
        Assert.Throws<InvalidOperationException>(() => { if (delete) catalog.Delete("first"); else catalog.SetEnabled("first", false); });
        Assert.Equal(bytes, File.ReadAllBytes(PathName));
        Assert.Equal("CTRL+J", backend.Value); Assert.NotNull(catalog.Resolve("CTRL+J"));
    }

    [Fact]
    public void PublishedAndResolvedSnapshotsDoNotShareMutableCollections()
    {
        var backend = new Backend(); var catalog = Catalog(backend);
        var keys = new[] { "CTRL+J" }; var settings = new Dictionary<string, string>();
        var draft = Draft() with { Trigger = Draft().Trigger with { Hotkeys = keys },
            Behavior = Draft().Behavior with { Settings = settings } };
        catalog.Save(draft); keys[0] = "CTRL+K"; settings["future"] = "unsupported";
        var resolved = Assert.IsType<Workflow>(catalog.Resolve("CTRL+J"));
        Assert.Equal("CTRL+J", Assert.Single(resolved.Trigger.Hotkeys)); Assert.Empty(resolved.Behavior.Settings);
        ((string[])resolved.Trigger.Hotkeys)[0] = "CTRL+L";
        ((Dictionary<string, string>)resolved.Behavior.Settings)["external"] = "mutation";
        var again = Assert.IsType<Workflow>(catalog.Resolve("CTRL+J"));
        Assert.Equal("CTRL+J", Assert.Single(again.Trigger.Hotkeys)); Assert.Empty(again.Behavior.Settings);
    }

    [Fact]
    public void PartialNativeFailureAndFailedRollbackSuspendOldCallbacksWithoutSaving()
    {
        Seed(Draft()); var bytes = File.ReadAllBytes(PathName); var backend = new Backend(); var catalog = Catalog(backend);
        Assert.Null(catalog.Initialize()); backend.PartialFailure = true;
        Assert.Throws<InvalidOperationException>(() => catalog.Save(Draft(chord: "CTRL+K")));
        Assert.Null(catalog.Resolve("CTRL+J")); Assert.Null(catalog.Resolve("CTRL+K"));
        Assert.Contains("suspended", catalog.Error); Assert.Equal(bytes, File.ReadAllBytes(PathName));
    }

    [Fact]
    public void ThrowingRollbackSuspendsEveryCallbackAndPreservesStoredWorkflow()
    {
        Seed(Draft()); var bytes = File.ReadAllBytes(PathName); var backend = new Backend();
        var catalog = Catalog(backend, _ => false); Assert.Null(catalog.Initialize());
        backend.ThrowOnChange = value => value == "CTRL+J";
        Assert.Throws<InvalidOperationException>(() => catalog.Save(Draft(chord: "CTRL+K")));
        Assert.Null(catalog.Resolve("CTRL+J")); Assert.Null(catalog.Resolve("CTRL+K"));
        Assert.Contains("suspended", catalog.Error); Assert.Equal(bytes, File.ReadAllBytes(PathName));
    }

    [Fact]
    public void ReorderedNativeReadbackIsAcceptedAndRestartRebuildsSavedBindings()
    {
        var backend = new Backend { Reverse = true }; var catalog = Catalog(backend);
        catalog.Save(Draft() with { Trigger = WorkflowTrigger.Hotkey(["CTRL+J", "CTRL+K"], WorkflowHotkeyBehavior.ProcessSelectedText) });
        Assert.Equal("CTRL+K,CTRL+J", backend.Value);
        var restarted = Catalog(new Backend()); Assert.Null(restarted.Initialize());
        Assert.Equal("first", restarted.Resolve("CTRL+K")?.Id);
    }

    private sealed class Backend : IProcessingCancelShortcutBackend
    {
        public string Value { get; private set; } = "";
        public List<string> Requests { get; } = [];
        public Func<string, bool>? Reject { get; set; }
        public Func<string, bool>? ThrowOnChange { get; set; }
        public bool PartialFailure { get; set; }
        public bool Reverse { get; set; }
        public string? TryChange(string value)
        {
            Requests.Add(value);
            if (ThrowOnChange?.Invoke(value) == true) throw new InvalidOperationException("Native rollback threw");
            if (PartialFailure) { Value = "CTRL+Z"; return "Partial native failure"; }
            if (Reject?.Invoke(value) == true) return "Registration unavailable";
            Value = Reverse ? string.Join(",", value.Split(',').Reverse()) : value;
            return null;
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
