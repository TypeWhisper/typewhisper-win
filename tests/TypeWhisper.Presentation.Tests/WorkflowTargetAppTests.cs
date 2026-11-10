using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

// A dictation workflow can send its text to another app than the one in front (#764).
public sealed class WorkflowTargetAppTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "typewhisper-target-app-" + Guid.NewGuid());
    private string FilePath => Path.Join(_directory, "workflows.json");

    private static Workflow Rule(WorkflowTrigger trigger, string? targetApp) => new()
    {
        Id = "chat", Name = "Chat", Template = WorkflowTemplate.Dictation, Trigger = trigger,
        Output = new WorkflowOutput { TargetAppProcessName = targetApp }
    };

    [Theory]
    [InlineData("discord", true)]
    [InlineData(" Discord ", true)]
    [InlineData("element-desktop", true)]
    [InlineData("discord.exe", false)]
    [InlineData(@"C:\Apps\discord", false)]
    [InlineData("discord, slack", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void OnlyProcessNamesWithoutPathOrExtensionAreValid(string? value, bool valid) =>
        Assert.Equal(valid, WorkflowTargetApp.IsValid(value));

    [Fact]
    public void LongNamesAreInvalid()
    {
        Assert.True(WorkflowTargetApp.IsValid(new string('a', WorkflowTargetApp.MaxLength)));
        Assert.False(WorkflowTargetApp.IsValid(new string('a', WorkflowTargetApp.MaxLength + 1)));
    }

    [Fact]
    public void MatchedRuleSendsToTheTrimmedTargetApp()
    {
        var rule = Rule(WorkflowTrigger.App("bg3"), " discord ");

        var snapshot = AutomaticWorkflowSnapshot.Select([rule], "bg3")!;

        Assert.Null(snapshot.Error);
        Assert.Equal("discord", snapshot.TargetApp);
        Assert.True(ManualWorkflowStore.IsEditable(rule));
    }

    [Fact]
    public void DictationShortcutSendsToTheTargetApp()
    {
        var shortcut = Rule(WorkflowTrigger.Hotkey(["CTRL+F9"], WorkflowHotkeyBehavior.StartDictation), "discord");

        Assert.True(ManualWorkflowStore.IsDictationShortcut(shortcut));
        Assert.Equal("discord", AutomaticWorkflowSnapshot.ForDictationShortcut(shortcut).TargetApp);
    }

    [Fact]
    public void WithoutTargetAppTheTextStaysWhereTheDictationStarted()
    {
        Assert.Null(AutomaticWorkflowSnapshot.Select([Rule(WorkflowTrigger.Global(), null)], "editor")!.TargetApp);
        Assert.Null(AutomaticWorkflowSnapshot.Rejected("error").TargetApp);
    }

    [Fact]
    public void InvalidTargetAppOrOneNextToAPluginActionIsUnsupported()
    {
        var invalid = Rule(WorkflowTrigger.Global(), "discord.exe");
        var withAction = Rule(WorkflowTrigger.Global(), "discord") with
        { Output = new WorkflowOutput { TargetAppProcessName = "discord", TargetActionPluginId = "action" } };

        foreach (var rule in new[] { invalid, withAction })
        {
            var snapshot = AutomaticWorkflowSnapshot.Select([rule], "editor")!;
            Assert.NotNull(snapshot.Error);
            Assert.Null(snapshot.TargetApp);
            Assert.False(ManualWorkflowStore.IsEditable(rule));
        }
    }

    [Fact]
    public void EditorDraftRoundTripsTheTargetAppAndKeepsUnchangedOutput()
    {
        var stored = Rule(WorkflowTrigger.App("bg3"), "discord");
        var draft = WorkflowDraft.FromStored(stored);

        Assert.Equal("discord", draft.TargetApp);
        Assert.Same(stored.Output, draft.ToStored().Output);
        Assert.Equal("slack", (draft with { TargetApp = " slack " }).ToStored().Output.TargetAppProcessName);
        Assert.Null((draft with { TargetApp = "" }).ToStored().Output.TargetAppProcessName);
    }

    [Fact]
    public void WorkflowFileOmitsAnUnsetTargetAppSoOlderVersionsStillReadIt()
    {
        Directory.CreateDirectory(_directory);
        var store = new ManualWorkflowStore(FilePath);

        store.Save(Rule(WorkflowTrigger.Global(), null), allowAutomatic: true);
        Assert.DoesNotContain("TargetAppProcessName", File.ReadAllText(FilePath));

        store.Save(Rule(WorkflowTrigger.Global(), "discord"), allowAutomatic: true);
        Assert.Contains("\"TargetAppProcessName\": \"discord\"", File.ReadAllText(FilePath));
        Assert.Equal("discord", Assert.Single(new ManualWorkflowStore(FilePath).Read()).Output.TargetAppProcessName);
    }

    [Fact]
    public void ChoosesTheFrontMostWindowTheUserCanSwitchTo()
    {
        var discord = new HashSet<uint> { 10, 11 };
        TopLevelWindow Window(int handle, uint process, bool visible = true, bool owned = false, bool tool = false, bool cloaked = false, int title = 7) =>
            new(handle, process, visible, owned, tool, cloaked, title);

        var chosen = TargetAppWindow.Choose([
            Window(1, 99),
            Window(2, 10, visible: false),
            Window(3, 10, owned: true),
            Window(4, 11, tool: true),
            Window(5, 10, cloaked: true),
            Window(6, 11, title: 0),
            Window(7, 11),
            Window(8, 10)], discord);

        Assert.Equal((IntPtr)7, chosen?.Window);
        Assert.Null(TargetAppWindow.Choose([Window(2, 10, visible: false), Window(1, 99)], discord));
    }

    [Fact]
    public void AppInTheNotificationAreaOffersItsHiddenMainWindow()
    {
        var discord = new HashSet<uint> { 10 };
        TopLevelWindow Hidden(int handle, bool caption = true, int width = 1289, int height = 1397, int title = 30, bool owned = false) =>
            new(handle, 10, false, owned, false, false, title, caption, width, height);

        var chosen = TargetAppWindow.Choose([
            Hidden(1, caption: false),
            Hidden(2, width: 136, height: 39),
            Hidden(3, title: 0),
            Hidden(4, owned: true),
            Hidden(5),
            Hidden(6)], discord);

        Assert.Equal((IntPtr)5, chosen?.Window);
        Assert.False(chosen?.Visible);
        // A visible window wins over a hidden one further in front.
        Assert.Equal((IntPtr)7, TargetAppWindow.Choose([Hidden(5), new(7, 10, true, false, false, false, 7)], discord)?.Window);
        Assert.Null(TargetAppWindow.Choose([Hidden(1, caption: false), Hidden(2, width: 1, height: 1)], discord));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
