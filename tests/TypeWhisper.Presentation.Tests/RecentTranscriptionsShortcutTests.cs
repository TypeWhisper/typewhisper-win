using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class RecentTranscriptionsShortcutTests
{
    private sealed class Backend : IShortcutRegistrationBackend
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
        var folder = Path.Combine(Path.GetTempPath(), "recent-transcriptions-shortcut-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "recent-transcriptions.txt");
        try
        {
            var backend = new Backend(); var settings = new PersistedShortcut(path, backend, _ => null, "Recent transcription shortcuts");
            Assert.Null(settings.Initialize()); Assert.Empty(settings.Value); Assert.False(File.Exists(path));
            Assert.Null(settings.Save("CTRL+ALT+H")); var before = File.ReadAllBytes(path);
            var reloaded = new PersistedShortcut(path, new Backend(), _ => null, "Recent transcription shortcuts");
            Assert.Null(reloaded.Initialize()); Assert.Equal("CTRL+ALT+H", reloaded.Value);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void PersistenceFailureRollsRegistrationBackAndRemovesTemporaryFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "recent-transcriptions-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); var path = Path.Combine(folder, "recent-transcriptions.txt");
        try
        {
            var backend = new Backend(); var settings = new PersistedShortcut(path, backend, _ => null, "Recent transcription shortcuts");
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
            var backend = new Backend(); var settings = new PersistedShortcut(path, backend, _ => null, "Recent transcription shortcuts");
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
            var settings = new PersistedShortcut(path, backend, chord =>
                GlobalShortcuts.Overlap(chord, false, "CTRL+ALT", true) ? "Dictation conflict." : null, "Recent transcription shortcuts");
            Assert.Equal("Dictation conflict.", settings.Initialize()); Assert.Empty(backend.Value);
            Assert.Equal("CTRL+ALT+H", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FailedRollbackReportsActualRegistrationWithoutClaimingPreviousValue()
    {
        var folder = Path.Combine(Path.GetTempPath(), "recent-transcriptions-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); var path = Path.Combine(folder, "recent-transcriptions.txt");
        try
        {
            var backend = new Backend(); var settings = new PersistedShortcut(path, backend, _ => null, "Recent transcription shortcuts");
            Assert.Null(settings.Save("CTRL+ALT+H")); File.Delete(path); Directory.CreateDirectory(path);
            backend.Reject = "CTRL+ALT+H";
            Assert.Contains("could not be restored", settings.Save("CTRL+ALT+J"));
            Assert.Equal("CTRL+ALT+J", settings.Value); Assert.Empty(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void SharedControllerLabelsRecentTranscriptionsLoadAndSaveFailuresCorrectly()
    {
        var folder = Path.Combine(Path.GetTempPath(), "recent-transcriptions-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var settings = new PersistedShortcut(folder, new Backend(), _ => null, "Recent transcription shortcuts");
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
    public void SharedConflictRulesCoverOrdinaryChordsAndDictationModifierPrefixes(string recent, string other, bool modifiers, bool conflict)
        => Assert.Equal(conflict, GlobalShortcuts.Overlap(recent, false, other, modifiers));
}
