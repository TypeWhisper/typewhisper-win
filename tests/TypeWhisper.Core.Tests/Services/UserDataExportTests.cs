using System.IO.Compression;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Services.UserData;

namespace TypeWhisper.Core.Tests.Services;

public sealed class UserDataExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TypeWhisper-export-tests-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_directory, "profile");
    private string Destination => Path.Combine(_directory, "out", "export.zip");

    public UserDataExportTests()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* A leftover temp folder must not fail the test run. */ }
    }

    private void Write(string relative, string content = "x")
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string[] Entries()
    {
        using var archive = ZipFile.OpenRead(Destination);
        return archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task ArchiveHoldsUserDataAndLeavesSecretsModelsAndInternalsOut()
    {
        new HistoryService(Path.Combine(Root, "history.json")).AddRecord(new TranscriptionRecord
        { Id = "record", Timestamp = DateTime.UtcNow, RawText = "raw", FinalText = "hello export" });
        Write("history-audio/history-1.wav", "audio");
        Write("recordings/meeting.wav", "audio");
        Write("Dictation/settings.json", "{}");
        Write("PluginData/com.example/settings.json", "{}");
        Write("PluginData/com.example/memories.json", "[]");
        Write("PluginData/com.example/0123.secret", "cipher");
        Write("PluginData/com.example/Models/model.bin", "weights");
        Write("PluginPackages/installed.json", "{}");
        Write("HttpApi/Uploads/upload.wav", "audio");
        Write("licenses.dat", "cipher");
        Write("premium-account.dat", "cipher");
        Write("api-discovery.json", "{\"token\":\"abc\"}");
        Write(".backup-preview-0123/history.json", "[]");
        Write("settings.json.0123.tmp", "partial");

        var result = await UserDataExport.ExportAsync(Root, Destination);

        Assert.True(result.IncludesBackup);
        Assert.Empty(result.Skipped);
        Assert.Equal(6, result.Files);
        Assert.Equal(
        [
            "README.txt",
            "profile/Dictation/settings.json",
            "profile/PluginData/com.example/memories.json",
            "profile/PluginData/com.example/settings.json",
            "profile/history-audio/history-1.wav",
            "profile/history.json",
            "profile/recordings/meeting.wav",
            "typewhisper-backup.json",
        ], Entries());
        using var archive = ZipFile.OpenRead(Destination);
        using var backup = new StreamReader(archive.GetEntry(UserDataExport.BackupEntryName)!.Open());
        Assert.Contains("hello export", await backup.ReadToEndAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(Destination)!, "*.tmp"));
        Assert.Equal([".backup-preview-0123"], Directory.EnumerateDirectories(Root, ".backup-preview-*").Select(Path.GetFileName));
    }

    [Fact]
    public async Task ExportReplacesAnExistingFileAtTheChosenPath()
    {
        Write("dictionary.json", "[]");
        File.WriteAllText(Destination, "old export");

        await UserDataExport.ExportAsync(Root, Destination);

        Assert.Contains("profile/dictionary.json", Entries());
    }

    [Fact]
    public async Task DestinationInsideTheDataFolderIsRefused()
    {
        Write("history.json", "[]");

        await Assert.ThrowsAsync<ArgumentException>(() => UserDataExport.ExportAsync(Root, Path.Combine(Root, "export.zip")));
        await Assert.ThrowsAsync<ArgumentException>(() => UserDataExport.ExportAsync(Root, Root));
        Assert.Equal(["history.json"], Directory.EnumerateFileSystemEntries(Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task DestinationReachedThroughALinkIntoTheDataFolderIsRefused()
    {
        var link = Path.Combine(_directory, "shortcut");
        LegacyDailyProfileMigrationTests.Link(link, Root);

        await Assert.ThrowsAsync<ArgumentException>(() => UserDataExport.ExportAsync(Root, Path.Combine(link, "export.zip")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
        Directory.Delete(link);
    }

    [Fact]
    public async Task LinksInsideTheDataFolderAreNotFollowed()
    {
        var outside = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.txt"), "not TypeWhisper data");
        LegacyDailyProfileMigrationTests.Link(Path.Combine(Root, "linked"), outside);

        await UserDataExport.ExportAsync(Root, Destination);

        Assert.DoesNotContain(Entries(), entry => entry.Contains("private.txt", StringComparison.Ordinal));
        Directory.Delete(Path.Combine(Root, "linked"));
    }

    [Fact]
    public async Task FileInUseIsSkippedAndListed()
    {
        if (!OperatingSystem.IsWindows()) return; // Only Windows enforces an exclusive open.
        Write("history.json", "[]");
        Write("recordings/locked.wav", "audio");

        UserDataExportResult result;
        using (new FileStream(Path.Combine(Root, "recordings", "locked.wav"), FileMode.Open, FileAccess.Read, FileShare.None))
            result = await UserDataExport.ExportAsync(Root, Destination);

        Assert.Equal(["recordings/locked.wav"], result.Skipped);
        Assert.DoesNotContain("profile/recordings/locked.wav", Entries());
        using var archive = ZipFile.OpenRead(Destination);
        using var readMe = new StreamReader(archive.GetEntry(UserDataExport.ReadMeEntryName)!.Open());
        Assert.Contains("recordings/locked.wav", await readMe.ReadToEndAsync());
    }

    [Fact]
    public async Task FolderThatCannotBeListedIsReportedAsMissing()
    {
        Write("history.json", "[]");
        Write("recordings/meeting.wav", "audio");
        var recordings = Path.Combine(Root, "recordings");
        DenyListing(recordings, deny: true);
        UserDataExportResult result;
        try
        {
            try { Directory.EnumerateFileSystemEntries(recordings).ToArray(); return; } // Elevated or root runs can still list it.
            catch (UnauthorizedAccessException) { }
            result = await UserDataExport.ExportAsync(Root, Destination);
        }
        finally { DenyListing(recordings, deny: false); }

        Assert.Equal(["recordings/"], result.Skipped);
        using var archive = ZipFile.OpenRead(Destination);
        using var readMe = new StreamReader(archive.GetEntry(UserDataExport.ReadMeEntryName)!.Open());
        Assert.Contains("recordings/", await readMe.ReadToEndAsync());
    }

    private static void DenyListing(string directory, bool deny)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(directory);
            var acl = info.GetAccessControl();
            var rule = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
            if (deny) acl.AddAccessRule(rule); else acl.RemoveAccessRule(rule);
            info.SetAccessControl(acl);
        }
        else File.SetUnixFileMode(directory, deny ? UnixFileMode.None : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task CanceledExportLeavesNothingAtTheChosenPath()
    {
        Write("history.json", "[]");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UserDataExport.ExportAsync(Root, Destination, cancellationToken: canceled.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(Destination)!));
    }

    [Theory]
    [InlineData("history.json", false, false)]
    [InlineData("history-audio/a.wav", false, false)]
    [InlineData("PluginData/com.example", true, false)]
    [InlineData("PluginData/com.example/scripts.json", false, false)]
    [InlineData("PluginData/com.example/Models", true, true)]
    [InlineData("PluginData/com.example/Models/model.onnx", false, true)]
    [InlineData("PluginData/com.example/key.secret", false, true)]
    [InlineData("PluginPackages", true, true)]
    [InlineData("licenses.dat", false, true)]
    [InlineData("premium-account-device.txt", false, true)]
    [InlineData("api-discovery.json", false, true)]
    [InlineData(".data-deletion-pending", false, true)]
    [InlineData(".backup-preview-0123", true, true)]
    [InlineData("recordings/.partial.lock", false, true)]
    public void ExclusionRules(string relative, bool isDirectory, bool excluded)
    {
        Assert.Equal(excluded, UserDataExport.IsExcluded(relative, isDirectory));
    }
}
