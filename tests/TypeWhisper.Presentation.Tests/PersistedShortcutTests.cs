using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class PersistedShortcutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tw-cancel-shortcut-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(_root, "cancel.txt");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Backend : IShortcutRegistrationBackend
    {
        public string Value { get; private set; } = "";
        internal Func<string, string?>? Reject;
        internal int Calls;
        internal bool ReverseOrder;
        public string? TryChange(string value)
        { Calls++; if (Reject?.Invoke(value) is { } error) return error; Value = ReverseOrder ? string.Join(",", value.Split(',').Reverse()) : value; return null; }
    }

    [Fact]
    public void NativeBindingOrderDoesNotCauseRollbackAndActualCanonicalValueIsSaved()
    {
        var backend = new Backend { ReverseOrder = true };
        var settings = new PersistedShortcut(PathName, backend, _ => null, "Cancel shortcuts");
        Assert.Null(settings.Save("CTRL+X,CTRL+Y"));
        Assert.Equal("CTRL+Y,CTRL+X", settings.Value);
        Assert.Equal(settings.Value, File.ReadAllText(PathName)); Assert.Equal(1, backend.Calls);
    }

    [Fact]
    public void DefaultDoesNotRegisterAndSavedChoiceSurvivesRestart()
    {
        var first = new Backend(); var settings = new PersistedShortcut(PathName, first, _ => null, "Cancel shortcuts");
        Assert.Null(settings.Initialize()); Assert.Equal(0, first.Calls); Assert.False(File.Exists(PathName));
        Assert.Null(settings.Save("CTRL+ALT+X"));
        var bytes = File.ReadAllBytes(PathName); var restarted = new Backend();
        Assert.Null(new PersistedShortcut(PathName, restarted, _ => null, "Cancel shortcuts").Initialize());
        Assert.Equal("CTRL+ALT+X", restarted.Value); Assert.Equal(bytes, File.ReadAllBytes(PathName));
        Assert.Null(settings.Save("")); Assert.Equal("", first.Value); Assert.Equal("", File.ReadAllText(PathName));
    }

    [Fact]
    public void NativeConflictDoesNotChangePreviousRegistrationOrFile()
    {
        var backend = new Backend(); var settings = new PersistedShortcut(PathName, backend, _ => null, "Cancel shortcuts");
        Assert.Null(settings.Save("CTRL+X")); var before = File.ReadAllBytes(PathName);
        backend.Reject = value => value == "CTRL+Y" ? "Already registered by another app." : null;
        Assert.NotNull(settings.Save("CTRL+Y")); Assert.Equal("CTRL+X", settings.Value);
        Assert.Equal(before, File.ReadAllBytes(PathName));
    }

    [Fact]
    public void FileWriteFailureRollsNativeRegistrationBackAndCleansTemporaryFile()
    {
        var backend = new Backend(); var settings = new PersistedShortcut(PathName, backend, _ => null, "Cancel shortcuts");
        Assert.Null(settings.Save("CTRL+X")); File.Delete(PathName); Directory.CreateDirectory(PathName);
        Assert.NotNull(settings.Save("CTRL+Y")); Assert.Equal("CTRL+X", settings.Value);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void FailedRollbackReportsActualActiveBindingWithoutClaimingOldBinding()
    {
        var backend = new Backend(); var settings = new PersistedShortcut(PathName, backend, _ => null, "Cancel shortcuts");
        Assert.Null(settings.Save("CTRL+X")); File.Delete(PathName); Directory.CreateDirectory(PathName);
        backend.Reject = value => value == "CTRL+X" ? "Original shortcut became unavailable." : null;
        Assert.Contains("could not be restored", settings.Save("CTRL+Y"));
        Assert.Equal("CTRL+Y", settings.Value);
    }

    [Theory]
    [InlineData("CTRL+X", "CTRL+X", false, true)]
    [InlineData("CTRL+ALT+X", "CTRL+ALT", true, true)]
    [InlineData("CTRL+ALT+SHIFT+X", "CTRL+SHIFT", true, true)]
    [InlineData("CTRL+ALT+X", "CTRL+ALT", false, false)]
    [InlineData("CTRL+ALT+X", "CTRL+SHIFT", true, false)]
    [InlineData("CTRL+X,ALT+F9", "ALT+F9", false, true)]
    [InlineData("CTRL+SHIFT+X", "CTRL+X", true, false)]
    public void ConflictIncludesModifierOnlyDictationPrefixes(string cancel, string other, bool modifiers, bool expected)
        => Assert.Equal(expected, GlobalShortcuts.Overlap(cancel, false, other, modifiers));

    [Fact]
    public void ApplicationConflictIsRejectedBeforeAnyNativeOrDiskMutation()
    {
        var backend = new Backend(); var settings = new PersistedShortcut(PathName, backend,
            value => GlobalShortcuts.Overlap(value, false, "CTRL+SHIFT", true) ? "Dictation conflict" : null, "Cancel shortcuts");
        Assert.Equal("Dictation conflict", settings.Save("CTRL+SHIFT+X")); Assert.Equal(0, backend.Calls);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void OversizedAndControlCharacterPreferencesNeverRegister()
    {
        Directory.CreateDirectory(_root); File.WriteAllText(PathName, new string('x', 1025));
        var backend = new Backend(); var settings = new PersistedShortcut(PathName, backend, _ => null, "Cancel shortcuts");
        Assert.NotNull(settings.Initialize()); Assert.Equal(0, backend.Calls);
        File.WriteAllText(PathName, "CTRL+X\nALT+X");
        Assert.NotNull(settings.Initialize()); Assert.Equal(0, backend.Calls);
    }
}
