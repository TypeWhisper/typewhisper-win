using Moq;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Obsidian.Tests;

public sealed class ObsidianNoteWriterTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "obsidian-writer-" + Guid.NewGuid().ToString("N"));
    private string Vault => Path.Combine(_root, "vault");
    public ObsidianNoteWriterTests(Xunit.Abstractions.ITestOutputHelper output)
    { _output = output; Directory.CreateDirectory(Vault); }

    [Fact]
    public async Task ReplacedNoteFolderCannotDeleteSameNamedForeignFileDuringCleanup()
    {
        var outside = Path.Combine(_root, "outside"); Directory.CreateDirectory(outside);
        var probe = Path.Combine(_root, "link-capability-probe");
        try { Directory.CreateSymbolicLink(probe, outside); }
        catch (Exception ex) when (OperatingSystem.IsWindows() &&
            (ex is UnauthorizedAccessException or PlatformNotSupportedException || ex is IOException && (ex.HResult & 0xffff) == 1314))
        {
            _output.WriteLine("Symlink regression not exercised: this Windows runner lacks directory-symlink support or privilege.");
            return;
        }
        Directory.Delete(probe);
        var notes = Path.Combine(Vault, "Notes");
        var displaced = Path.Combine(_root, "original-notes");
        string? foreign = null;
        string? ownedTemporary = null;
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => ObsidianNoteWriter.WriteAsync(Vault, "Notes", "note", "owned transcript", default,
                beforeCommit: () =>
                {
                    var name = Path.GetFileName(Assert.Single(Directory.GetFiles(notes, "*.tmp")));
                    foreign = Path.Combine(outside, name); File.WriteAllText(foreign, "foreign data must survive");
                    Directory.Move(notes, displaced);
                    ownedTemporary = Path.Combine(displaced, name);
                    Directory.CreateSymbolicLink(notes, outside);
                    return Task.CompletedTask;
                }));
            Assert.Equal("foreign data must survive", File.ReadAllText(foreign!));
            Assert.Equal("owned transcript", File.ReadAllText(ownedTemporary!));
            Assert.Empty(Directory.GetFiles(outside, "*.md"));
        }
        finally
        {
            if (Directory.Exists(notes) && (File.GetAttributes(notes) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(notes);
        }
    }

    [Fact]
    public async Task ConcurrentSameNamesNeverOverwriteAndPreserveUnicode()
    {
        const int count = 24;
        var arrived = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = Enumerable.Range(0, count).Select(index => ObsidianNoteWriter.WriteAsync(
            Vault, "Notes", "Grüße", $"content {index}", default, beforeCommit: async () =>
            {
                if (Interlocked.Increment(ref arrived) == count) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            })).ToArray();
        var paths = await Task.WhenAll(writes);
        Assert.Equal(count, paths.Distinct().Count());
        for (var index = 0; index < count; index++) Assert.Equal($"content {index}", File.ReadAllText(paths[index]));
        Assert.All(paths, path => Assert.StartsWith("Grüße", Path.GetFileName(path)));
        Assert.Empty(Directory.GetFiles(Vault, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("/outside")]
    [InlineData("C:\\outside")]
    [InlineData("folder/../outside")]
    public async Task InvalidSubfolderCannotEscapeVault(string subfolder)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ObsidianNoteWriter.WriteAsync(Vault, subfolder, "note", "text", default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Vault));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("NUL.txt", "_NUL.txt")]
    [InlineData("a:b/c\\d?", "a_b_c_d_")]
    [InlineData("...", "Transcription")]
    public void WindowsFilenameRulesAreIdenticalOnEveryTestPlatform(string value, string expected)
        => Assert.Equal(expected, ObsidianNoteWriter.SafeFilename(value));

    [Fact]
    public async Task CancelBeforeAtomicCommitLeavesNoNoteOrTemporaryFile()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = ObsidianNoteWriter.WriteAsync(Vault, "", "note", "text", cancellation.Token,
            async () => { entered.SetResult(); await release.Task; });
        await entered.Task;
        Assert.Single(Directory.GetFiles(Vault, "*.tmp"));
        cancellation.Cancel(); release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Vault));
    }

    [Fact]
    public async Task CancelImmediatelyAfterCommitStillReportsSavedFile()
    {
        using var cancellation = new CancellationTokenSource();
        var path = await ObsidianNoteWriter.WriteAsync(Vault, "", "note", "committed", cancellation.Token,
            afterCommit: cancellation.Cancel);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("committed", File.ReadAllText(path));
    }

    [Fact]
    public async Task CommitFailureLeavesExistingDirectoryAndNoTemporaryFile()
    {
        Directory.CreateDirectory(Path.Combine(Vault, "note.md"));
        var error = await Record.ExceptionAsync(() => ObsidianNoteWriter.WriteAsync(Vault, "", "note", "text", default));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(Path.Combine(Vault, "note.md")));
        Assert.Empty(Directory.GetFiles(Vault));
    }

    [Fact]
    public async Task RealPluginPersistsSettingsAndCreatesANoteWithoutOpeningAUrl()
    {
        using (var plugin = new ObsidianPlugin())
        {
            await plugin.ActivateAsync(new VocabularyHostServices(Path.Combine(_root, "settings")));
            await plugin.SaveTextSettingAsync("vault-path", Vault, default);
            await plugin.SaveTextSettingAsync("filename-template", "My note", default);
        }
        using var restarted = new ObsidianPlugin();
        await restarted.ActivateAsync(new VocabularyHostServices(Path.Combine(_root, "settings")));
        var result = await restarted.ExecuteAsync("Reviewed text", new(null, null, null, "en", null), default);
        Assert.True(result.Success, result.Message);
        Assert.Null(result.Url);
        var path = Assert.Single(Directory.GetFiles(Vault, "*.md", SearchOption.AllDirectories));
        Assert.Contains("Reviewed text", File.ReadAllText(path));
        Assert.Equal("My note.md", Path.GetFileName(path));
    }

    [Fact]
    public async Task ExistingDailyModeIsPreservedAndExplicitlyRejected()
    {
        var host = new VocabularyHostServices(Path.Combine(_root, "settings"));
        host.SetSetting("vault-path", Vault); host.SetSetting("daily-note-mode", true);
        using var plugin = new ObsidianPlugin(); await plugin.ActivateAsync(host);
        var result = await plugin.ExecuteAsync("do not append", new(null, null, null, null, null), default);
        Assert.False(result.Success);
        Assert.Contains("Daily-note", result.Message);
        Assert.True(host.GetSetting<bool>("daily-note-mode"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Vault));
    }

    [Fact]
    public async Task FailedSettingsWriteKeepsThePreviouslyPublishedValue()
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(item => item.GetSetting<string>("filename-template")).Returns("Existing note");
        host.Setup(item => item.SetSetting("filename-template", It.IsAny<string>())).Throws(new IOException("disk failure"));
        using var plugin = new ObsidianPlugin(); await plugin.ActivateAsync(host.Object);
        await Assert.ThrowsAsync<IOException>(async () => await plugin.SaveTextSettingAsync("filename-template", "Changed", default));
        Assert.Equal("Existing note", plugin.TextSettings.Single(field => field.Id == "filename-template").Value);
    }

    [Fact]
    public async Task InvalidSettingsAndCanceledSaveDoNotMutatePersistedConfiguration()
    {
        var host = new VocabularyHostServices(Path.Combine(_root, "settings"));
        using var plugin = new ObsidianPlugin(); await plugin.ActivateAsync(host);
        await plugin.SaveTextSettingAsync("vault-path", Vault, default);
        await plugin.SaveTextSettingAsync("subfolder", "Notes", default);
        await Assert.ThrowsAsync<ArgumentException>(async () => await plugin.SaveTextSettingAsync("subfolder", "../outside", default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await plugin.SaveTextSettingAsync("note-mode", "daily-note", default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await plugin.SaveTextSettingAsync("subfolder", "Changed", canceled.Token));
        Assert.Equal("Notes", host.GetSetting<string>("subfolder"));
        Assert.Equal(Vault, host.GetSetting<string>("vault-path"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
