using System.Buffers.Binary;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class RecorderLibraryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "recorder-library-" + Guid.NewGuid());

    [Fact]
    public async Task FreshStoreFindsPublishedWavWithActualMetadataAndNoTemporaryFiles()
    {
        var path = await RecorderWavStore.SaveAsync(_directory, new float[3200], "Grüße");
        await File.WriteAllTextAsync(path + ".tmp", "unfinished");
        await File.WriteAllTextAsync(Path.Combine(_directory, "notes.txt"), "not audio");
        var store = new RecorderLibraryStore(_directory);
        var entry = Assert.Single(await store.ReadAsync());
        Assert.Equal(path, entry.FilePath);
        Assert.Equal(Path.GetFileName(path), entry.Name);
        Assert.Equal(6444, entry.SizeBytes);
        Assert.Equal(TimeSpan.FromMilliseconds(200), entry.Duration);
        Assert.Equal(new DateTimeOffset(File.GetCreationTimeUtc(path)), entry.CreatedAt);
        Assert.Null(entry.Error);
        Assert.Single(await new RecorderLibraryStore(_directory).ReadAsync());
    }

    [Fact]
    public async Task CorruptAndTruncatedWavsRemainVisibleBesideValidAudio()
    {
        var path = await RecorderWavStore.SaveAsync(_directory, new float[100]);
        await File.WriteAllTextAsync(Path.Combine(_directory, "broken.wav"), "not a WAV");
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "truncated.WAV"), bytes[..^2]);
        var entries = await new RecorderLibraryStore(_directory).ReadAsync();
        Assert.Equal(3, entries.Count);
        Assert.Single(entries.Where(entry => entry.Error is null));
        Assert.All(entries.Where(entry => entry.Error is not null), entry => Assert.Null(entry.Duration));
        Assert.True(File.Exists(Path.Combine(_directory, "broken.wav")));
    }

    [Fact]
    public async Task WavChunkMetadataIsReadWithoutAssumingFixedAudioOffset()
    {
        var path = await RecorderWavStore.SaveAsync(_directory, new float[160]);
        var original = await File.ReadAllBytesAsync(path);
        var bytes = new byte[original.Length + 12];
        original.AsSpan(0, 36).CopyTo(bytes);
        "JUNK"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 4);
        original.AsSpan(36).CopyTo(bytes.AsSpan(48));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        await File.WriteAllBytesAsync(path, bytes);
        var entry = Assert.Single(await new RecorderLibraryStore(_directory).ReadAsync());
        Assert.Equal(TimeSpan.FromMilliseconds(10), entry.Duration);
        Assert.Null(entry.Error);
    }

    [Fact]
    public async Task DeleteRemovesOnlySelectedWavAndRejectsOutsideOrNonAudioPaths()
    {
        var path = await RecorderWavStore.SaveAsync(_directory, [0.1f]);
        var retained = await RecorderWavStore.SaveAsync(_directory, [0.2f]);
        var notes = Path.Combine(_directory, "notes.txt");
        await File.WriteAllTextAsync(notes, "keep");
        var store = new RecorderLibraryStore(_directory);
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(notes));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(Path.Combine(_directory, "..", "outside.wav")));
        await store.DeleteAsync(path);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(retained));
        Assert.Equal("keep", await File.ReadAllTextAsync(notes));
        Assert.Single(await store.ReadAsync());
    }

    [Fact]
    public async Task ExternallyRemovedSourceReportsFailureWithoutDeletingAnotherRecording()
    {
        var path = await RecorderWavStore.SaveAsync(_directory, [0.1f]);
        var removed = await RecorderWavStore.SaveAsync(_directory, [0.2f]);
        var store = new RecorderLibraryStore(_directory);
        File.Delete(removed);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.DeleteAsync(removed));
        Assert.Equal(path, Assert.Single(await store.ReadAsync()).FilePath);
    }

    [Fact]
    public async Task MissingDirectoryIsEmptyAndCanceledRefreshDoesNotReturnEntries()
    {
        var store = new RecorderLibraryStore(_directory);
        Assert.Empty(await store.ReadAsync());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(cancellation.Token));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task FinalSourceGuardAndDeleteCompleteOnCallingThreadWithoutAQueuedWorker()
    {
        var path = await RecorderWavStore.SaveAsync(_directory, [0.1f]);
        var store = new RecorderLibraryStore(_directory);
        var thread = Environment.CurrentManagedThreadId;
        var queued = true;
        bool IsQueued(string candidate)
        {
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            Assert.Equal(path, candidate);
            Assert.True(File.Exists(path));
            return queued;
        }
        Assert.Throws<InvalidOperationException>(() => store.Delete(path, IsQueued));
        Assert.True(File.Exists(path));
        queued = false;
        store.Delete(path, IsQueued);
        Assert.False(File.Exists(path));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
