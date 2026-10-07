using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WorkflowSpokenLanguageTests
{
    private static readonly string[] Choices = ["de", "en", "fr"];

    private static Workflow Dictation(string? language, WorkflowTrigger? trigger = null) => new()
    {
        Id = "spoken-language", Name = "Spoken language", Template = WorkflowTemplate.Dictation,
        Trigger = trigger ?? WorkflowTrigger.Hotkey(["CTRL+J"], WorkflowHotkeyBehavior.StartDictation),
        Behavior = new() { InputLanguage = language }
    };

    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData(" ", "en")]
    [InlineData("global", "en")]
    [InlineData("inherit_global", "en")]
    [InlineData("auto", "auto")]
    [InlineData("de", "de")]
    [InlineData("fr", "fr")]
    public void ShortcutUsesExplicitLanguageOrCapturedGlobal(string? language, string expected)
    {
        var snapshot = AutomaticWorkflowSnapshot.ForDictationShortcut(Dictation(language));
        Assert.Equal(expected, WorkflowSpokenLanguage.Resolve(WorkflowSpokenLanguage.SelectedLanguageFor(snapshot), "en", Choices));
    }

    [Fact]
    public void LanguageMatchesTheModelsSpelling() =>
        Assert.Equal("zh-Hans", WorkflowSpokenLanguage.Resolve("zh-hans", "en", ["en", "zh-Hans"]));

    [Fact]
    public void LanguageTheModelCannotUseIsRejected()
    {
        var error = Assert.Throws<NotSupportedException>(() => WorkflowSpokenLanguage.Resolve("ja", "en", Choices));
        Assert.Contains("does not support this workflow's spoken language", error.Message);
        // A provider without a language list only detects automatically.
        Assert.Throws<NotSupportedException>(() => WorkflowSpokenLanguage.Resolve("de", "auto", []));
        Assert.Equal("auto", WorkflowSpokenLanguage.Resolve("auto", "auto", []));
    }

    [Theory]
    [InlineData("D")]
    [InlineData("d")]
    [InlineData("de_DE")]
    [InlineData("1de")]
    [InlineData("averyveryverylongcode")]
    public void MalformedLanguageIsRejectedOrFallsBackToTranscriptReview(string language)
    {
        Assert.False(ManualWorkflowStore.IsDictationShortcut(Dictation(language)));
        Assert.Throws<InvalidOperationException>(() => AutomaticWorkflowSnapshot.ForDictationShortcut(Dictation(language)));
        Assert.Throws<InvalidOperationException>(() => WorkflowSpokenLanguage.Resolve(language, "en", Choices));
        // An unsupported automatic rule still records for transcript review, using the global language.
        var snapshot = AutomaticWorkflowSnapshot.Select([Dictation(language, WorkflowTrigger.Global())], "editor")!;
        Assert.NotNull(snapshot.Error);
        Assert.Null(WorkflowSpokenLanguage.SelectedLanguageFor(snapshot));
    }

    [Fact]
    public void AutomaticRulesApplyTheirLanguageByPrecedence()
    {
        var global = Dictation("en", WorkflowTrigger.Global()) with { Id = "fallback" };
        var app = Dictation("de", WorkflowTrigger.App("editor")) with { Id = "app" };
        var website = Dictation("fr", WorkflowTrigger.Website("example.com")) with { Id = "website" };
        Workflow[] rules = [global, app, website];
        Assert.Equal("de", WorkflowSpokenLanguage.SelectedLanguageFor(AutomaticWorkflowSnapshot.Select(rules, "editor")));
        Assert.Equal("fr", WorkflowSpokenLanguage.SelectedLanguageFor(AutomaticWorkflowSnapshot.Select(rules, "editor", "example.com")));
        Assert.Equal("en", WorkflowSpokenLanguage.SelectedLanguageFor(AutomaticWorkflowSnapshot.Select(rules, "other")));
        Assert.True(ManualWorkflowStore.IsEditable(app));
    }

    [Fact]
    public void LanguageHintsRemainUnsupported()
    {
        var workflow = Dictation(null) with { Behavior = new() { InputLanguageHints = ["de", "en"] } };
        Assert.False(ManualWorkflowStore.IsDictationShortcut(workflow));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    [InlineData("de")]
    public void EditorAndStorageRoundTripLanguageWithoutChangingOtherSettings(string? language)
    {
        var directory = Path.Combine(Path.GetTempPath(), "workflow-language-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ManualWorkflowStore(Path.Combine(directory, "workflows.json"));
            var workflow = Dictation(null) with { Behavior = new() { SelectedTask = "transcribe" } };
            var draft = WorkflowDraft.FromStored(workflow) with { InputLanguage = language };
            store.Save(draft.ToStored(), allowAutomatic: true);
            var restored = WorkflowDraft.FromStored(Assert.Single(store.Read()));
            Assert.Equal(language, restored.InputLanguage);
            Assert.Equal("transcribe", restored.SelectedTask);
            Assert.Equal("CTRL+J", restored.Hotkeys);
            Assert.Equal(WorkflowHotkeyBehavior.StartDictation, restored.HotkeyBehavior);
            var snapshot = AutomaticWorkflowSnapshot.ForDictationShortcut(restored.ToStored());
            store.Save((restored with { InputLanguage = language == "de" ? "fr" : "de" }).ToStored(), allowAutomatic: true);
            Assert.Equal(language, snapshot.InputLanguage);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void InheritedMacValuesOpenAsGlobalSetting() =>
        Assert.Null(WorkflowDraft.FromStored(Dictation("inherit_global")).InputLanguage);
}
