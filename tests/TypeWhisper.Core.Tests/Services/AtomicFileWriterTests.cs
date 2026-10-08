using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class AtomicFileWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tw_atomic_writer_{Guid.NewGuid():N}");

    public AtomicFileWriterTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void TryWriteAllText_ReportsNoErrorAfterAWrite()
    {
        var target = Path.Combine(_directory, "catalog.json");

        Assert.True(AtomicFileWriter.TryWriteAllText(target, "[]", out var error));

        Assert.Null(error);
        Assert.Equal("[]", File.ReadAllText(target));
    }

    [Fact]
    public void TryWriteAllText_ReportsWhyTheTargetDirectoryCannotBeCreated()
    {
        // A file where the directory should be makes the directory unwritable on every platform.
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "");
        var target = Path.Combine(blocker, "catalog.json");

        Assert.False(AtomicFileWriter.TryWriteAllText(target, "[]", out var error));

        Assert.IsAssignableFrom<IOException>(error);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void TryWriteAllBytes_ReportsWhyALockedTargetCannotBeReplaced()
    {
        if (!OperatingSystem.IsWindows()) return; // Only Windows refuses to replace an open file.
        var target = Path.Combine(_directory, "catalog.json");
        File.WriteAllText(target, "old");

        bool written;
        Exception? error;
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            written = AtomicFileWriter.TryWriteAllBytes(target, "new"u8.ToArray(), out error);

        Assert.False(written);
        Assert.NotNull(error);
        Assert.Equal("old", File.ReadAllText(target));
        // The temporary file beside the target is removed after the failed replacement.
        Assert.Equal(["catalog.json"], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (DirectoryNotFoundException) { }
    }
}
