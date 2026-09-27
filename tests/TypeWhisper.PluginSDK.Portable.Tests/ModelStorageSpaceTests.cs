using TypeWhisper.PluginSDK.Helpers;
using Xunit;

public sealed class ModelStorageSpaceTests : IDisposable
{
    private const long MiB = 1024L * 1024;
    private readonly string _root = Path.Join(Path.GetTempPath(), "storage-" + Guid.NewGuid());
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void RefusesDownloadWithRequiredAndAvailableSpace()
    {
        string? probed = null;
        var error = Assert.Throws<InsufficientModelStorageException>(() => ModelStorageSpace.EnsureAvailable(
            Path.Join(_root, "Models"), 670 * MiB, "Parakeet TDT 0.6B", path => { probed = path; return 400 * MiB; }));

        Assert.Equal(Path.Join(_root, "Models"), probed);
        Assert.Equal(670 * MiB + ModelStorageSpace.ReserveBytes, error.RequiredBytes);
        Assert.Equal(400 * MiB, error.AvailableBytes);
        Assert.Equal(unchecked((int)0x80070070), error.HResult);
        Assert.Equal("Not enough free disk space to download Parakeet TDT 0.6B. It needs about 926 MB on "
            + Path.GetPathRoot(Path.GetFullPath(_root)) + ", but only 400 MB is available. Free up at least 526 MB and try again.",
            error.Message);
    }

    [Theory]
    [InlineData(1024L, "1.0 GB")]
    [InlineData(1536L, "1.5 GB")]
    [InlineData(926L, "926 MB")]
    public void FormatsSizesLikeFileExplorer(long mebibytes, string expected) =>
        Assert.Equal(expected, ModelStorageSpace.FormatBytes(mebibytes * MiB));

    [Fact]
    public void RoundsMissingSpaceUpSoTheAdviceIsSufficient()
    {
        var error = Assert.Throws<InsufficientModelStorageException>(() => ModelStorageSpace.EnsureAvailable(
            _root, 2 * 1024 * MiB, "Whisper Medium", _ => 1024 * MiB + 1));
        Assert.Contains("needs about 2.3 GB", error.Message);
        Assert.Contains("only 1.0 GB is available", error.Message);
        Assert.Contains("Free up at least 1.3 GB", error.Message);
    }

    [Theory]
    [InlineData(926L, true)]
    [InlineData(925L, false)]
    public void AcceptsExactlyTheRequiredSpaceIncludingReserve(long availableMebibytes, bool accepted)
    {
        void Check() => ModelStorageSpace.EnsureAvailable(_root, 670 * MiB, "Model", _ => availableMebibytes * MiB);
        if (accepted) Check();
        else Assert.Throws<InsufficientModelStorageException>(Check);
    }

    [Fact]
    public void SkipsCheckWhenNothingIsMissingOrSpaceIsUnknown()
    {
        ModelStorageSpace.EnsureAvailable(_root, 0, "Model", _ => throw new InvalidOperationException("must not probe"));
        ModelStorageSpace.EnsureAvailable(_root, long.MaxValue, "Model", _ => null);
    }

    [Fact]
    public void ReadsFreeSpaceThroughTheNearestExistingDirectory()
    {
        Directory.CreateDirectory(_root);
        var available = ModelStorageSpace.GetAvailableBytes(Path.Join(_root, "missing", "Models"));
        Assert.NotNull(available);
        Assert.True(available > 0);
    }

    [Fact]
    public void ClassifiesDiskFullFailuresOnly()
    {
        var diskFull = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        var insufficient = new InsufficientModelStorageException("Model", @"C:\", 2, 1);

        Assert.True(ModelStorageSpace.IsDiskFull(new InvalidOperationException("wrapped", diskFull)));
        Assert.True(ModelStorageSpace.IsDiskFull(new AggregateException(new HttpRequestException(), insufficient)));
        Assert.True(ModelStorageSpace.IsDiskFull(new IOException("handle", unchecked((int)0x80070027))));
        Assert.False(ModelStorageSpace.IsDiskFull(new IOException("The model file download was incomplete.")));
        Assert.False(ModelStorageSpace.IsDiskFull(null));

        Assert.Equal(insufficient.Message, ModelStorageSpace.DescribeFailure(new IOException("retry", insufficient)));
        Assert.StartsWith("The disk ran out of space while downloading the model.", ModelStorageSpace.DescribeFailure(diskFull));
        Assert.Null(ModelStorageSpace.DescribeFailure(new HttpRequestException("offline")));
    }

    [Fact]
    public void RemovesOnlyAbandonedPartialFiles()
    {
        Directory.CreateDirectory(_root);
        var abandoned = Path.Join(_root, "encoder.onnx.tmp");
        var active = Path.Join(_root, "decoder.onnx.tmp");
        File.WriteAllText(abandoned, "partial");
        File.WriteAllText(active, "partial");

        using (new FileStream(active, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            Assert.True(ModelStorageSpace.TryRemoveAbandonedFile(abandoned));
            Assert.False(ModelStorageSpace.TryRemoveAbandonedFile(active));
        }

        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(active));
        Assert.False(ModelStorageSpace.TryRemoveAbandonedFile(abandoned));
    }
}
