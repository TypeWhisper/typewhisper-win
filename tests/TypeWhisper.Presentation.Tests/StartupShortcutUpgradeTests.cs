using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class StartupShortcutUpgradeTests
{
    [Fact]
    public async Task ExistingShortcutRemainsEnabledWithoutDuplicatingStartup()
    {
        var backend = new Backend();
        var registry = new StartupRegistration(backend, "TypeWhisper", Path.GetFullPath("TypeWhisper.exe"));
        var shortcut = true;
        var startup = new StartupRegistrationWithShortcut(registry, () => shortcut, () => shortcut = false);
        Assert.True((await startup.ReadAsync()).IsEnabled);
        Assert.True((await startup.SetEnabledAsync(true)).IsEnabled);
        Assert.Null(backend.Command);
        Assert.True(shortcut);
        Assert.False((await startup.SetEnabledAsync(false)).IsEnabled);
        Assert.False(shortcut);
        Assert.True((await startup.SetEnabledAsync(true)).IsEnabled);
        Assert.NotNull(backend.Command);
    }

    [Fact]
    public async Task UnreadableShortcutDoesNotAllowChangingStartup()
    {
        var registry = new StartupRegistration(new Backend(), "TypeWhisper", Path.GetFullPath("TypeWhisper.exe"));
        var startup = new StartupRegistrationWithShortcut(registry, () => throw new IOException("unavailable"), () => throw new Exception());
        var state = await startup.SetEnabledAsync(false);
        Assert.False(state.CanChange);
        Assert.NotNull(state.Error);
    }

    private sealed class Backend : IStartupRegistrationBackend
    {
        internal string? Command;
        public string? Read(string identity) => Command;
        public void Write(string identity, string command) => Command = command;
        public void Delete(string identity) => Command = null;
    }
}
