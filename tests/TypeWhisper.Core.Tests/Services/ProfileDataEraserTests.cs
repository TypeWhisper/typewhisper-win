using TypeWhisper.Core.Services.UserData;

namespace TypeWhisper.Core.Tests.Services;

public sealed class ProfileDataEraserTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TypeWhisper-eraser-tests-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_directory, "profile");
    private string Outside => Path.Combine(_directory, "outside");

    public ProfileDataEraserTests()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private void Seed()
    {
        File.WriteAllText(Path.Combine(Root, "history.json"), "[]");
        Directory.CreateDirectory(Path.Combine(Root, "history-audio"));
        File.WriteAllText(Path.Combine(Root, "history-audio", "one.wav"), "audio");
        Directory.CreateDirectory(Path.Combine(Root, "PluginData", "plugin", "Models"));
        File.WriteAllText(Path.Combine(Root, "PluginData", "plugin", "Models", "model.bin"), "model");
        var readOnly = Path.Combine(Root, "PluginData", "plugin", "key.secret");
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
        File.WriteAllText(Path.Combine(Root, "keep.txt"), "kept");
        File.WriteAllText(Path.Combine(Root, "history-audio", "keep.txt"), "nested");

        var report = ProfileDataEraser.Erase(Root, "keep.txt");

        Assert.True(report.Complete);
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(Root).Select(Path.GetFileName));
    }

    [Fact]
    public void LinkedFolderIsRemovedWithoutTouchingItsTarget()
    {
        File.WriteAllText(Path.Combine(Outside, "precious.txt"), "keep me");
        LegacyDailyProfileMigrationTests.Link(Path.Combine(Root, "linked"), Outside);

        var report = ProfileDataEraser.Erase(Root);

        Assert.True(report.Complete);
        Assert.False(Directory.Exists(Path.Combine(Root, "linked")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(Outside, "precious.txt")));
    }

    [Fact]
    public void LinkedRootIsRefusedAndNothingIsDeleted()
    {
        File.WriteAllText(Path.Combine(Outside, "precious.txt"), "keep me");
        var linkedRoot = Path.Combine(_directory, "linked-root");
        LegacyDailyProfileMigrationTests.Link(linkedRoot, Outside);

        var report = ProfileDataEraser.Erase(linkedRoot);

        Assert.True(report.Refused);
        Assert.False(report.Complete);
        Assert.False(ProfileDataEraser.CanErase(linkedRoot));
        Assert.Throws<IOException>(() => ProfileDataEraser.RequestErasure(linkedRoot));
        Assert.True(File.Exists(Path.Combine(Outside, "precious.txt")));
        Directory.Delete(linkedRoot);
    }

    [Fact]
    public void FileRootAndDriveRootAreRefused()
    {
        var file = Path.Combine(_directory, "file-root");
        File.WriteAllText(file, "not a folder");

        Assert.True(ProfileDataEraser.Erase(file).Refused);
        Assert.True(File.Exists(file));
        Assert.False(ProfileDataEraser.CanErase(Path.GetPathRoot(_directory)!));
    }

    [Fact]
    public void MissingRootIsAlreadyEmpty()
    {
        var report = ProfileDataEraser.Erase(Path.Combine(_directory, "missing"));

        Assert.True(report.Complete);
        Assert.Equal(0, report.Removed);
    }

    [Fact]
    public void FileInUseIsCountedAndThePendingMarkerStays()
    {
        if (!OperatingSystem.IsWindows()) return; // Only Windows refuses to delete an open file.
        Seed();
        ProfileDataEraser.RequestErasure(Root);
        var held = Path.Combine(Root, "history-audio", "one.wav");

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var report = ProfileDataEraser.CompletePendingErasure(Root);
            Assert.NotNull(report);
            Assert.False(report.Complete);
            Assert.Equal(2, report.Remaining); // The folder and the file inside it.
            Assert.True(ProfileDataEraser.IsErasurePending(Root));
            Assert.False(File.Exists(Path.Combine(Root, "history.json")));
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
        var legacy = Path.Combine(_directory, "legacy");
        Directory.CreateDirectory(Path.Combine(legacy, "Data"));
        File.WriteAllText(Path.Combine(legacy, "Data", "history.json"), "[]");
        ProfileDataEraser.RequestErasure(Root);

        var report = ProfileDataEraser.CompletePendingErasure(Root, legacy);

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

        Assert.Throws<ArgumentException>(() => ProfileDataEraser.CompletePendingErasure(Root, _directory));
        Assert.Throws<ArgumentException>(() => ProfileDataEraser.CompletePendingErasure(Root, Root));
        Assert.True(File.Exists(Path.Combine(Root, "history.json")));
        Assert.True(ProfileDataEraser.IsErasurePending(Root));
    }

    [Fact]
    public void NoPendingErasureLeavesTheFolderAlone()
    {
        Seed();

        Assert.Null(ProfileDataEraser.CompletePendingErasure(Root));
        Assert.True(File.Exists(Path.Combine(Root, "history.json")));
    }

    [Fact]
    public void CanceledErasureDoesNotRunAtTheNextLaunch()
    {
        Seed();
        ProfileDataEraser.RequestErasure(Root);

        ProfileDataEraser.CancelPendingErasure(Root);

        Assert.False(ProfileDataEraser.IsErasurePending(Root));
        Assert.Null(ProfileDataEraser.CompletePendingErasure(Root));
        Assert.True(File.Exists(Path.Combine(Root, "history.json")));
    }

    [Theory]
    [InlineData("profile", "profile/a", true)]
    [InlineData("profile", "profile", false)]
    [InlineData("profile", "profile-other/a", false)]
    [InlineData("profile", "profile/../outside", false)]
    public void StrictlyInsideRejectsTheRootSiblingsAndClimbs(string root, string candidate, bool expected)
    {
        Assert.Equal(expected, ProfileDataEraser.IsStrictlyInside(Path.Combine(_directory, root), Path.Combine(_directory, candidate)));
    }
}
