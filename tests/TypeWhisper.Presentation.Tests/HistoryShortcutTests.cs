using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryShortcutTests
{
    private sealed class Backend : IProcessingCancelShortcutBackend
    {
        public string Value { get; private set; } = "";
        internal string? Reject;
        public string? TryChange(string value)
        {
            if (value == Reject) return "Registration unavailable.";
            Value = value; return null;
        }
    }

    [Fact]
    public void UnassignedDefaultAndSavedChordReloadWithoutRewriting()
    {
        var folder = Path.Combine(Path.GetTempPath(), "history-shortcut-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "history.txt");
        try
        {
            var backend = new Backend(); var settings = new ProcessingCancelShortcut(path, backend, _ => null);
            Assert.Null(settings.Initialize()); Assert.Empty(settings.Value); Assert.False(File.Exists(path));
            Assert.Null(settings.Save("CTRL+ALT+H")); var before = File.ReadAllBytes(path);
            var reloaded = new ProcessingCancelShortcut(path, new Backend(), _ => null);
            Assert.Null(reloaded.Initialize()); Assert.Equal("CTRL+ALT+H", reloaded.Value);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void PersistenceFailureRollsRegistrationBackAndRemovesTemporaryFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "history-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); var path = Path.Combine(folder, "history.txt");
        try
        {
            var backend = new Backend(); var settings = new ProcessingCancelShortcut(path, backend, _ => null);
            Assert.Null(settings.Save("CTRL+ALT+H")); File.Delete(path); Directory.CreateDirectory(path);
            Assert.NotNull(settings.Save("CTRL+ALT+J")); Assert.Equal("CTRL+ALT+H", settings.Value);
            Assert.Empty(Directory.GetFiles(folder)); Assert.True(Directory.Exists(path));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void UnavailableNativeRegistrationLeavesExistingPersistenceUnchanged()
    {
        var path = Path.GetTempFileName();
        try
        {
            var backend = new Backend(); var settings = new ProcessingCancelShortcut(path, backend, _ => null);
            Assert.Null(settings.Save("CTRL+ALT+H")); backend.Reject = "CTRL+ALT+J";
            Assert.NotNull(settings.Save("CTRL+ALT+J")); Assert.Equal("CTRL+ALT+H", File.ReadAllText(path));
            Assert.Equal("CTRL+ALT+H", settings.Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SavedConflictNeverRegistersAndDoesNotRewritePreference()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "CTRL+ALT+H"); var backend = new Backend();
            var settings = new ProcessingCancelShortcut(path, backend, chord =>
                ProcessingCancelShortcut.Conflicts(chord, "CTRL+ALT", true) ? "Dictation conflict." : null);
            Assert.Equal("Dictation conflict.", settings.Initialize()); Assert.Empty(backend.Value);
            Assert.Equal("CTRL+ALT+H", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FailedRollbackReportsActualRegistrationWithoutClaimingPreviousValue()
    {
        var folder = Path.Combine(Path.GetTempPath(), "history-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); var path = Path.Combine(folder, "history.txt");
        try
        {
            var backend = new Backend(); var settings = new ProcessingCancelShortcut(path, backend, _ => null);
            Assert.Null(settings.Save("CTRL+ALT+H")); File.Delete(path); Directory.CreateDirectory(path);
            backend.Reject = "CTRL+ALT+H";
            Assert.Contains("could not be restored", settings.Save("CTRL+ALT+J"));
            Assert.Equal("CTRL+ALT+J", settings.Value); Assert.Empty(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void SharedControllerLabelsHistoryLoadAndSaveFailuresCorrectly()
    {
        var folder = Path.Combine(Path.GetTempPath(), "history-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var settings = new ProcessingCancelShortcut(folder, new Backend(), _ => null, "Recent transcription shortcuts");
            Assert.StartsWith("Recent transcription shortcuts", settings.Initialize());
            Assert.StartsWith("Recent transcription shortcuts", settings.Save("CTRL+ALT+H"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("CTRL+ALT+H", "CTRL+ALT+H", false, true)]
    [InlineData("CTRL+ALT+H", "CTRL+ALT", true, true)]
    [InlineData("CTRL+ALT+H", "CTRL+SHIFT", true, false)]
    [InlineData("CTRL+ALT+H", "CTRL+ALT+J", false, false)]
    public void SharedConflictRulesCoverOrdinaryChordsAndDictationModifierPrefixes(string history, string other, bool modifiers, bool conflict)
        => Assert.Equal(conflict, ProcessingCancelShortcut.Conflicts(history, other, modifiers));

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ClosingRunningWorkAndOtherWorkspacesRejectNavigation(bool closing, bool busy, bool workspace)
        => Assert.NotNull(HistoryShortcutAdmission.Rejection(closing, busy, workspace));

    [Fact]
    public void IdleLauncherAndExistingHistoryAllowNavigation()
        => Assert.Null(HistoryShortcutAdmission.Rejection(false, false, false));
}
