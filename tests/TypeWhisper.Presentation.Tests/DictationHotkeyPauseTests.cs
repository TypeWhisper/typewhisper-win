using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class DictationHotkeyPauseTests
{
    [Fact]
    public void DefaultIsActiveAndBothChoicesSurviveRestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hotkey-pause-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "pause.json");
        try
        {
            var store = new DictationHotkeyPauseStore(path);
            Assert.False(store.Current); Assert.False(File.Exists(path));
            Assert.Null(store.Save(true)); Assert.True(new DictationHotkeyPauseStore(path).Current);
            Assert.Null(store.Save(false)); Assert.False(new DictationHotkeyPauseStore(path).Current);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("{\"Paused\":true,\"Paused\":false}")]
    [InlineData("{\"Paused\":\"true\"}")]
    [InlineData("{}")]
    public void InvalidChoiceReportsErrorWithoutClaimingPaused(string json)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var store = new DictationHotkeyPauseStore(path);
            Assert.False(store.Current); Assert.NotNull(store.Error); Assert.Equal(json, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FailedSaveKeepsPausedStateAndCleansTemporaryFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hotkey-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); var path = Path.Combine(folder, "pause.json");
        try
        {
            var store = new DictationHotkeyPauseStore(path); Assert.Null(store.Save(true));
            File.Delete(path); Directory.CreateDirectory(path);
            Assert.NotNull(store.Save(false)); Assert.True(store.Current);
            Assert.Empty(Directory.GetFiles(folder)); Assert.True(Directory.Exists(path));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(RecordingMode.Hybrid)]
    [InlineData(RecordingMode.Toggle)]
    [InlineData(RecordingMode.Hold)]
    public void PausedGestureAndHeldResumeCannotStartUntilAllKeysReleased(RecordingMode mode)
    {
        var state = new HybridHotkeyState(); IReadOnlySet<string> bindings = new HashSet<string> { "CTRL+F9" };
        Assert.Null(state.Key(0x11, true, 0, bindings, mode: mode, paused: true));
        Assert.Null(state.Key(120, true, 1, bindings, mode: mode, paused: true));
        state.Suspend();
        Assert.Null(state.Key(120, true, 2, bindings, mode: mode));
        Assert.Null(state.Key(120, false, 3, bindings, mode: mode));
        Assert.Null(state.Key(120, true, 4, bindings, mode: mode));
        Assert.Null(state.Key(120, false, 5, bindings, mode: mode));
        Assert.Null(state.Key(0x11, false, 6, bindings, mode: mode));
        Assert.Null(state.Key(0x11, true, 7, bindings, mode: mode));
        Assert.Equal(HybridHotkeyAction.Start, state.Key(120, true, 8, bindings, mode: mode));
    }

    [Fact]
    public async Task PauseBeforeQueuedDispatchPreventsLateCaptureStart()
    {
        Action? dispatch = null; var paused = false; var starts = 0;
        var coordinator = new DictationInputCoordinator(() => { starts++; return Task.CompletedTask; },
            () => Task.CompletedTask, () => Task.CompletedTask, () => false, () => !paused,
            () => RecordingMode.Hybrid, action => { dispatch = action; return true; });
        var submitted = coordinator.SubmitAsync(DictationInputAction.Start);
        paused = true; dispatch!(); await submitted;
        Assert.Equal(0, starts);
    }
}
