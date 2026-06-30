using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Native;
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
        Assert.Equal(1, platform.ModifierKeyUpInputCalls);
        Assert.Contains(errorLog.Entries, entry =>
            entry.Category == ErrorCategory.Insertion
            && entry.Message.Contains("modifier keys", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ModifierTimeout_NormalizesStuckModifiersBeforeFallingBack()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            ModifierDefaultState = true,
            ModifierKeyUpInputClearsState = true
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("dictated");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal("previous", platform.ClipboardText);
        Assert.Equal(1, platform.ModifierKeyUpInputCalls);
        Assert.Equal(1, platform.PasteInputCalls);
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
    public async Task FocusFailure_UsesForegroundActivationRetryBeforeFallingBack()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            ForegroundWindow = new IntPtr(100),
            SetForegroundWindowResults = new Queue<bool>([false, true])
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("dictated", targetHwnd: new IntPtr(200));

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal("previous", platform.ClipboardText);
        Assert.Equal(2, platform.SetForegroundWindowCalls);
        Assert.Equal(1, platform.ForegroundActivationInputCalls);
        Assert.Equal(1, platform.PasteInputCalls);
    }

    [Fact]
    public async Task FocusFailure_FallsBackWhenSetForegroundWindowReportsSuccessWithoutForeground()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            ForegroundWindow = new IntPtr(100),
            SetForegroundWindowResult = true,
            MoveForegroundOnSetForegroundWindowSuccess = false,
            ForegroundActivationInputResult = 0
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("dictated", targetHwnd: new IntPtr(200));

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("dictated", platform.ClipboardText);
        Assert.Equal(0, platform.PasteInputCalls);
        Assert.Equal(1, platform.SetForegroundWindowCalls);
    }

    [Fact]
    public async Task FocusRetry_AcceptsForegroundWindowMovedWithinSameProcess()
    {
        var targetHwnd = new IntPtr(200);
        var currentForegroundHwnd = new IntPtr(300);
        var rootHwnd = new IntPtr(100);
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            ForegroundWindow = currentForegroundHwnd,
            SetForegroundWindowResult = false,
            WindowProcessIds =
            {
                [targetHwnd] = 42,
                [currentForegroundHwnd] = 42
            },
            RootWindows =
            {
                [targetHwnd] = rootHwnd,
                [currentForegroundHwnd] = rootHwnd
            }
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("dictated", targetHwnd: targetHwnd);

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal("previous", platform.ClipboardText);
        Assert.Equal(1, platform.PasteInputCalls);
    }

    [Fact]
    public async Task FocusRetry_DoesNotAcceptDifferentForegroundWindowFromSameProcess()
    {
        var targetHwnd = new IntPtr(200);
        var otherWindowHwnd = new IntPtr(300);
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            ForegroundWindow = otherWindowHwnd,
            SetForegroundWindowResult = false,
            WindowProcessIds =
            {
                [targetHwnd] = 42,
                [otherWindowHwnd] = 42
            }
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("dictated", targetHwnd: targetHwnd);

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("dictated", platform.ClipboardText);
        Assert.Equal(0, platform.PasteInputCalls);
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

    [Fact]
    public void WindowsTextInsertionPlatform_KeyInput_MarksAppGeneratedInput()
    {
        var keyDown = WindowsTextInsertionPlatform.KeyInput(NativeMethods.VK_V, keyUp: false);
        var keyUp = WindowsTextInsertionPlatform.KeyInput(NativeMethods.VK_V, keyUp: true);

        Assert.Equal(NativeMethods.SelfInjectedInputMarker, keyDown.u.ki.dwExtraInfo);
        Assert.Equal(NativeMethods.SelfInjectedInputMarker, keyUp.u.ki.dwExtraInfo);
        Assert.Equal(0u, keyDown.u.ki.dwFlags);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, keyUp.u.ki.dwFlags);
    }

    [Fact]
    public async Task TryCaptureSelectedTextAsync_ReturnsCopiedSelectionAndRestoresPreviousClipboard()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            CapturedSelectionText = "selected text",
            MarkerReadsBeforeSelection = 1
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.TryCaptureSelectedTextAsync(new IntPtr(321));

        Assert.Equal("selected text", result);
        Assert.Equal("previous", platform.ClipboardText);
        Assert.Equal(1, platform.CopyInputCalls);
        Assert.Equal(new IntPtr(321), platform.LastSetForegroundWindow);
        Assert.Contains(platform.ClipboardWrites, value => value.StartsWith("__typewhisper-selection-", StringComparison.Ordinal));
        Assert.Equal("previous", platform.ClipboardWrites[^1]);
    }

    [Fact]
    public async Task TryCaptureSelectedTextAsync_ReturnsNullWhenClipboardNeverChanges()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous"
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.TryCaptureSelectedTextAsync(new IntPtr(44));

        Assert.Null(result);
        Assert.Equal("previous", platform.ClipboardText);
        Assert.Equal(1, platform.CopyInputCalls);
    }

    [Fact]
    public async Task TryCaptureSelectedTextAsync_ClearsClipboardWhenNoPreviousClipboardExists()
    {
        var platform = new FakeTextInsertionPlatform();
        var sut = new TextInsertionService(platform);

        var result = await sut.TryCaptureSelectedTextAsync();

        Assert.Null(result);
        Assert.Null(platform.ClipboardText);
        Assert.Equal(1, platform.ClearClipboardCalls);
    }

    private sealed class FakeTextInsertionPlatform : ITextInsertionPlatform
    {
        public string? ClipboardText { get; set; }
        public List<string> ClipboardWrites { get; } = [];
        public Queue<bool> ModifierStates { get; } = [];
        public bool ModifierDefaultState { get; set; }
        public bool ModifierKeyUpInputClearsState { get; set; }
        public IntPtr ForegroundWindow { get; set; }
        public bool SetForegroundWindowResult { get; set; } = true;
        public Queue<bool> SetForegroundWindowResults { get; set; } = [];
        public bool MoveForegroundOnSetForegroundWindowSuccess { get; set; } = true;
        public Dictionary<IntPtr, uint> WindowProcessIds { get; set; } = [];
        public Dictionary<IntPtr, IntPtr> RootWindows { get; set; } = [];
        public IntPtr LastSetForegroundWindow { get; private set; }
        public uint PasteInputResult { get; set; } = 4;
        public uint EnterInputResult { get; set; } = 2;
        public uint CopyInputResult { get; set; } = 4;
        public uint ModifierKeyUpInputResult { get; set; } = 1;
        public uint ForegroundActivationInputResult { get; set; } = 2;
        public string? CapturedSelectionText { get; set; }
        public int MarkerReadsBeforeSelection { get; set; }
        public int PasteInputCalls { get; private set; }
        public int EnterInputCalls { get; private set; }
        public int CopyInputCalls { get; private set; }
        public int ModifierKeyUpInputCalls { get; private set; }
        public int ForegroundActivationInputCalls { get; private set; }
        public int SetForegroundWindowCalls { get; private set; }
        public int ClearClipboardCalls { get; private set; }
        public int DelayCalls { get; private set; }
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
            ClipboardWrites.Add(text);
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
            ClearClipboardCalls++;
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
            SetForegroundWindowCalls++;
            LastSetForegroundWindow = hwnd;
            var result = SetForegroundWindowResults.Count > 0
                ? SetForegroundWindowResults.Dequeue()
                : SetForegroundWindowResult;
            if (result && MoveForegroundOnSetForegroundWindowSuccess)
                ForegroundWindow = hwnd;

            return result;
        }

        public uint GetWindowProcessId(IntPtr hwnd) =>
            WindowProcessIds.GetValueOrDefault(hwnd);

        public IntPtr GetRootWindow(IntPtr hwnd) =>
            RootWindows.GetValueOrDefault(hwnd, hwnd);

        public uint SendModifierKeyUpInputs()
        {
            ModifierKeyUpInputCalls++;
            if (ModifierKeyUpInputClearsState)
                ModifierDefaultState = false;

            return ModifierKeyUpInputResult;
        }

        public uint SendForegroundActivationInput()
        {
            ForegroundActivationInputCalls++;
            return ForegroundActivationInputResult;
        }

        public uint SendPasteInput()
        {
            PasteInputCalls++;
            return PasteInputResult;
        }

        public uint SendCopyInput()
        {
            CopyInputCalls++;
            return CopyInputResult;
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
