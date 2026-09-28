using TypeWhisper.Core.Services.UserData;

namespace TypeWhisper.Core.Tests.Services;

public sealed class ProfileDataEraserTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "TypeWhisper-eraser-tests-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Join(_directory, "profile");
    private string Outside => Path.Join(_directory, "outside");

    public ProfileDataEraserTests()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* A leftover temp folder must not fail the test run. */ }
    }

    private void Seed()
    {
        File.WriteAllText(Path.Join(Root, "history.json"), "[]");
        Directory.CreateDirectory(Path.Join(Root, "history-audio"));
        File.WriteAllText(Path.Join(Root, "history-audio", "one.wav"), "audio");
        Directory.CreateDirectory(Path.Join(Root, "PluginData", "plugin", "Models"));
        File.WriteAllText(Path.Join(Root, "PluginData", "plugin", "Models", "model.bin"), "model");
        var readOnly = Path.Join(Root, "PluginData", "plugin", "key.secret");
        File.WriteAllText(readOnly, "secret");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly | FileAttributes.Hidden);
    }

    [Fact]
    public void EraseEmptiesEveryEntryButKeepsTheFolder()
    {
        Seed();

        var report = ProfileDataEraser.Erase(Root);

        Assert.True(report.Complete);
        Assert.Equal(8, report.Removed);
        Assert.True(Directory.Exists(Root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
    }

    [Fact]
    public void EraseKeepsNamedTopLevelEntriesOnly()
    {
        Seed();
        File.WriteAllText(Path.Join(Root, "keep.txt"), "kept");
        File.WriteAllText(Path.Join(Root, "history-audio", "keep.txt"), "nested");

        var report = ProfileDataEraser.Erase(Root, "keep.txt");

        Assert.True(report.Complete);
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(Root).Select(Path.GetFileName));
    }

    [Fact]
    public void LinkedFolderIsRemovedWithoutTouchingItsTarget()
    {
        File.WriteAllText(Path.Join(Outside, "precious.txt"), "keep me");
        LegacyDailyProfileMigrationTests.Link(Path.Join(Root, "linked"), Outside);

        var report = ProfileDataEraser.Erase(Root);

        Assert.True(report.Complete);
        Assert.False(Directory.Exists(Path.Join(Root, "linked")));
        Assert.Equal("keep me", File.ReadAllText(Path.Join(Outside, "precious.txt")));
    }

    [Fact]
    public void LinkedRootIsRefusedAndNothingIsDeleted()
    {
        File.WriteAllText(Path.Join(Outside, "precious.txt"), "keep me");
        var linkedRoot = Path.Join(_directory, "linked-root");
        LegacyDailyProfileMigrationTests.Link(linkedRoot, Outside);

        var report = ProfileDataEraser.Erase(linkedRoot);

        Assert.True(report.Refused);
        Assert.False(report.Complete);
        Assert.False(ProfileDataEraser.CanErase(linkedRoot));
        Assert.Throws<IOException>(() => ProfileDataEraser.RequestErasure(linkedRoot));
        Assert.True(File.Exists(Path.Join(Outside, "precious.txt")));
        Directory.Delete(linkedRoot);
    }

    [Fact]
    public void FileRootAndDriveRootAreRefused()
    {
        var file = Path.Join(_directory, "file-root");
        File.WriteAllText(file, "not a folder");

        Assert.True(ProfileDataEraser.Erase(file).Refused);
        Assert.True(File.Exists(file));
        Assert.False(ProfileDataEraser.CanErase(Path.GetPathRoot(_directory)!));
    }

    [Fact]
    public void MissingRootIsAlreadyEmpty()
    {
        var report = ProfileDataEraser.Erase(Path.Join(_directory, "missing"));

        Assert.True(report.Complete);
        Assert.Equal(0, report.Removed);
    }

    [Fact]
    public void FileInUseIsCountedAndThePendingMarkerStays()
    {
        if (!OperatingSystem.IsWindows()) return; // Only Windows refuses to delete an open file.
        Seed();
        ProfileDataEraser.RequestErasure(Root);
        var held = Path.Join(Root, "history-audio", "one.wav");

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var report = ProfileDataEraser.CompletePendingErasure(Root);
            Assert.NotNull(report);
            Assert.False(report.Complete);
            Assert.Equal(2, report.Remaining); // The folder and the file inside it.
            Assert.True(ProfileDataEraser.IsErasurePending(Root));
            Assert.False(File.Exists(Path.Join(Root, "history.json")));
        }

        var finished = ProfileDataEraser.CompletePendingErasure(Root);
        Assert.NotNull(finished);
        Assert.True(finished.Complete);
        Assert.False(ProfileDataEraser.IsErasurePending(Root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
    }

    [Fact]
    public void PendingErasureAlsoEmptiesAdditionalFolders()
    {
        Seed();
        var legacy = Path.Join(_directory, "legacy");
        Directory.CreateDirectory(Path.Join(legacy, "Data"));
        File.WriteAllText(Path.Join(legacy, "Data", "history.json"), "[]");
        ProfileDataEraser.RequestErasure(Root);

        var report = ProfileDataEraser.CompletePendingErasure(Root, new ErasureTarget(legacy));

        Assert.NotNull(report);
        Assert.True(report.Complete);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(legacy));
    }

    [Fact]
    public void AdditionalFolderHoldingTheDataFolderIsRejected()
    {
        Seed();
        ProfileDataEraser.RequestErasure(Root);

        Assert.Throws<ArgumentException>(() => ProfileDataEraser.CompletePendingErasure(Root, new ErasureTarget(_directory)));
        Assert.Throws<ArgumentException>(() => ProfileDataEraser.CompletePendingErasure(Root, new ErasureTarget(Root)));
        Assert.Throws<ArgumentException>(() => ProfileDataEraser.CompletePendingErasure(Root, new ErasureTarget(_directory, ["profile"])));
        Assert.Throws<ArgumentException>(() => ProfileDataEraser.CompletePendingErasure(Root, ErasureTarget.Entry(Root)));
        Assert.True(File.Exists(Path.Join(Root, "history.json")));
        Assert.True(ProfileDataEraser.IsErasurePending(Root));
    }

    [Fact]
    public void NamedEntriesAreDeletedAndEverythingElseInTheFolderStays()
    {
        var install = Path.Join(_directory, "install");
        Directory.CreateDirectory(Path.Join(install, "Data"));
        File.WriteAllText(Path.Join(install, "Data", "history.json"), "[]");
        File.WriteAllText(Path.Join(install, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Join(install, "current"));
        File.WriteAllText(Path.Join(install, "current", "TypeWhisper.exe"), "app");
        File.WriteAllText(Path.Join(install, "Update.exe"), "updater");

        var report = ProfileDataEraser.EraseEntries(install, ["Data", "settings.json", "Models"]);

        Assert.True(report.Complete);
        Assert.Equal(3, report.Removed);
        Assert.Equal(["Update.exe", "current"], Directory.EnumerateFileSystemEntries(install).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Join(install, "current", "TypeWhisper.exe")));
    }

    [Fact]
    public void ExternalFileInUseKeepsTheErasurePending()
    {
        if (!OperatingSystem.IsWindows()) return; // Only Windows refuses to delete an open file.
        Seed();
        var log = Path.Join(Outside, "errors.log");
        File.WriteAllText(log, "exception details");
        ProfileDataEraser.RequestErasure(Root);

        using (new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var report = ProfileDataEraser.CompletePendingErasure(Root, ErasureTarget.Entry(log));
            Assert.NotNull(report);
            Assert.Equal(1, report.Remaining);
            Assert.True(ProfileDataEraser.IsErasurePending(Root));
        }

        var finished = ProfileDataEraser.CompletePendingErasure(Root, ErasureTarget.Entry(log));
        Assert.NotNull(finished);
        Assert.True(finished.Complete);
        Assert.False(File.Exists(log));
        Assert.False(ProfileDataEraser.IsErasurePending(Root));
    }

    [Fact]
    public void RequestErasureReplacesALinkedMarkerWithoutWritingThroughIt()
    {
        Directory.CreateDirectory(Root);
        var precious = Path.Join(Outside, "precious.txt");
        File.WriteAllText(precious, "keep me");
        var marker = Path.Join(Root, ProfileDataEraser.PendingMarkerName);
        LegacyDailyProfileMigrationTests.Link(marker, Outside);

        ProfileDataEraser.RequestErasure(Root);

        Assert.Equal("keep me", File.ReadAllText(precious));
        Assert.False(File.GetAttributes(marker).HasFlag(FileAttributes.ReparsePoint));
        Assert.True(ProfileDataEraser.IsErasurePending(Root));

        File.Delete(marker);
        try { File.CreateSymbolicLink(marker, precious); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // File links need a privilege on Windows.
        ProfileDataEraser.RequestErasure(Root);
        Assert.Equal("keep me", File.ReadAllText(precious));
        Assert.False(File.GetAttributes(marker).HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void EntriesLeftElsewhereKeepTheErasurePending()
    {
        Seed();
        ProfileDataEraser.RequestErasure(Root);
        var left = 2;

        var report = ProfileDataEraser.CompletePendingErasure(Root, () => left);
        Assert.NotNull(report);
        Assert.Equal(2, report.Remaining);
        Assert.True(ProfileDataEraser.IsErasurePending(Root));

        left = 0;
        Assert.True(ProfileDataEraser.CompletePendingErasure(Root, () => left)!.Complete);
        Assert.False(ProfileDataEraser.IsErasurePending(Root));
        Assert.Null(ProfileDataEraser.CompletePendingErasure(Root, () => throw new InvalidOperationException("Not called when nothing is pending.")));
    }

    [Fact]
    public void CancelReportsSuccessOnlyWhenTheMarkerIsGone()
    {
        Directory.CreateDirectory(Root);
        var marker = Path.Join(Root, ProfileDataEraser.PendingMarkerName);
        Directory.CreateDirectory(marker);
        Assert.True(ProfileDataEraser.CancelPendingErasure(Root));
        Assert.False(Directory.Exists(marker));

        Directory.CreateDirectory(marker);
        File.WriteAllText(Path.Join(marker, "blocking.txt"), "x");
        Assert.False(ProfileDataEraser.CancelPendingErasure(Root));
        Assert.True(ProfileDataEraser.IsErasurePending(Root));

        Assert.True(ProfileDataEraser.CancelPendingErasure(Path.Join(_directory, "missing")));
    }

    [Fact]
    public void NoPendingErasureLeavesTheFolderAlone()
    {
        Seed();

        Assert.Null(ProfileDataEraser.CompletePendingErasure(Root));
        Assert.True(File.Exists(Path.Join(Root, "history.json")));
    }

    [Fact]
    public void CanceledErasureDoesNotRunAtTheNextLaunch()
    {
        Seed();
        ProfileDataEraser.RequestErasure(Root);

        ProfileDataEraser.CancelPendingErasure(Root);

        Assert.False(ProfileDataEraser.IsErasurePending(Root));
        Assert.Null(ProfileDataEraser.CompletePendingErasure(Root));
        Assert.True(File.Exists(Path.Join(Root, "history.json")));
    }

    [Theory]
    [InlineData("profile", "profile/a", true)]
    [InlineData("profile", "profile", false)]
    [InlineData("profile", "profile-other/a", false)]
    [InlineData("profile", "profile/../outside", false)]
    public void StrictlyInsideRejectsTheRootSiblingsAndClimbs(string root, string candidate, bool expected)
    {
        Assert.Equal(expected, ProfileDataEraser.IsStrictlyInside(Path.Join(_directory, root), Path.Join(_directory, candidate)));
    }
}
