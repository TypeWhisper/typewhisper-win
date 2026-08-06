using System.Buffers.Binary;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class DictationRecoveryAudioStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"tw_recovery_test_{Guid.NewGuid():N}");
    private DateTimeOffset _now = new(2026, 8, 5, 10, 11, 12, 345, TimeSpan.Zero);

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Recording_WritesValidPcm16WavHeaderSamplesAndDuration()
    {
        await using var store = CreateStore();

        var recordingId = Assert.IsType<Guid>(store.BeginRecording());
        store.AppendSamples(recordingId, [-1f, -0.5f, 0f, 0.5f, 1f]);
        var lease = Assert.IsType<RecoveryRecordingLease>(
            await store.FinalizeRecordingAsync(recordingId));
        var descriptor = Assert.IsType<RecoveryRecordingDescriptor>(await lease.PreserveAsync());

        var bytes = await File.ReadAllBytesAsync(Assert.IsType<string>(store.GetRecordingPath(descriptor.Id)));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24, 4)));
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22, 2)));
        Assert.Equal((short)16, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34, 2)));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40, 4)));
        Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44, 2)));
        Assert.Equal((short)-16384, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(46, 2)));
        Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(48, 2)));
        Assert.Equal((short)16384, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(50, 2)));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(52, 2)));
        Assert.Equal(5d / 16_000d, descriptor.DurationSeconds, 8);
        Assert.Equal(54, descriptor.FileSizeBytes);
    }

    [Fact]
    public async Task MultipleStoppedDictations_HaveIndependentPendingLeases()
    {
        await using var store = CreateStore();

        var firstId = Assert.IsType<Guid>(store.BeginRecording());
        store.AppendSamples(firstId, [0.1f, 0.2f]);
        var firstLease = Assert.IsType<RecoveryRecordingLease>(await store.FinalizeRecordingAsync(firstId));

        _now = _now.AddMilliseconds(1);
        var secondId = Assert.IsType<Guid>(store.BeginRecording());
        store.AppendSamples(secondId, [0.3f, 0.4f]);
        var secondLease = Assert.IsType<RecoveryRecordingLease>(await store.FinalizeRecordingAsync(secondId));

        await firstLease.DiscardAsync();
        var kept = Assert.IsType<RecoveryRecordingDescriptor>(await secondLease.PreserveAsync());

        Assert.Single(store.Recordings);
        Assert.Equal(kept.Id, store.Recordings[0].Id);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.pending.wav"));
    }

    [Fact]
    public async Task Startup_PromotesValidPendingAndDeletesIncompleteActiveFiles()
    {
        string pendingPath;
        string activePath;
        await using (var firstStore = CreateStore())
        {
            var pendingId = Assert.IsType<Guid>(firstStore.BeginRecording());
            firstStore.AppendSamples(pendingId, [0.2f, 0.3f]);
            _ = Assert.IsType<RecoveryRecordingLease>(await firstStore.FinalizeRecordingAsync(pendingId));
            pendingPath = Assert.Single(Directory.EnumerateFiles(_directory, "*.pending.wav"));

            _now = _now.AddMilliseconds(1);
            var activeId = Assert.IsType<Guid>(firstStore.BeginRecording());
            firstStore.AppendSamples(activeId, [0.4f]);
            await firstStore.RefreshAsync();
            activePath = Assert.Single(Directory.EnumerateFiles(_directory, "*.active.wav"));
        }

        Assert.True(File.Exists(pendingPath));
        Assert.True(File.Exists(activePath));

        await using var restartedStore = CreateStore();
        await restartedStore.InitializeAsync();

        Assert.Single(restartedStore.Recordings);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.pending.wav"));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.active.wav"));
        Assert.Single(Directory.EnumerateFiles(_directory, "*.wav"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(180)]
    public async Task Retention_KeepsBoundaryAndDeletesOlderRecordings(int days)
    {
        await using var store = CreateStore();
        var descriptor = await CreateRecoveryAsync(store);

        _now = descriptor.CreatedAt.AddDays(days);
        await store.SetRetentionAsync(days);
        Assert.Single(store.Recordings);

        _now = _now.AddTicks(1);
        await store.RefreshAsync();
        Assert.Empty(store.Recordings);
    }

    [Fact]
    public async Task RetentionImmediately_DeletesExistingAndDisablesNewWrites()
    {
        await using var store = CreateStore();
        _ = await CreateRecoveryAsync(store);

        await store.SetRetentionAsync(-1);

        Assert.Empty(store.Recordings);
        Assert.Null(store.BeginRecording());
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task RetentionNever_KeepsOldRecordings()
    {
        await using var store = CreateStore();
        _ = await CreateRecoveryAsync(store);
        _now = _now.AddYears(50);

        await store.SetRetentionAsync(0);
        await store.RefreshAsync();

        Assert.Single(store.Recordings);
    }

    [Fact]
    public async Task Delete_RejectsTraversalAndIgnoresForeignFiles()
    {
        var foreignPath = Path.Combine(_directory, "dictation-recovery-not-ours.wav");
        var externalPath = Path.Combine(
            Path.GetDirectoryName(_directory)!,
            $"dictation-recovery-external-{Guid.NewGuid():N}.wav");
        await File.WriteAllTextAsync(foreignPath, "do not touch");
        await File.WriteAllTextAsync(externalPath, "do not touch");
        try
        {
            await using var store = CreateStore();

            Assert.False(await store.DeleteAsync($"../{Path.GetFileName(externalPath)}"));
            await store.DeleteAllAsync();

            Assert.True(File.Exists(externalPath));
            Assert.True(File.Exists(foreignPath));
            Assert.Empty(store.Recordings);
        }
        finally
        {
            try { File.Delete(externalPath); } catch { }
        }
    }

    [Fact]
    public async Task Startup_IgnoresMatchingSymbolicLink()
    {
        var externalPath = Path.Combine(Path.GetTempPath(), $"tw_recovery_external_{Guid.NewGuid():N}.wav");
        await WriteValidWavAsync(externalPath);
        var linkPath = Path.Combine(_directory, "dictation-recovery-20260805-101112-345-0001.wav");

        try
        {
            try
            {
                File.CreateSymbolicLink(linkPath, externalPath);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }

            await using var store = CreateStore();
            await store.InitializeAsync();
            Assert.Empty(store.Recordings);
            await store.DeleteAllAsync();
            Assert.True(File.Exists(externalPath));
        }
        finally
        {
            try { File.Delete(linkPath); } catch { }
            try { File.Delete(externalPath); } catch { }
        }
    }

    [Fact]
    public async Task TransientDirectoryFailure_DisablesOnlyCurrentRecording()
    {
        var blockingParent = Path.Combine(_directory, "blocked-parent");
        File.WriteAllText(blockingParent, "temporarily unavailable");
        var recoveryDirectory = Path.Combine(blockingParent, "recovery");
        await using var store = new DictationRecoveryAudioStore(recoveryDirectory, () => _now);

        var failedId = Assert.IsType<Guid>(store.BeginRecording());
        store.AppendSamples(failedId, [0.1f]);
        Assert.Null(await store.FinalizeRecordingAsync(failedId));

        File.Delete(blockingParent);
        Directory.CreateDirectory(blockingParent);
        var recoveredId = Assert.IsType<Guid>(store.BeginRecording());
        store.AppendSamples(recoveredId, [0.2f, 0.3f]);
        var lease = Assert.IsType<RecoveryRecordingLease>(await store.FinalizeRecordingAsync(recoveredId));

        Assert.NotNull(await lease.PreserveAsync());
        Assert.Single(store.Recordings);
    }

    private DictationRecoveryAudioStore CreateStore() => new(_directory, () => _now);

    private static async Task<RecoveryRecordingDescriptor> CreateRecoveryAsync(
        DictationRecoveryAudioStore store)
    {
        var recordingId = Assert.IsType<Guid>(store.BeginRecording());
        store.AppendSamples(recordingId, [0.1f, 0.2f, 0.3f]);
        var lease = Assert.IsType<RecoveryRecordingLease>(await store.FinalizeRecordingAsync(recordingId));
        return Assert.IsType<RecoveryRecordingDescriptor>(await lease.PreserveAsync());
    }

    private static async Task WriteValidWavAsync(string path)
    {
        var bytes = new byte[46];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), 38);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), 16_000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), 32_000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40, 4), 2);
        await File.WriteAllBytesAsync(path, bytes);
    }
}
