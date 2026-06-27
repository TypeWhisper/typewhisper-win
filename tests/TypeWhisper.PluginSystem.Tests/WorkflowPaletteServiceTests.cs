using System.IO;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class WorkflowPaletteServiceTests : IDisposable
{
    private readonly string _workflowFilePath;

    public WorkflowPaletteServiceTests()
    {
        _workflowFilePath = Path.GetTempFileName();
    }

    [Fact]
    public async Task TogglePaletteAsync_ShowsEnabledManualWorkflowsOnly()
    {
        var workflows = new WorkflowService(_workflowFilePath);
        workflows.AddWorkflow(NewWorkflow("Manual One", WorkflowTrigger.Manual(), sortOrder: 0));
        workflows.AddWorkflow(NewWorkflow("Disabled Manual", WorkflowTrigger.Manual(), sortOrder: 1, isEnabled: false));
        workflows.AddWorkflow(NewWorkflow("Always", WorkflowTrigger.Global(), sortOrder: 2));
        workflows.AddWorkflow(NewWorkflow("Broken Manual", WorkflowTrigger.Manual(), sortOrder: 3) with
        {
            Template = WorkflowTemplate.Custom
        });

        var presenter = new FakeWorkflowPalettePresenter();
        var platform = new FakeTextInsertionPlatform { ClipboardText = "clipboard text" };
        var textInsertion = new TextInsertionService(platform);
        var settings = new FakeSettingsService(AppSettings.Default with { AutoPaste = false });
        var activeWindow = new FakeActiveWindowService();
        var pluginManager = CreatePluginManager(workflows, settings, activeWindow);
        var processor = new FakeWorkflowTextProcessor();
        var sut = new WorkflowPaletteService(
            workflows,
            activeWindow,
            textInsertion,
            settings,
            processor,
            pluginManager,
            presenter);

        await sut.TogglePaletteAsync();

        var shownViewModel = Assert.IsType<WorkflowPaletteViewModel>(presenter.ViewModel);
        var item = Assert.Single(shownViewModel.FilteredWorkflows);
        Assert.Equal("Manual One", item.Name);
    }

    [Fact]
    public async Task SelectingWorkflow_ProcessesCapturedTextAndCopiesResultBack()
    {
        var workflows = new WorkflowService(_workflowFilePath);
        workflows.AddWorkflow(NewWorkflow("Rewrite", WorkflowTrigger.Manual(), sortOrder: 0));

        var presenter = new FakeWorkflowPalettePresenter();
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            CapturedSelectionText = "selected text"
        };
        var textInsertion = new TextInsertionService(platform);
        var settings = new FakeSettingsService(AppSettings.Default with { AutoPaste = false });
        var activeWindow = new FakeActiveWindowService();
        var processor = new FakeWorkflowTextProcessor { ResultText = "processed text" };
        var sut = new WorkflowPaletteService(
            workflows,
            activeWindow,
            textInsertion,
            settings,
            processor,
            CreatePluginManager(workflows, settings, activeWindow),
            presenter);

        await sut.TogglePaletteAsync();
        presenter.ViewModel!.SelectCurrent();
        await WaitUntilAsync(() => platform.ClipboardText == "processed text");

        Assert.Equal("selected text", processor.LastInputText);
        Assert.Equal("processed text", platform.ClipboardText);
        Assert.Equal(1, platform.CopyInputCalls);
    }

    [Fact]
    public async Task TogglePaletteAsync_FallsBackToClipboardWhenNoSelectionWasCopied()
    {
        var workflows = new WorkflowService(_workflowFilePath);
        workflows.AddWorkflow(NewWorkflow("Rewrite", WorkflowTrigger.Manual(), sortOrder: 0));

        var presenter = new FakeWorkflowPalettePresenter();
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "clipboard fallback"
        };
        var textInsertion = new TextInsertionService(platform);
        var settings = new FakeSettingsService(AppSettings.Default with { AutoPaste = false });
        var activeWindow = new FakeActiveWindowService();
        var processor = new FakeWorkflowTextProcessor { ResultText = "processed text" };
        var sut = new WorkflowPaletteService(
            workflows,
            activeWindow,
            textInsertion,
            settings,
            processor,
            CreatePluginManager(workflows, settings, activeWindow),
            presenter);

        await sut.TogglePaletteAsync();
        presenter.ViewModel!.SelectCurrent();
        await WaitUntilAsync(() => processor.LastInputText is not null);

        Assert.Equal("clipboard fallback", processor.LastInputText);
    }

    [Fact]
    public async Task SelectingWorkflow_WithoutAvailableProvider_RaisesFeedback()
    {
        var workflows = new WorkflowService(_workflowFilePath);
        workflows.AddWorkflow(NewWorkflow("Rewrite", WorkflowTrigger.Manual(), sortOrder: 0));

        var presenter = new FakeWorkflowPalettePresenter();
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            CapturedSelectionText = "selected text"
        };
        var textInsertion = new TextInsertionService(platform);
        var settings = new FakeSettingsService(AppSettings.Default with { AutoPaste = false });
        var activeWindow = new FakeActiveWindowService();
        var processor = new FakeWorkflowTextProcessor { IsAnyProviderAvailable = false };
        var sut = new WorkflowPaletteService(
            workflows,
            activeWindow,
            textInsertion,
            settings,
            processor,
            CreatePluginManager(workflows, settings, activeWindow),
            presenter);

        (string Message, bool IsError)? feedback = null;
        sut.FeedbackRequested += (message, isError) => feedback = (message, isError);

        await sut.TogglePaletteAsync();
        presenter.ViewModel!.SelectCurrent();
        await WaitUntilAsync(() => feedback is not null);

        Assert.NotNull(feedback);
        Assert.True(feedback!.Value.IsError);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_ProcessesAutomaticWorkflowWithoutOpeningPalette()
    {
        var workflows = new WorkflowService(_workflowFilePath);
        workflows.AddWorkflow(NewWorkflow(
            "Rewrite",
            new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.Hotkey,
                Hotkeys = ["Ctrl+Alt+R"],
                HotkeyBehavior = WorkflowHotkeyBehavior.ProcessSelectedText
            },
            sortOrder: 0));

        var presenter = new FakeWorkflowPalettePresenter();
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            CapturedSelectionText = "selected text"
        };
        var textInsertion = new TextInsertionService(platform);
        var settings = new FakeSettingsService(AppSettings.Default with { AutoPaste = false });
        var activeWindow = new FakeActiveWindowService();
        var processor = new FakeWorkflowTextProcessor { ResultText = "processed text" };
        var sut = new WorkflowPaletteService(
            workflows,
            activeWindow,
            textInsertion,
            settings,
            processor,
            CreatePluginManager(workflows, settings, activeWindow),
            presenter);

        await sut.ExecuteWorkflowAsync(workflows.Workflows.Single());
        await WaitUntilAsync(() => platform.ClipboardText == "processed text");

        Assert.Null(presenter.ViewModel);
        Assert.Equal("selected text", processor.LastInputText);
    }

    private static PluginManager CreatePluginManager(
        IWorkflowService workflows,
        ISettingsService settings,
        IActiveWindowService activeWindow) =>
        new(
            new PluginLoader(),
            new PluginEventBus(),
            activeWindow,
            workflows,
            settings,
            Array.Empty<string>());

    private static Workflow NewWorkflow(
        string name,
        WorkflowTrigger trigger,
        int sortOrder,
        bool isEnabled = true) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            IsEnabled = isEnabled,
            SortOrder = sortOrder,
            Template = WorkflowTemplate.CleanedText,
            Trigger = trigger,
            Behavior = new WorkflowBehavior { InputLanguage = "en" }
        };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 1000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
                throw new TimeoutException("Condition was not reached in time.");

            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_workflowFilePath))
            File.Delete(_workflowFilePath);
    }

    private sealed class FakeWorkflowPalettePresenter : IWorkflowPalettePresenter
    {
        public WorkflowPaletteViewModel? ViewModel { get; private set; }
        public bool IsVisible { get; private set; }

        public void Show(WorkflowPaletteViewModel viewModel, Action onClosed)
        {
            ViewModel = viewModel;
            IsVisible = true;
        }

        public void Close()
        {
            IsVisible = false;
        }
    }

    private sealed class FakeWorkflowTextProcessor : IWorkflowTextProcessor
    {
        public bool IsAnyProviderAvailable { get; set; } = true;
        public string ResultText { get; set; } = "processed";
        public string? LastInputText { get; private set; }

        public Task<string> ProcessAsync(
            string systemPrompt,
            string inputText,
            string? providerOverride,
            string? modelOverride,
            CancellationToken ct)
        {
            LastInputText = inputText;
            return Task.FromResult(ResultText);
        }
    }

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

    private sealed class FakeActiveWindowService : IActiveWindowService
    {
        public IntPtr Handle { get; set; } = new(77);
        public string? ProcessName { get; set; } = "notepad";
        public string? Title { get; set; } = "Notepad";
        public string? Url { get; set; }

        public IntPtr GetActiveWindowHandle() => Handle;
        public string? GetActiveWindowProcessName() => ProcessName;
        public string? GetActiveWindowTitle() => Title;
        public string? GetBrowserUrl() => Url;
        public IReadOnlyList<string> GetRunningAppProcessNames() => [];
    }

    private sealed class FakeTextInsertionPlatform : ITextInsertionPlatform
    {
        public string? ClipboardText { get; set; }
        public string? CapturedSelectionText { get; set; }
        public int MarkerReadsBeforeSelection { get; set; }
        public uint CopyInputResult { get; set; } = 4;
        public uint PasteInputResult { get; set; } = 4;
        public uint EnterInputResult { get; set; } = 2;
        public IntPtr ForegroundWindow { get; set; }
        public bool SetForegroundWindowResult { get; set; } = true;
        public IntPtr LastSetForegroundWindow { get; private set; }
        public int CopyInputCalls { get; private set; }
        private string? SelectionMarker { get; set; }
        private int MarkerReadsCompleted { get; set; }

        public Task<string?> TryGetClipboardTextAsync()
        {
            if (SelectionMarker is not null && CopyInputCalls > 0)
            {
                if (CapturedSelectionText is null)
                {
                    ClipboardText = SelectionMarker;
                    return Task.FromResult<string?>(ClipboardText);
                }

                if (MarkerReadsCompleted < MarkerReadsBeforeSelection)
                {
                    MarkerReadsCompleted++;
                    ClipboardText = SelectionMarker;
                    return Task.FromResult<string?>(ClipboardText);
                }

                ClipboardText = CapturedSelectionText;
                SelectionMarker = null;
            }

            return Task.FromResult<string?>(ClipboardText);
        }

        public Task SetClipboardTextAsync(string text)
        {
            ClipboardText = text;
            if (text.StartsWith("__typewhisper-selection-", StringComparison.Ordinal))
            {
                SelectionMarker = text;
                MarkerReadsCompleted = 0;
            }
            else
            {
                SelectionMarker = null;
            }

            return Task.CompletedTask;
        }

        public Task ClearClipboardTextAsync()
        {
            ClipboardText = null;
            SelectionMarker = null;
            return Task.CompletedTask;
        }

        public Task DelayAsync(TimeSpan delay) => Task.CompletedTask;

        public bool IsAnyModifierKeyDown() => false;

        public IntPtr GetForegroundWindow() => ForegroundWindow;

        public bool SetForegroundWindow(IntPtr hwnd)
        {
            LastSetForegroundWindow = hwnd;
            if (SetForegroundWindowResult)
                ForegroundWindow = hwnd;

            return SetForegroundWindowResult;
        }

        public uint GetWindowProcessId(IntPtr hwnd) => 0;

        public uint SendModifierKeyUpInputs() => 0;

        public uint SendForegroundActivationInput() => 0;

        public uint SendCopyInput()
        {
            CopyInputCalls++;
            return CopyInputResult;
        }

        public uint SendPasteInput() => PasteInputResult;

        public uint SendEnterInput() => EnterInputResult;
    }
}
