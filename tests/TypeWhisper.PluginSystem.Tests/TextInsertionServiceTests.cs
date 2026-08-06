using System.Runtime.InteropServices;
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
        Assert.Equal(0, platform.TemporaryClipboardWrites);
        Assert.Equal(1, platform.PersistentClipboardWrites);
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
        Assert.Equal(1, platform.TemporaryClipboardWrites);
        Assert.Equal(0, platform.PersistentClipboardWrites);
        Assert.Equal(1, platform.RestoreClipboardCalls);
        Assert.Contains(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.Equal(
            ["temporary-write", "delay:100", "paste", "delay:500", "restore"],
            platform.InsertionEvents);
    }

    [Fact]
    public async Task SuccessfulPaste_KeepsTransientTextAvailableForSlowTargetUntilRestoreDelay()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            ReadClipboardDuringRestoreDelay = true,
            SimulatedTargetReadOffset = TimeSpan.FromMilliseconds(400)
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("dictated");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal("dictated", platform.TextReadByTarget);
        Assert.Equal(TimeSpan.FromMilliseconds(400), platform.TargetReadOffsetObserved);
        Assert.Equal("previous", platform.ClipboardText);
        Assert.True(platform.LastTemporaryWriteExcludedFromHistory);
    }

    [Fact]
    public async Task AutoEnter_RestoresOnlyAfterPasteEnterAndFullRestoreDelay()
    {
        var platform = new FakeTextInsertionPlatform { ClipboardText = "previous" };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("dictated", autoEnter: true);

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal(
            [
                "temporary-write",
                "delay:100",
                "paste",
                "delay:50",
                "enter",
                "delay:500",
                "restore"
            ],
            platform.InsertionEvents);
    }

    [Fact]
    public async Task ConcurrentClipboardOperations_AreSerializedAcrossTheRestoreWindow()
    {
        var restoreDelayEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRestoreDelay = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            RestoreDelayEntered = restoreDelayEntered,
            ContinueRestoreDelay = continueRestoreDelay
        };
        var sut = new TextInsertionService(platform);

        var paste = sut.InsertTextAsync("dictated");
        await restoreDelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var copy = sut.InsertTextAsync("copied", autoPaste: false);

        try
        {
            await Task.Yield();
            Assert.False(copy.IsCompleted);
        }
        finally
        {
            continueRestoreDelay.TrySetResult(true);
        }

        Assert.Equal(InsertionResult.Pasted, await paste);
        Assert.Equal(InsertionResult.CopiedToClipboard, await copy);
        Assert.Equal("copied", platform.ClipboardText);
        Assert.True(
            platform.InsertionEvents.IndexOf("restore")
            < platform.InsertionEvents.IndexOf("persistent-write"));
    }

    [Fact]
    public async Task SuccessfulPaste_RestoresAllClipboardFormats()
    {
        var platform = new FakeTextInsertionPlatform();
        platform.ClipboardFormats["UnicodeText"] = "previous";
        platform.ClipboardFormats["HTML Format"] = "<b>previous</b>";
        platform.ClipboardFormats["Rich Text Format"] = "{\\rtf1 previous}";
        platform.ClipboardFormats["FileDrop"] = "C:\\temp\\previous.txt";
        platform.ClipboardFormats["Bitmap"] = "bitmap-bytes";
        platform.ClipboardFormats["TypeWhisper.Test.Custom"] = "custom-bytes";
        var expected = platform.ClipboardFormats.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("dictated");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal(expected, platform.ClipboardFormats);
    }

    [Fact]
    public async Task SuccessfulPaste_DoesNotOverwriteNewerClipboardChange()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            ChangeClipboardDuringRestoreDelay = true
        };
        var errorLog = new FakeErrorLogService();
        var sut = new TextInsertionService(platform, errorLog);

        var result = await sut.InsertTextAsync("dictated");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal("newer", platform.ClipboardText);
        Assert.Equal(1, platform.RestoreClipboardCalls);
        Assert.Contains(errorLog.Entries, entry =>
            entry.Category == ErrorCategory.Insertion
            && entry.Message.Contains("changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SnapshotFailure_LeavesOriginalClipboardUntouched()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            BeginTemporaryClipboardException = new InvalidOperationException("unsupported format")
        };
        platform.ClipboardFormats["OwnerDisplay"] = "owner-dependent";
        var expected = platform.ClipboardFormats.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        var sut = new TextInsertionService(platform);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.InsertTextAsync("dictated"));

        Assert.Equal(expected, platform.ClipboardFormats);
        Assert.Equal(0, platform.TemporaryClipboardWrites);
    }

    [Fact]
    public async Task CancellationBeforePaste_RestoresClipboardImmediately()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            CancellationSource = cancellation,
            CancelDuringFocusDelay = true
        };
        var sut = new TextInsertionService(platform);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.InsertTextAsync("dictated", cancellationToken: cancellation.Token));

        Assert.Equal("previous", platform.ClipboardText);
        Assert.Equal(0, platform.PasteInputCalls);
        Assert.DoesNotContain(TimeSpan.FromMilliseconds(500), platform.Delays);
    }

    [Fact]
    public async Task CancellationAfterPaste_WaitsBeforeRestoringClipboard()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            CancellationSource = cancellation,
            CancelDuringRestoreDelay = true,
            ReadClipboardDuringRestoreDelay = true
        };
        var sut = new TextInsertionService(platform);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.InsertTextAsync("dictated", cancellationToken: cancellation.Token));

        Assert.Equal("dictated", platform.TextReadByTarget);
        Assert.Equal("previous", platform.ClipboardText);
        Assert.Contains(TimeSpan.FromMilliseconds(500), platform.Delays);
    }

    [Fact]
    public async Task RestoreFailure_StillReportsPasteAndLogsDiagnostic()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            RestoreClipboardException = new ExternalException("restore failed")
        };
        var errorLog = new FakeErrorLogService();
        var sut = new TextInsertionService(platform, errorLog);

        var result = await sut.InsertTextAsync("dictated");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Contains(errorLog.Entries, entry =>
            entry.Category == ErrorCategory.Insertion
            && entry.Message.Contains("restore", StringComparison.OrdinalIgnoreCase));
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
        platform.ClipboardFormats["HTML Format"] = "<b>previous</b>";
        platform.ClipboardFormats["Bitmap"] = "bitmap-bytes";
        var expected = platform.ClipboardFormats.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        var sut = new TextInsertionService(platform);

        var result = await sut.TryCaptureSelectedTextAsync(new IntPtr(321));

        Assert.Equal("selected text", result);
        Assert.Equal("previous", platform.ClipboardText);
        Assert.Equal(1, platform.CopyInputCalls);
        Assert.Equal(new IntPtr(321), platform.LastSetForegroundWindow);
        Assert.Contains(platform.ClipboardWrites, value => value.StartsWith("__typewhisper-selection-", StringComparison.Ordinal));
        Assert.Equal("previous", platform.ClipboardWrites[^1]);
        Assert.Equal(expected, platform.ClipboardFormats);
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

    [Fact]
    public async Task TryCaptureSelectedTextAsync_CopyInputFailureRestoresAllFormats()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            CopyInputResult = 0
        };
        platform.ClipboardFormats["HTML Format"] = "<b>previous</b>";
        platform.ClipboardFormats["TypeWhisper.Test.Custom"] = "custom-bytes";
        var expected = platform.ClipboardFormats.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value);
        var sut = new TextInsertionService(platform);

        var result = await sut.TryCaptureSelectedTextAsync();

        Assert.Null(result);
        Assert.Equal(expected, platform.ClipboardFormats);
        Assert.Equal(1, platform.RestoreClipboardCalls);
    }

    [Fact]
    public async Task TryCaptureSelectedTextAsync_ReadErrorStillRestoresAllFormats()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardText = "previous",
            ClipboardReadException = new ExternalException("read failed")
        };
        platform.ClipboardFormats["Bitmap"] = "bitmap-bytes";
        var expected = platform.ClipboardFormats.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value);
        var sut = new TextInsertionService(platform);

        await Assert.ThrowsAsync<ExternalException>(() => sut.TryCaptureSelectedTextAsync());

        Assert.Equal(expected, platform.ClipboardFormats);
        Assert.Equal(1, platform.RestoreClipboardCalls);
    }

    private sealed class FakeTextInsertionPlatform : ITextInsertionPlatform
    {
        public Dictionary<string, string> ClipboardFormats { get; } = [];
        public string? ClipboardText
        {
            get => ClipboardFormats.GetValueOrDefault("UnicodeText");
            set
            {
                if (value is null)
                    ClipboardFormats.Remove("UnicodeText");
                else
                    ClipboardFormats["UnicodeText"] = value;
            }
        }
        public List<string> ClipboardWrites { get; } = [];
        public List<TimeSpan> Delays { get; } = [];
        public List<string> InsertionEvents { get; } = [];
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
        public int TemporaryClipboardWrites { get; private set; }
        public int PersistentClipboardWrites { get; private set; }
        public int RestoreClipboardCalls { get; private set; }
        public bool LastTemporaryWriteExcludedFromHistory { get; private set; }
        public bool ReadClipboardDuringRestoreDelay { get; set; }
        public TimeSpan? SimulatedTargetReadOffset { get; set; }
        public TimeSpan? TargetReadOffsetObserved { get; private set; }
        public bool ChangeClipboardDuringRestoreDelay { get; set; }
        public string? TextReadByTarget { get; private set; }
        public CancellationTokenSource? CancellationSource { get; set; }
        public bool CancelDuringFocusDelay { get; set; }
        public bool CancelDuringRestoreDelay { get; set; }
        public TaskCompletionSource<bool>? RestoreDelayEntered { get; set; }
        public TaskCompletionSource<bool>? ContinueRestoreDelay { get; set; }
        public Exception? RestoreClipboardException { get; set; }
        public Exception? BeginTemporaryClipboardException { get; set; }
        public Exception? ClipboardReadException { get; set; }
        private string? SelectionMarker { get; set; }
        private int MarkerReadsCompleted { get; set; }
        private uint ClipboardSequenceNumber { get; set; } = 1;
        private bool RestoreDelayActionsCompleted { get; set; }

        public Task<ClipboardTextState> TryGetClipboardTextStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ClipboardReadException is not null)
                throw ClipboardReadException;

            if (SelectionMarker is not null && CopyInputCalls > 0)
            {
                if (CapturedSelectionText is null)
                {
                    ClipboardText = SelectionMarker;
                    return Task.FromResult(new ClipboardTextState(ClipboardText, ClipboardSequenceNumber));
                }

                if (MarkerReadsCompleted < MarkerReadsBeforeSelection)
                {
                    MarkerReadsCompleted++;
                    ClipboardText = SelectionMarker;
                    return Task.FromResult(new ClipboardTextState(ClipboardText, ClipboardSequenceNumber));
                }

                ClipboardFormats.Clear();
                ClipboardText = CapturedSelectionText;
                SelectionMarker = null;
                ClipboardSequenceNumber++;
            }

            return Task.FromResult(new ClipboardTextState(ClipboardText, ClipboardSequenceNumber));
        }

        public Task<IClipboardLease> BeginTemporaryClipboardTextAsync(
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeginTemporaryClipboardException is not null)
                throw BeginTemporaryClipboardException;

            var lease = new FakeClipboardLease(
                ClipboardFormats.ToDictionary(static pair => pair.Key, static pair => pair.Value));
            ClipboardFormats.Clear();
            ClipboardText = text;
            ClipboardFormats[WindowsClipboardTransaction.ExcludeClipboardContentFromMonitorProcessing] = "1";
            ClipboardWrites.Add(text);
            TemporaryClipboardWrites++;
            InsertionEvents.Add("temporary-write");
            LastTemporaryWriteExcludedFromHistory = true;
            ClipboardSequenceNumber++;
            lease.ExpectedSequenceNumber = ClipboardSequenceNumber;
            if (text.StartsWith("__typewhisper-selection-", StringComparison.Ordinal))
            {
                SelectionMarker = text;
                MarkerReadsCompleted = 0;
            }
            else
            {
                SelectionMarker = null;
            }
            return Task.FromResult<IClipboardLease>(lease);
        }

        public Task SetClipboardTextAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClipboardFormats.Clear();
            ClipboardText = text;
            ClipboardWrites.Add(text);
            PersistentClipboardWrites++;
            InsertionEvents.Add("persistent-write");
            ClipboardSequenceNumber++;
            SelectionMarker = null;
            return Task.CompletedTask;
        }

        public Task<bool> CommitTemporaryClipboardTextAsync(
            IClipboardLease lease,
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fakeLease = Assert.IsType<FakeClipboardLease>(lease);
            if (fakeLease.ExpectedSequenceNumber != ClipboardSequenceNumber)
            {
                fakeLease.Completed = true;
                return Task.FromResult(false);
            }

            ClipboardFormats.Clear();
            ClipboardText = text;
            ClipboardWrites.Add(text);
            PersistentClipboardWrites++;
            InsertionEvents.Add("fallback-write");
            ClipboardSequenceNumber++;
            SelectionMarker = null;
            fakeLease.Completed = true;
            return Task.FromResult(true);
        }

        public Task<ClipboardRestoreResult> RestoreClipboardAsync(
            IClipboardLease lease,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreClipboardCalls++;
            InsertionEvents.Add("restore");
            if (RestoreClipboardException is not null)
                throw RestoreClipboardException;

            var fakeLease = Assert.IsType<FakeClipboardLease>(lease);
            if (fakeLease.Completed)
                return Task.FromResult(ClipboardRestoreResult.Restored);

            if (fakeLease.ExpectedSequenceNumber != ClipboardSequenceNumber)
            {
                fakeLease.Completed = true;
                return Task.FromResult(ClipboardRestoreResult.ClipboardChanged);
            }

            ClipboardFormats.Clear();
            foreach (var pair in fakeLease.Snapshot)
                ClipboardFormats[pair.Key] = pair.Value;

            ClipboardSequenceNumber++;
            fakeLease.Completed = true;
            SelectionMarker = null;
            if (ClipboardText is { } restoredText)
                ClipboardWrites.Add(restoredText);
            else
                ClearClipboardCalls++;

            return Task.FromResult(ClipboardRestoreResult.Restored);
        }

        public void AcceptClipboardSequence(IClipboardLease lease, uint sequenceNumber)
        {
            var fakeLease = Assert.IsType<FakeClipboardLease>(lease);
            fakeLease.ExpectedSequenceNumber = sequenceNumber;
        }

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DelayCalls++;
            Delays.Add(delay);
            InsertionEvents.Add($"delay:{delay.TotalMilliseconds:0}");

            if (delay == TimeSpan.FromMilliseconds(100) && CancelDuringFocusDelay)
                CancellationSource?.Cancel();

            if (delay == TimeSpan.FromMilliseconds(500) && !RestoreDelayActionsCompleted)
            {
                RestoreDelayActionsCompleted = true;
                RestoreDelayEntered?.TrySetResult(true);
                if (ContinueRestoreDelay is not null)
                    await ContinueRestoreDelay.Task.WaitAsync(cancellationToken);

                if (ReadClipboardDuringRestoreDelay)
                {
                    var readOffset = SimulatedTargetReadOffset ?? TimeSpan.Zero;
                    Assert.True(readOffset <= delay);
                    TextReadByTarget = ClipboardText;
                    TargetReadOffsetObserved = readOffset;
                }
                if (ChangeClipboardDuringRestoreDelay)
                {
                    ClipboardFormats.Clear();
                    ClipboardText = "newer";
                    ClipboardSequenceNumber++;
                }
                if (CancelDuringRestoreDelay)
                    CancellationSource?.Cancel();
            }

            cancellationToken.ThrowIfCancellationRequested();
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
            InsertionEvents.Add("paste");
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
            InsertionEvents.Add("enter");
            return EnterInputResult;
        }

        private sealed class FakeClipboardLease(Dictionary<string, string> snapshot) : IClipboardLease
        {
            public Dictionary<string, string> Snapshot { get; } = snapshot;
            public uint ExpectedSequenceNumber { get; set; }
            public bool Completed { get; set; }
            public void Dispose() => Completed = true;
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
