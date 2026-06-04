using System.Reflection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class HotkeyServiceTests
{
    [Fact]
    public void BuildWorkflowHotkeyBindings_ExcludesManualWorkflows()
    {
        IReadOnlyList<Workflow> workflows =
        [
            NewWorkflow("Manual", WorkflowTrigger.Manual()),
            NewWorkflow("Hotkey", WorkflowTrigger.Hotkey("Ctrl+Alt+H")),
            NewWorkflow("Disabled Hotkey", WorkflowTrigger.Hotkey("Ctrl+Alt+D"), isEnabled: false)
        ];

        var bindings = HotkeyService.BuildWorkflowHotkeyBindings(workflows);

        var binding = Assert.Single(bindings);
        Assert.Equal("Ctrl+Alt+H", binding.Hotkey);
        Assert.Equal(WorkflowHotkeyBehavior.StartDictation, binding.Behavior);
    }

    [Fact]
    public void BuildWorkflowHotkeyBindings_IncludesProcessSelectedTextBehavior()
    {
        IReadOnlyList<Workflow> workflows =
        [
            NewWorkflow(
                "Combined",
                new WorkflowTrigger
                {
                    Kind = WorkflowTriggerKind.App,
                    ProcessNames = ["notepad"],
                    Hotkeys = ["Ctrl+Alt+P"],
                    HotkeyBehavior = WorkflowHotkeyBehavior.ProcessSelectedText
                })
        ];

        var binding = Assert.Single(HotkeyService.BuildWorkflowHotkeyBindings(workflows));

        Assert.Equal("Ctrl+Alt+P", binding.Hotkey);
        Assert.Equal(WorkflowHotkeyBehavior.ProcessSelectedText, binding.Behavior);
    }

    [Fact]
    public void BuildWorkflowHotkeyBindings_IncludesMultipleHotkeysForOneWorkflow()
    {
        var workflow = NewWorkflow(
            "Rewrite",
            WorkflowTrigger.Hotkey(
                WorkflowHotkeyBehavior.ProcessSelectedText,
                "Ctrl+Alt+R",
                "Ctrl+Shift+R"));

        var bindings = HotkeyService.BuildWorkflowHotkeyBindings([workflow]);

        Assert.Equal(["Ctrl+Alt+R", "Ctrl+Shift+R"], bindings.Select(binding => binding.Hotkey));
        Assert.All(bindings, binding =>
        {
            Assert.Equal(workflow.Id, binding.WorkflowId);
            Assert.Equal(WorkflowHotkeyBehavior.ProcessSelectedText, binding.Behavior);
        });
    }

    [Fact]
    public void BuildAppHotkeyBindings_IncludesMultipleHotkeysPerShortcutAction()
    {
        var settings = AppSettings.Default with
        {
            MainDictationHotkeys = ["Ctrl+Alt+D", "Ctrl+Shift+D"],
            ToggleOnlyHotkeys = ["Ctrl+Alt+T"],
            WorkflowPaletteHotkeys = ["Ctrl+Alt+W", "Ctrl+Shift+W"]
        };

        var bindings = HotkeyService.BuildAppHotkeyBindings(settings);

        Assert.Equal(
            ["Ctrl+Alt+D", "Ctrl+Shift+D"],
            bindings.Where(binding => binding.Action == AppHotkeyAction.MainDictation)
                .Select(binding => binding.Hotkey));
        Assert.Equal(
            ["Ctrl+Alt+T"],
            bindings.Where(binding => binding.Action == AppHotkeyAction.ToggleOnly)
                .Select(binding => binding.Hotkey));
        Assert.Equal(
            ["Ctrl+Alt+W", "Ctrl+Shift+W"],
            bindings.Where(binding => binding.Action == AppHotkeyAction.WorkflowPalette)
                .Select(binding => binding.Hotkey));
    }

    [Fact]
    public void WorkflowPaletteRequested_EventIsRaised()
    {
        var sut = new HotkeyService(
            new FakeSettingsService(AppSettings.Default with { WorkflowPaletteHotkey = "Ctrl+Alt+W" }),
            new FakeWorkflowService());
        var raised = false;
        sut.WorkflowPaletteRequested += (_, _) => raised = true;

        var method = typeof(HotkeyService).GetMethod(
            "OnWorkflowPaletteKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method!.Invoke(sut, [null, EventArgs.Empty]);

        Assert.True(raised);
    }

    [Fact]
    public void WorkflowTextProcessingRequested_EventIsRaised()
    {
        var workflow = NewWorkflow(
            "Rewrite",
            new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.Hotkey,
                Hotkeys = ["Ctrl+Alt+R"],
                HotkeyBehavior = WorkflowHotkeyBehavior.ProcessSelectedText
            });
        var sut = new HotkeyService(
            new FakeSettingsService(AppSettings.Default),
            new FakeWorkflowService([workflow]));

        var method = typeof(HotkeyService).GetMethod(
            "HandleWorkflowHotkeyKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        string? raisedWorkflowId = null;
        sut.WorkflowTextProcessingRequested += (_, workflowId) => raisedWorkflowId = workflowId;

        method!.Invoke(sut, [workflow.Id, WorkflowHotkeyBehavior.ProcessSelectedText]);

        Assert.Equal(workflow.Id, raisedWorkflowId);
    }

    private static Workflow NewWorkflow(string name, WorkflowTrigger trigger, bool isEnabled = true) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            IsEnabled = isEnabled,
            SortOrder = 0,
            Template = WorkflowTemplate.CleanedText,
            Trigger = trigger
        };

    private sealed class FakeSettingsService : ISettingsService
    {
        public FakeSettingsService(AppSettings current)
        {
            Current = current;
        }

        public AppSettings Current { get; private set; }
        public event Action<AppSettings>? SettingsChanged;

        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }

    private sealed class FakeWorkflowService : IWorkflowService
    {
        public FakeWorkflowService(IReadOnlyList<Workflow>? workflows = null)
        {
            Workflows = workflows ?? [];
        }

        public IReadOnlyList<Workflow> Workflows { get; }
        public event Action? WorkflowsChanged
        {
            add { }
            remove { }
        }
        public void AddWorkflow(Workflow workflow) => throw new NotSupportedException();
        public void UpdateWorkflow(Workflow workflow) => throw new NotSupportedException();
        public void DeleteWorkflow(string id) => throw new NotSupportedException();
        public void ToggleWorkflow(string id) => throw new NotSupportedException();
        public void Reorder(IReadOnlyList<string> orderedIds) => throw new NotSupportedException();
        public int NextSortOrder() => 0;
        public Workflow? GetWorkflow(string id) => null;
        public WorkflowMatchResult? MatchWorkflow(string? processName, string? url) => null;
        public WorkflowMatchResult? ForceMatch(string workflowId) => null;
    }
}
