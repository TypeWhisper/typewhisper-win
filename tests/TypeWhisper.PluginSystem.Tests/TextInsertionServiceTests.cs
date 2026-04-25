using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class TextInsertionServiceTests
{
    [Fact]
    public async Task AutoPasteDisabled_LeavesDictationInClipboardWithoutPasteInput()
    {
        var platform = new FakeTextInsertionPlatform { ClipboardText = "previous" };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("dictated", autoPaste: false);

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("dictated", platform.ClipboardText);
        Assert.Equal(0, platform.PasteInputCalls);
    }

    [Fact]
    public async Task ModifierTimeout_FallsBackToClipboardAndKeepsDictationAvailable()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            ModifierDefaultState = true
        };
        var errorLog = new FakeErrorLogService();
        var sut = new TextInsertionService(platform, errorLog);

        var result = await sut.InsertTextAsync("dictated");

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("dictated", platform.ClipboardText);
        Assert.Equal(0, platform.PasteInputCalls);
        Assert.Contains(errorLog.Entries, entry =>
            entry.Category == ErrorCategory.Insertion
            && entry.Message.Contains("modifier keys", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ModifierRelease_WaitsBeforeSendingPasteInput()
    {
        var platform = new FakeTextInsertionPlatform { ClipboardText = "previous" };
        platform.ModifierStates.Enqueue(true);
        platform.ModifierStates.Enqueue(true);
        platform.ModifierStates.Enqueue(false);
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("dictated");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal(1, platform.PasteInputCalls);
        Assert.Equal("previous", platform.ClipboardText);
        Assert.True(platform.DelayCalls >= 3);
    }

    [Fact]
    public async Task FocusFailure_FallsBackToClipboardWithoutPasteInput()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            ForegroundWindow = new IntPtr(100),
            SetForegroundWindowResult = false
        };
        var errorLog = new FakeErrorLogService();
        var sut = new TextInsertionService(platform, errorLog);

        var result = await sut.InsertTextAsync("dictated", targetHwnd: new IntPtr(200));

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("dictated", platform.ClipboardText);
        Assert.Equal(0, platform.PasteInputCalls);
        Assert.Equal(new IntPtr(200), platform.LastSetForegroundWindow);
        Assert.Contains(errorLog.Entries, entry =>
            entry.Category == ErrorCategory.Insertion
            && entry.Message.Contains("target window", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PasteInputFailure_FallsBackToClipboardWithoutRestoringPreviousClipboard()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            PasteInputResult = 0
        };
        var errorLog = new FakeErrorLogService();
        var sut = new TextInsertionService(platform, errorLog);

        var result = await sut.InsertTextAsync("dictated");

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("dictated", platform.ClipboardText);
        Assert.Equal(1, platform.PasteInputCalls);
        Assert.Contains(errorLog.Entries, entry =>
            entry.Category == ErrorCategory.Insertion
            && entry.Message.Contains("Ctrl+V", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SuccessfulPaste_RestoresPreviousClipboard()
    {
        var platform = new FakeTextInsertionPlatform { ClipboardText = "previous" };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("dictated");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal("previous", platform.ClipboardText);
        Assert.Equal(1, platform.PasteInputCalls);
        Assert.Equal(["dictated", "previous"], platform.ClipboardWrites);
    }

    [Fact]
    public async Task EnterInputFailure_StillReportsPasteAndLogsDiagnostic()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            EnterInputResult = 0
        };
        var errorLog = new FakeErrorLogService();
        var sut = new TextInsertionService(platform, errorLog);

        var result = await sut.InsertTextAsync("dictated", autoEnter: true);

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal("previous", platform.ClipboardText);
        Assert.Equal(1, platform.EnterInputCalls);
        Assert.Contains(errorLog.Entries, entry =>
            entry.Category == ErrorCategory.Insertion
            && entry.Message.Contains("Enter", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeTextInsertionPlatform : ITextInsertionPlatform
    {
        public string? ClipboardText { get; set; }
        public List<string> ClipboardWrites { get; } = [];
        public Queue<bool> ModifierStates { get; } = [];
        public bool ModifierDefaultState { get; set; }
        public IntPtr ForegroundWindow { get; set; }
        public bool SetForegroundWindowResult { get; set; } = true;
        public IntPtr LastSetForegroundWindow { get; private set; }
        public uint PasteInputResult { get; set; } = 4;
        public uint EnterInputResult { get; set; } = 2;
        public int PasteInputCalls { get; private set; }
        public int EnterInputCalls { get; private set; }
        public int DelayCalls { get; private set; }

        public Task<string?> TryGetClipboardTextAsync() => Task.FromResult(ClipboardText);

        public Task SetClipboardTextAsync(string text)
        {
            ClipboardText = text;
            ClipboardWrites.Add(text);
            return Task.CompletedTask;
        }

        public Task DelayAsync(TimeSpan delay)
        {
            DelayCalls++;
            return Task.CompletedTask;
        }

        public bool IsAnyModifierKeyDown() =>
            ModifierStates.Count > 0 ? ModifierStates.Dequeue() : ModifierDefaultState;

        public IntPtr GetForegroundWindow() => ForegroundWindow;

        public bool SetForegroundWindow(IntPtr hwnd)
        {
            LastSetForegroundWindow = hwnd;
            if (SetForegroundWindowResult)
                ForegroundWindow = hwnd;

            return SetForegroundWindowResult;
        }

        public uint SendPasteInput()
        {
            PasteInputCalls++;
            return PasteInputResult;
        }

        public uint SendEnterInput()
        {
            EnterInputCalls++;
            return EnterInputResult;
        }
    }

    private sealed class FakeErrorLogService : IErrorLogService
    {
        private readonly List<ErrorLogEntry> _entries = [];

        public IReadOnlyList<ErrorLogEntry> Entries => _entries;

        public event Action? EntriesChanged;

        public void AddEntry(string message, string category = "general")
        {
            _entries.Add(ErrorLogEntry.Create(message, category));
            EntriesChanged?.Invoke();
        }

        public void ClearAll() => _entries.Clear();

        public string ExportDiagnostics() => "";
    }
}
