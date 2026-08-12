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
    public void DefaultProviderOption_SkipsKnownUnavailableProviderForAutoFallback()
    {
        var unavailable = new FakeLlmProvider(
            "com.test.unavailable",
            "Unavailable",
            [new PluginModelInfo("default", "Default")])
        {
            IsAvailable = false
        };
        var available = new FakeLlmProvider(
            "com.test.available",
            "Available",
            [new PluginModelInfo("default", "Default")]);
        SetLlmProviders(unavailable, available);

        var sut = CreateViewModel();

        var defaultOption = Assert.Single(sut.AvailableProviders, option => option.Value is null);
        Assert.Equal("Default AI provider: Available / Default (auto)", defaultOption.DisplayName);
    }

    [Fact]
    public void DefaultProviderOption_PreservesUnavailableConfiguredDefault()
    {
        _settings.Save(_settings.Current with { DefaultLlmProvider = "plugin:missing:gpt-4o" });
        AddLlmProvider(new FakeLlmProvider(
            "com.test.openai",
            "OpenAI",
            [new PluginModelInfo("gpt-5.5", "GPT-5.5")]));

        var sut = CreateViewModel();

        var defaultOption = Assert.Single(sut.AvailableProviders, option => option.Value is null);
        Assert.Contains("unavailable", defaultOption.DisplayName, StringComparison.OrdinalIgnoreCase);
        var selected = Assert.IsType<ProviderOption>(sut.SelectedDefaultProvider);
        Assert.Equal("plugin:missing:gpt-4o", selected.Value);
        Assert.Contains("unavailable", selected.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("plugin:missing:gpt-4o", _settings.Current.DefaultLlmProvider);
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
    public void ProviderOptions_PreserveStaleEditProviderOverrideOnRefresh()
    {
        AddLlmProvider(new FakeLlmProvider(
            "com.test.openai",
            "OpenAI",
            [new PluginModelInfo("gpt-5.5", "GPT-5.5")]));
        var sut = CreateViewModel();
        sut.EditProviderOverride = "plugin:missing:gpt-4o";

        InvokeRebuildProviderOptions(sut);

        Assert.Equal("plugin:missing:gpt-4o", sut.EditProviderOverride);
        var selected = Assert.IsType<ProviderOption>(sut.SelectedEditProvider);
        Assert.Equal("plugin:missing:gpt-4o", selected.Value);
        Assert.Contains("unavailable", selected.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderOptions_ShowKnownUnavailableProviderWithoutClearingSelection()
    {
        var provider = new FakeLlmProvider(
            "com.test.cli",
            "CLI Provider",
            [new PluginModelInfo("default", "Provider default")])
        {
            IsAvailable = false
        };
        AddLlmProvider(provider);
        _settings.Save(_settings.Current with { DefaultLlmProvider = "plugin:com.test.cli:default" });

        var sut = CreateViewModel();

        var selected = Assert.IsType<ProviderOption>(sut.SelectedDefaultProvider);
        Assert.Equal("plugin:com.test.cli:default", selected.Value);
        Assert.Equal("CLI Provider / Provider default (unavailable)", selected.DisplayName);
        Assert.True(sut.HasNoAvailableLlmProvider);
    }

    [Fact]
    public void WorkflowProviderOptions_OfferNoneWithoutChangingGlobalProviders()
    {
        var sut = CreateViewModel();

        Assert.Equal("none", sut.AvailableEditProviders[0].Value);
        Assert.Equal("None (no post-processing)", sut.AvailableEditProviders[0].DisplayName);
        Assert.DoesNotContain(sut.AvailableProviders, option => option.Value == "none");
    }

    [Fact]
    public void NewDraft_IsNamedSmartFormattingButKeepsCleanedTextTemplate()
    {
        var sut = CreateViewModel();

        Assert.Equal("Smart Formatting", sut.EditName);
        Assert.Equal(WorkflowTemplate.CleanedText, sut.EditTemplate);
        Assert.Contains("AI", sut.SelectedTemplateDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewDraft_DefaultsCombinedContextMatchingToRecommendedAnyMode()
    {
        var sut = CreateViewModel();

        Assert.Equal(WorkflowContextMatchMode.Any, sut.EditContextMatchMode);
        Assert.Contains(
            sut.ContextMatchModeOptions,
            option => option.Mode == WorkflowContextMatchMode.Any
                      && option.DisplayName.Contains("recommended", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ContextMatchModeEditor_IsVisibleOnlyForAppAndWebsiteCombination()
    {
        var sut = CreateViewModel();

        sut.EditAppTriggerEnabled = true;
        Assert.False(sut.ShowContextMatchModeEditor);

        sut.EditWebsiteTriggerEnabled = true;
        Assert.True(sut.ShowContextMatchModeEditor);

        sut.EditAppTriggerEnabled = false;
        Assert.False(sut.ShowContextMatchModeEditor);
    }

    [Fact]
    public void ExistingCombinedWorkflow_LoadsBackwardCompatibleAllMode()
    {
        var workflow = NewWorkflow(
            "Combined",
            new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.App,
                ProcessNames = ["chrome"],
                WebsitePatterns = ["github.com"]
            });
        var sut = CreateViewModel(new TestWorkflowService([workflow]));

        sut.StartEditCommand.Execute(workflow);

        Assert.Equal(WorkflowContextMatchMode.All, sut.EditContextMatchMode);
        Assert.True(sut.ShowContextMatchModeEditor);
    }

    [Fact]
    public void SaveEditor_PersistsAnyContextMatchMode()
    {
        var workflows = new TestWorkflowService();
        var sut = CreateViewModel(workflows);
        sut.EditAppTriggerEnabled = true;
        sut.EditWebsiteTriggerEnabled = true;
        sut.EditHotkeyTriggerEnabled = false;
        sut.ProcessNameChips.Add("antigravity");
        sut.WebsitePatternChips.Add("perplexity.ai");
        sut.EditContextMatchMode = WorkflowContextMatchMode.Any;

        sut.SaveEditorCommand.Execute(null);

        var saved = Assert.Single(workflows.Workflows);
        Assert.Equal(WorkflowContextMatchMode.Any, saved.Trigger.ContextMatchMode);
    }

    [Theory]
    [InlineData(WorkflowContextMatchMode.Any, "App or Website")]
    [InlineData(WorkflowContextMatchMode.All, "App and Website")]
    public void WorkflowTriggerSummary_UsesContextMatchMode(
        WorkflowContextMatchMode mode,
        string expected)
    {
        var workflow = NewWorkflow(
            "Combined",
            new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.App,
                ProcessNames = ["chrome"],
                WebsitePatterns = ["github.com"],
                ContextMatchMode = mode
            });

        var summary = WorkflowsViewModel.WorkflowTriggerSummary(workflow);

        Assert.Equal(expected, summary);
    }

    [Fact]
    public void ExistingCleanedTextWorkflowName_IsNotMigrated()
    {
        var workflow = NewWorkflow("My existing cleaned text", WorkflowTrigger.Global());
        var sut = CreateViewModel(new TestWorkflowService([workflow]));

        sut.StartEditCommand.Execute(workflow);

        Assert.Equal("My existing cleaned text", sut.EditName);
        Assert.Equal(WorkflowTemplate.CleanedText, sut.EditTemplate);
    }

    [Fact]
    public void ProviderWarning_TracksAvailableProviderState()
    {
        var sut = CreateViewModel();

        Assert.False(sut.HasAvailableLlmProvider);
        Assert.True(sut.HasNoAvailableLlmProvider);

        AddLlmProvider(new FakeLlmProvider(
            "com.test.openai",
            "OpenAI",
            [new PluginModelInfo("gpt-5.5", "GPT-5.5")]));
        InvokeRebuildProviderOptions(sut);

        Assert.True(sut.HasAvailableLlmProvider);
        Assert.False(sut.HasNoAvailableLlmProvider);
    }

    [Fact]
    public void ProviderWarning_ReactsToPluginStateChanges()
    {
        var provider = new FakeLlmProvider(
            "com.test.openai",
            "OpenAI",
            [new PluginModelInfo("gpt-5.5", "GPT-5.5")]);
        AddLlmProvider(provider);
        var sut = CreateViewModel();
        var changedProperties = new List<string?>();
        sut.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        provider.IsAvailable = false;
        RaisePluginStateChanged();

        Assert.True(sut.HasNoAvailableLlmProvider);
        Assert.Contains(nameof(WorkflowsViewModel.HasAvailableLlmProvider), changedProperties);
        Assert.Contains(nameof(WorkflowsViewModel.HasNoAvailableLlmProvider), changedProperties);
    }

    [Fact]
    public void SelectedEditProvider_IgnoresSelectionChangesDuringProviderRefresh()
    {
        var sut = CreateViewModel();
        sut.EditProviderOverride = "none";
        TestPluginManagerFactory.SetPrivateField(sut, "_isRefreshingProviders", true);

        sut.SelectedEditProvider = null;

        Assert.Equal("none", sut.EditProviderOverride);
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
    public void LanguageOptions_IncludeChineseSpokenLanguage()
    {
        var sut = CreateViewModel();

        var option = Assert.Single(sut.LanguageOptions, option => option.Value == "zh");
        Assert.Equal("中文", option.DisplayName);
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

    [Theory]
    [InlineData("MouseLeft")]
    [InlineData("MouseRight")]
    public void AddHotkeyCommand_RejectsUnsafeMouseBinding(string hotkey)
    {
        var sut = CreateViewModel();

        sut.AddHotkeyCommand.Execute(hotkey);

        Assert.Empty(sut.EditHotkeys);
        Assert.Contains("modifier", sut.EditorError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", sut.NewHotkey);
    }

    [Fact]
    public void StartEdit_PreservesUnsafeMouseBindingAndShowsWarning()
    {
        var workflow = NewWorkflow("Mouse", WorkflowTrigger.Hotkey("MouseLeft"));
        var workflows = new TestWorkflowService([workflow]);
        var sut = CreateViewModel(workflows);

        sut.StartEditCommand.Execute(workflow);

        Assert.Equal(["MouseLeft"], sut.EditHotkeys);
        Assert.Contains("modifier", sut.EditorError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["MouseLeft"], Assert.Single(workflows.Workflows).Trigger.Hotkeys);
    }

    [Fact]
    public void SaveEditor_RejectsExistingUnsafeMouseBindingUntilRemoved()
    {
        var workflow = NewWorkflow("Mouse", WorkflowTrigger.Hotkey("MouseLeft"));
        var workflows = new TestWorkflowService([workflow]);
        var sut = CreateViewModel(workflows);
        sut.StartEditCommand.Execute(workflow);

        sut.SaveEditorCommand.Execute(null);

        Assert.True(sut.IsEditorOpen);
        Assert.Contains("modifier", sut.EditorError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["MouseLeft"], Assert.Single(workflows.Workflows).Trigger.Hotkeys);

        sut.RemoveHotkeyCommand.Execute("MouseLeft");
        sut.AddHotkeyCommand.Execute("Ctrl+MouseLeft");
        sut.SaveEditorCommand.Execute(null);

        Assert.False(sut.IsEditorOpen);
        Assert.Equal(["Ctrl+MouseLeft"], Assert.Single(workflows.Workflows).Trigger.Hotkeys);
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
    public void SaveEditor_PersistsAndReloadsNoPostProcessingProvider()
    {
        var workflows = new TestWorkflowService();
        var sut = CreateViewModel(workflows);
        sut.EditTriggerMode = WorkflowTriggerMode.Global;
        sut.SelectedEditProvider = sut.AvailableEditProviders[0];

        sut.SaveEditorCommand.Execute(null);

        var saved = Assert.Single(workflows.Workflows);
        Assert.Equal("none", saved.Behavior.ProviderOverride);
        sut.StartEditCommand.Execute(saved);
        Assert.Equal("none", sut.SelectedEditProvider?.Value);
    }

    [Fact]
    public void SaveEditor_AllowsCustomWorkflowWithoutPrompt()
    {
        var workflows = new TestWorkflowService();
        var sut = CreateViewModel(workflows);
        sut.EditTriggerMode = WorkflowTriggerMode.Global;
        sut.EditTemplate = WorkflowTemplate.Custom;
        sut.EditCustomInstruction = "";
        sut.EditFineTuning = "";

        sut.SaveEditorCommand.Execute(null);

        var saved = Assert.Single(workflows.Workflows);
        Assert.Null(saved.SystemPrompt());
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

    private void AddLlmProvider(FakeLlmProvider provider) => SetLlmProviders(provider);

    private void SetLlmProviders(params FakeLlmProvider[] providers)
    {
        var loaded = providers.Select(provider =>
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
            return new LoadedPlugin(manifest, provider, context, AppContext.BaseDirectory);
        }).ToList();

        TestPluginManagerFactory.SetPrivateField(_pluginManager, "_allPlugins", loaded);
        TestPluginManagerFactory.SetPrivateField(
            _pluginManager,
            "_llmProviders",
            providers.Cast<ILlmProviderPlugin>().ToList());
    }

    private static void InvokeRebuildProviderOptions(WorkflowsViewModel viewModel) =>
        typeof(WorkflowsViewModel)
            .GetMethod("RebuildProviderOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(viewModel, null);

    private void RaisePluginStateChanged()
    {
        var handler = (EventHandler?)typeof(PluginManager)
            .GetField("PluginStateChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_pluginManager);
        handler?.Invoke(_pluginManager, EventArgs.Empty);
    }

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
