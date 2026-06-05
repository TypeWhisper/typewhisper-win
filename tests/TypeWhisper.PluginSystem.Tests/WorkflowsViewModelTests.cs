using System.Windows.Controls;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class WorkflowsViewModelTests : IDisposable
{
    private readonly FakeSettingsService _settings = new(AppSettings.Default);
    private readonly PluginManager _pluginManager;

    public WorkflowsViewModelTests()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";
        _pluginManager = TestPluginManagerFactory.Create(_settings);
    }

    [Fact]
    public void DefaultProviderOption_ShowsAutoFallbackWhenNoDefaultConfigured()
    {
        AddLlmProvider(new FakeLlmProvider(
            "com.test.openai",
            "OpenAI",
            [new PluginModelInfo("gpt-5.5", "GPT-5.5")]));

        var sut = CreateViewModel();

        var defaultOption = Assert.Single(sut.AvailableProviders, option => option.Value is null);
        Assert.Equal("Default AI provider: OpenAI / GPT-5.5 (auto)", defaultOption.DisplayName);
        Assert.Same(defaultOption, sut.SelectedDefaultProvider);
        Assert.Null(_settings.Current.DefaultLlmProvider);
    }

    [Fact]
    public void DefaultProviderOption_ShowsAutoFallbackWhenConfiguredDefaultIsStale()
    {
        _settings.Save(_settings.Current with { DefaultLlmProvider = "plugin:missing:gpt-4o" });
        AddLlmProvider(new FakeLlmProvider(
            "com.test.openai",
            "OpenAI",
            [new PluginModelInfo("gpt-5.5", "GPT-5.5")]));

        var sut = CreateViewModel();

        var defaultOption = Assert.Single(sut.AvailableProviders, option => option.Value is null);
        Assert.Equal("Default AI provider: OpenAI / GPT-5.5 (auto)", defaultOption.DisplayName);
        Assert.Same(defaultOption, sut.SelectedDefaultProvider);
    }

    [Fact]
    public void ProviderOptions_UseLlmSelectionIdForAdditionalProfileRoles()
    {
        AddLlmProvider(new FakeLlmProvider(
            "com.typewhisper.openai-compatible",
            "Local Gateway",
            [new PluginModelInfo("gpt-local", "GPT Local")],
            selectionId: "openai-compatible-profile-a"));

        var sut = CreateViewModel();

        var option = Assert.Single(sut.AvailableProviders, provider => provider.Value is not null);
        Assert.Equal("plugin:openai-compatible-profile-a:gpt-local", option.Value);
        Assert.Equal("Local Gateway / GPT Local", option.DisplayName);
    }

    [Fact]
    public void ProviderOptions_ClearStaleEditProviderOverrideOnRefresh()
    {
        AddLlmProvider(new FakeLlmProvider(
            "com.test.openai",
            "OpenAI",
            [new PluginModelInfo("gpt-5.5", "GPT-5.5")]));
        var sut = CreateViewModel();
        sut.EditProviderOverride = "plugin:missing:gpt-4o";

        InvokeRebuildProviderOptions(sut);

        Assert.Null(sut.EditProviderOverride);
        Assert.Same(
            Assert.Single(sut.AvailableProviders, option => option.Value is null),
            sut.SelectedEditProvider);
    }

    [Fact]
    public void TaskOptions_PreserveTranslateWhenSelectedProfileCannotResolve()
    {
        var sut = CreateViewModel();
        sut.EditTask = "translate";

        sut.EditTranscriptionModelOverride = ModelManagerService.GetPluginModelId("missing-profile", "whisper");

        Assert.Equal("translate", sut.EditTask);
        Assert.DoesNotContain(sut.TaskOptions, option => option.Value == "translate");
    }

    [Fact]
    public void StartEdit_LoadsSingleHotkeyWorkflowAsOneHotkeyChip()
    {
        var workflow = NewWorkflow("Rewrite", WorkflowTrigger.Hotkey("Ctrl+Alt+R"));
        var workflows = new TestWorkflowService([workflow]);
        var sut = CreateViewModel(workflows);

        sut.StartEditCommand.Execute(workflow);

        Assert.Equal(["Ctrl+Alt+R"], sut.EditHotkeys);
        Assert.Equal("Ctrl+Alt+R starts dictation", WorkflowsViewModel.WorkflowTriggerDetail(workflow));
    }

    [Fact]
    public void AddAndRemoveHotkey_UpdatesEditorHotkeyChips()
    {
        var sut = CreateViewModel();

        sut.NewHotkey = "Ctrl+Alt+R";
        sut.AddHotkeyCommand.Execute(null);
        sut.NewHotkey = "Ctrl+Shift+R";
        sut.AddHotkeyCommand.Execute(null);
        sut.RemoveHotkeyCommand.Execute("Ctrl+Alt+R");

        Assert.Equal(["Ctrl+Shift+R"], sut.EditHotkeys);
        Assert.Equal("", sut.NewHotkey);
    }

    [Fact]
    public void AddHotkeyCommand_AcceptsRecordedHotkeyParameter()
    {
        var sut = CreateViewModel();

        sut.AddHotkeyCommand.Execute("Ctrl+Alt+R");

        Assert.Equal(["Ctrl+Alt+R"], sut.EditHotkeys);
        Assert.Equal("", sut.NewHotkey);
    }

    [Fact]
    public void SaveEditor_PersistsMultipleWorkflowHotkeys()
    {
        var workflows = new TestWorkflowService();
        var sut = CreateViewModel(workflows);
        sut.NewHotkey = "Ctrl+Alt+R";
        sut.AddHotkeyCommand.Execute(null);
        sut.NewHotkey = "Ctrl+Shift+R";
        sut.AddHotkeyCommand.Execute(null);

        sut.SaveEditorCommand.Execute(null);

        var saved = Assert.Single(workflows.Workflows);
        Assert.Equal(["Ctrl+Alt+R", "Ctrl+Shift+R"], saved.Trigger.Hotkeys);
    }

    [Fact]
    public void SaveEditor_RejectsDuplicateHotkeysInEditedWorkflow()
    {
        var workflows = new TestWorkflowService();
        var sut = CreateViewModel(workflows);
        sut.EditHotkeys.Add("Ctrl+Alt+R");
        sut.EditHotkeys.Add("Ctrl+Alt+R");

        sut.SaveEditorCommand.Execute(null);

        Assert.Empty(workflows.Workflows);
        Assert.Equal("This workflow already uses Ctrl+Alt+R.", sut.EditorError);
    }

    [Fact]
    public void AddHotkey_RevalidatesExistingEditorError()
    {
        var workflows = new TestWorkflowService();
        var sut = CreateViewModel(workflows);
        sut.EditTemplate = WorkflowTemplate.Translation;
        sut.EditTranslationTargetLanguage = "";
        sut.EditHotkeyTriggerEnabled = true;

        sut.SaveEditorCommand.Execute(null);
        sut.AddHotkeyCommand.Execute("Ctrl+Alt+R");

        Assert.Empty(workflows.Workflows);
        Assert.Equal("Translation workflows need a target language.", sut.EditorError);
    }

    [Fact]
    public void RemoveHotkey_RevalidatesExistingEditorError()
    {
        var sut = CreateViewModel();
        sut.EditHotkeys.Add("Ctrl+Alt+R");
        sut.EditHotkeys.Add("Ctrl+Alt+R");
        sut.SaveEditorCommand.Execute(null);

        sut.RemoveHotkeyCommand.Execute("Ctrl+Alt+R");

        Assert.Null(sut.EditorError);
    }

    [Fact]
    public void SaveEditor_RejectsHotkeyConflictWithAnotherWorkflow()
    {
        var existing = NewWorkflow("Existing", WorkflowTrigger.Hotkey("Ctrl+Alt+R"));
        var workflows = new TestWorkflowService([existing]);
        var sut = CreateViewModel(workflows);
        sut.NewHotkey = "Ctrl+Alt+R";
        sut.AddHotkeyCommand.Execute(null);

        sut.SaveEditorCommand.Execute(null);

        Assert.Single(workflows.Workflows);
        Assert.Equal("Ctrl+Alt+R is already used by workflow \"Existing\".", sut.EditorError);
    }

    [Fact]
    public void SaveEditor_RejectsHotkeyConflictWithAppShortcut()
    {
        _settings.Save(AppSettings.Default with { PushToTalkHotkey = "Ctrl+Alt+R", ToggleHotkey = "Ctrl+Alt+R" });
        var workflows = new TestWorkflowService();
        var sut = CreateViewModel(workflows);
        sut.NewHotkey = "Ctrl+Alt+R";
        sut.AddHotkeyCommand.Execute(null);

        sut.SaveEditorCommand.Execute(null);

        Assert.Empty(workflows.Workflows);
        Assert.Equal("Ctrl+Alt+R is already used by Main dictation hotkey.", sut.EditorError);
    }

    [Fact]
    public void SaveEditor_RejectsHotkeyConflictWithAdditionalAppShortcutChip()
    {
        _settings.Save(AppSettings.Default with
        {
            MainDictationHotkeys = ["Ctrl+Alt+D", "Ctrl+Alt+R"]
        });
        var workflows = new TestWorkflowService();
        var sut = CreateViewModel(workflows);
        sut.NewHotkey = "Ctrl+Alt+R";
        sut.AddHotkeyCommand.Execute(null);

        sut.SaveEditorCommand.Execute(null);

        Assert.Empty(workflows.Workflows);
        Assert.Equal("Ctrl+Alt+R is already used by Main dictation hotkey.", sut.EditorError);
    }

    [Fact]
    public void ReviewText_IncludesMultipleEditedHotkeys()
    {
        var sut = CreateViewModel();
        sut.NewHotkey = "Ctrl+Alt+R";
        sut.AddHotkeyCommand.Execute(null);
        sut.NewHotkey = "Ctrl+Shift+R";
        sut.AddHotkeyCommand.Execute(null);

        Assert.Contains("Ctrl+Alt+R, Ctrl+Shift+R", sut.ReviewText);
    }

    public void Dispose() => _pluginManager.Dispose();

    private WorkflowsViewModel CreateViewModel(TestWorkflowService? workflows = null)
    {
        workflows ??= new TestWorkflowService();

        var activeWindow = new Mock<IActiveWindowService>();
        activeWindow.Setup(service => service.GetBrowserUrl()).Returns((string?)null);

        var history = new Mock<IHistoryService>();
        history.SetupGet(service => service.Records).Returns([]);
        history.Setup(service => service.GetDistinctApps()).Returns([]);

        return new WorkflowsViewModel(
            workflows,
            activeWindow.Object,
            history.Object,
            _settings,
            _pluginManager,
            new ModelManagerService(_pluginManager, _settings),
            new WindowsAppDiscoveryService(history.Object));
    }

    private static Workflow NewWorkflow(string name, WorkflowTrigger trigger) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            IsEnabled = true,
            SortOrder = 0,
            Template = WorkflowTemplate.CleanedText,
            Trigger = trigger
        };

    private void AddLlmProvider(FakeLlmProvider provider)
    {
        var manifest = new PluginManifest
        {
            Id = provider.PluginId,
            Name = provider.PluginName,
            Version = provider.PluginVersion,
            AssemblyName = "Fake.dll",
            PluginClass = provider.GetType().FullName!
        };
        var context = new PluginAssemblyLoadContext(typeof(WorkflowsViewModelTests).Assembly.Location);
        var loaded = new LoadedPlugin(manifest, provider, context, AppContext.BaseDirectory);

        TestPluginManagerFactory.SetPrivateField(_pluginManager, "_allPlugins", new List<LoadedPlugin> { loaded });
        TestPluginManagerFactory.SetPrivateField(_pluginManager, "_llmProviders", new List<ILlmProviderPlugin> { provider });
    }

    private static void InvokeRebuildProviderOptions(WorkflowsViewModel viewModel) =>
        typeof(WorkflowsViewModel)
            .GetMethod("RebuildProviderOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(viewModel, null);

    private sealed class FakeLlmProvider : ILlmProviderPlugin, ILlmProviderSelectionIdentity
    {
        private readonly string? _selectionId;

        public FakeLlmProvider(
            string pluginId,
            string providerName,
            IReadOnlyList<PluginModelInfo> supportedModels,
            string? selectionId = null)
        {
            PluginId = pluginId;
            PluginName = providerName;
            ProviderName = providerName;
            SupportedModels = supportedModels;
            _selectionId = selectionId;
        }

        public string PluginId { get; }
        public string PluginName { get; }
        public string PluginVersion => "1.0.0";
        public string LlmSelectionId => _selectionId ?? PluginId;
        public string ProviderName { get; }
        public bool IsAvailable { get; set; } = true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public UserControl? CreateSettingsView() => null;
        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) =>
            Task.FromResult(userText);
        public void Dispose() { }
    }

    private sealed class TestWorkflowService : IWorkflowService
    {
        private readonly List<Workflow> _workflows;

        public TestWorkflowService(IReadOnlyList<Workflow>? workflows = null)
        {
            _workflows = workflows?.ToList() ?? [];
        }

        public IReadOnlyList<Workflow> Workflows => _workflows;
        public event Action? WorkflowsChanged;

        public void AddWorkflow(Workflow workflow)
        {
            _workflows.Add(workflow);
            WorkflowsChanged?.Invoke();
        }

        public void UpdateWorkflow(Workflow workflow)
        {
            var index = _workflows.FindIndex(existing => existing.Id == workflow.Id);
            if (index >= 0)
                _workflows[index] = workflow;
            WorkflowsChanged?.Invoke();
        }

        public void DeleteWorkflow(string id)
        {
            _workflows.RemoveAll(workflow => workflow.Id == id);
            WorkflowsChanged?.Invoke();
        }

        public void ToggleWorkflow(string id) { }
        public void Reorder(IReadOnlyList<string> orderedIds) { }
        public int NextSortOrder() => _workflows.Count;
        public Workflow? GetWorkflow(string id) => _workflows.FirstOrDefault(workflow => workflow.Id == id);
        public WorkflowMatchResult? MatchWorkflow(string? processName, string? url) => null;
        public WorkflowMatchResult? ForceMatch(string workflowId) => null;
    }
}
