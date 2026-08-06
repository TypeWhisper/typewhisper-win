using System.IO;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class ManualAudioRecoveryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"typewhisper-manual-recovery-{Guid.NewGuid():N}");

    public ManualAudioRecoveryServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RecoverAsync_SavesHistoryAudioAndMetadataBeforeDeletingRecovery()
    {
        await using var store = new DictationRecoveryAudioStore(Path.Combine(_root, "recovery-success"));
        var descriptor = await CreateRecoveryAsync(store);
        var audioPath = Path.Combine(_root, "history-audio");
        var history = new HistoryService(Path.Combine(_root, "history.json"), audioPath);
        var processor = new FakeProcessor(new FileTranscriptionProcessResult(
            new TranscriptionResult { Text = "raw", DetectedLanguage = "de", Duration = 1.25 },
            "processed",
            "actual-engine",
            "actual-model",
            TranscriptionTask.Translate));
        var service = new ManualAudioRecoveryService(store, processor, history, audioPath);

        var record = await service.RecoverAsync(
            descriptor.Id,
            new FileTranscriptionProcessOptions("chosen-engine", "chosen-model", "de", TranscriptionTask.Translate),
            _ => { },
            CancellationToken.None);

        Assert.Equal("raw", record.RawText);
        Assert.Equal("processed", record.FinalText);
        Assert.Equal("actual-engine", record.EngineUsed);
        Assert.Equal("actual-model", record.ModelUsed);
        Assert.Equal("Translate", record.TranscriptionTaskUsed);
        Assert.NotNull(record.AudioFileName);
        Assert.True(File.Exists(Path.Combine(audioPath, record.AudioFileName!)));
        Assert.Single(history.Records);
        Assert.Empty(store.Recordings);
    }

    [Fact]
    public async Task RecoverAsync_HistoryPersistenceFailureKeepsRecoveryAndRemovesCopy()
    {
        await using var store = new DictationRecoveryAudioStore(Path.Combine(_root, "recovery-persist-failure"));
        var descriptor = await CreateRecoveryAsync(store);
        var invalidParent = Path.Combine(_root, "not-directory");
        File.WriteAllText(invalidParent, "blocking file");
        var historyAudio = Path.Combine(_root, "failed-history-audio");
        var history = new HistoryService(Path.Combine(invalidParent, "history.json"), historyAudio);
        var service = new ManualAudioRecoveryService(
            store,
            new FakeProcessor(CreateResult()),
            history,
            historyAudio);

        await Assert.ThrowsAsync<IOException>(() => service.RecoverAsync(
            descriptor.Id,
            new FileTranscriptionProcessOptions(),
            _ => { },
            CancellationToken.None));

        Assert.Single(store.Recordings);
        Assert.False(Directory.Exists(historyAudio) && Directory.EnumerateFiles(historyAudio).Any());
    }

    [Fact]
    public async Task RecoverAsync_EmptyOrCancelledTranscriptionKeepsRecovery()
    {
        await using var store = new DictationRecoveryAudioStore(Path.Combine(_root, "recovery-errors"));
        var emptyDescriptor = await CreateRecoveryAsync(store);
        var history = new HistoryService(Path.Combine(_root, "error-history.json"));
        var emptyService = new ManualAudioRecoveryService(
            store,
            new FakeProcessor(new FileTranscriptionProcessResult(new TranscriptionResult { Text = "" }, "")),
            history,
            Path.Combine(_root, "error-audio"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => emptyService.RecoverAsync(
            emptyDescriptor.Id,
            new FileTranscriptionProcessOptions(),
            _ => { },
            CancellationToken.None));
        Assert.Single(store.Recordings);

        var cancelledService = new ManualAudioRecoveryService(
            store,
            new FakeProcessor(new OperationCanceledException()),
            history,
            Path.Combine(_root, "error-audio"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledService.RecoverAsync(
            emptyDescriptor.Id,
            new FileTranscriptionProcessOptions(),
            _ => { },
            new CancellationToken(true)));
        Assert.Single(store.Recordings);
        Assert.Empty(history.Records);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static FileTranscriptionProcessResult CreateResult() => new(
        new TranscriptionResult { Text = "raw", Duration = 0.1 },
        "processed",
        "engine",
        "model");

    private static async Task<RecoveryRecordingDescriptor> CreateRecoveryAsync(DictationRecoveryAudioStore store)
    {
        var id = Assert.IsType<Guid>(store.BeginRecording());
        store.AppendSamples(id, Enumerable.Repeat(0.2f, 1600).ToArray());
        var lease = Assert.IsType<RecoveryRecordingLease>(await store.FinalizeRecordingAsync(id));
        return Assert.IsType<RecoveryRecordingDescriptor>(await lease.PreserveAsync());
    }

    private sealed class FakeProcessor : IFileTranscriptionProcessor
    {
        private readonly FileTranscriptionProcessResult? _result;
        private readonly Exception? _exception;

        public FakeProcessor(FileTranscriptionProcessResult result) => _result = result;
        public FakeProcessor(Exception exception) => _exception = exception;

        public Task<FileTranscriptionProcessResult> ProcessAsync(
            string filePath,
            Action<FileTranscriptionProcessProgress> onProgress,
            FileTranscriptionProcessOptions? options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _exception is not null
                ? Task.FromException<FileTranscriptionProcessResult>(_exception)
                : Task.FromResult(_result!);
        }
    }
}
