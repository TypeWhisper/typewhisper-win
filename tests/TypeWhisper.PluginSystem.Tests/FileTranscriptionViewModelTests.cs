using TypeWhisper.Core.Models;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class FileTranscriptionViewModelTests
{
    [Fact]
    public async Task AddFiles_ProcessesSupportedFilesSeriallyInOrder()
    {
        var processor = new FakeProcessor();
        var sut = CreateSut(processor);

        sut.AddFilesCommand.Execute(new[] { "first.wav", "second.mp3" });

        await WaitForAsync(() => sut.Items.Count == 2 && sut.Items.All(i => i.Status == FileTranscriptionQueueItemStatus.Completed));

        Assert.Equal(["first.wav", "second.mp3"], processor.StartedPaths);
        Assert.Equal(["first.wav", "second.mp3"], sut.Items.Select(i => i.FilePath).ToArray());
        Assert.All(sut.Items, item => Assert.True(item.HasResult));
    }

    [Fact]
    public async Task AddFiles_DeDuplicatesWithinBatchAndMarksUnsupportedFiles()
    {
        var processor = new FakeProcessor();
        var sut = CreateSut(processor);

        sut.AddFilesCommand.Execute(new[] { "clip.wav", "clip.wav", "notes.txt" });

        await WaitForAsync(() => sut.Items.Count == 2 && sut.Items[0].Status == FileTranscriptionQueueItemStatus.Completed);

        Assert.Equal(2, sut.Items.Count);
        Assert.Equal(FileTranscriptionQueueItemStatus.Completed, sut.Items[0].Status);
        Assert.Equal(FileTranscriptionQueueItemStatus.Unsupported, sut.Items[1].Status);
        Assert.Equal(["clip.wav"], processor.StartedPaths);
    }

    [Fact]
    public async Task FailedFile_DoesNotStopLaterQueuedFiles()
    {
        var processor = new FakeProcessor();
        processor.Failures.Add("bad.wav", new InvalidOperationException("boom"));
        var sut = CreateSut(processor);

        sut.AddFilesCommand.Execute(new[] { "bad.wav", "good.wav" });

        await WaitForAsync(() => sut.Items.Count == 2
            && sut.Items[0].Status == FileTranscriptionQueueItemStatus.Error
            && sut.Items[1].Status == FileTranscriptionQueueItemStatus.Completed);

        Assert.Equal("boom", sut.Items[0].ErrorText);
        Assert.Equal("processed good.wav", sut.Items[1].ResultText);
    }

    [Fact]
    public async Task CancelItem_CancelsQueuedItemWithoutStoppingActiveItem()
    {
        var processor = new BlockingProcessor();
        var sut = CreateSut(processor);

        sut.AddFilesCommand.Execute(new[] { "active.wav", "queued.wav" });
        await WaitForAsync(() => sut.Items.Count == 2 && sut.Items[0].Status == FileTranscriptionQueueItemStatus.Transcribing);

        sut.CancelItemCommand.Execute(sut.Items[1]);
        processor.Release("active.wav");

        await WaitForAsync(() => sut.Items[0].Status == FileTranscriptionQueueItemStatus.Completed
            && sut.Items[1].Status == FileTranscriptionQueueItemStatus.Cancelled);

        Assert.Equal(["active.wav"], processor.StartedPaths);
    }

    [Fact]
    public async Task CancelItem_CancelsActiveItemAndContinuesQueue()
    {
        var processor = new BlockingProcessor();
        var sut = CreateSut(processor);

        sut.AddFilesCommand.Execute(new[] { "active.wav", "next.wav" });
        await WaitForAsync(() => sut.Items.Count == 2 && sut.Items[0].Status == FileTranscriptionQueueItemStatus.Transcribing);

        sut.CancelItemCommand.Execute(sut.Items[0]);
        await WaitForAsync(() => sut.Items[0].Status == FileTranscriptionQueueItemStatus.Cancelled
            && sut.Items[1].Status == FileTranscriptionQueueItemStatus.Transcribing);

        processor.Release("next.wav");
        await WaitForAsync(() => sut.Items[1].Status == FileTranscriptionQueueItemStatus.Completed);
    }

    [Fact]
    public async Task CompletedItem_EnablesSubtitleExportWhenSegmentsExist()
    {
        var processor = new FakeProcessor
        {
            ResultFactory = path => new FileTranscriptionProcessResult(
                new TranscriptionResult
                {
                    Text = path,
                    Duration = 2,
                    ProcessingTime = 1,
                    DetectedLanguage = "en",
                    Segments = [new TranscriptionSegment("hello", 0, 1)]
                },
                "hello")
        };
        var sut = CreateSut(processor);

        sut.AddFilesCommand.Execute(new[] { "clip.wav" });

        await WaitForAsync(() => sut.Items.Count == 1 && sut.Items[0].Status == FileTranscriptionQueueItemStatus.Completed);

        Assert.True(sut.Items[0].HasResult);
        Assert.True(sut.Items[0].CanExportSubtitles);
    }

    [Fact]
    public async Task ClearQueue_RemovesInactiveItemsAndKeepsActiveQueue()
    {
        var processor = new BlockingProcessor();
        var sut = CreateSut(processor);

        sut.AddFilesCommand.Execute(new[] { "active.wav", "queued.wav" });
        await WaitForAsync(() => sut.Items.Count == 2
            && sut.Items[0].Status == FileTranscriptionQueueItemStatus.Transcribing);

        var completed = new FileTranscriptionQueueItemViewModel("done.wav", FileTranscriptionQueueItemStatus.Completed);
        var cancelled = new FileTranscriptionQueueItemViewModel("cancelled.wav", FileTranscriptionQueueItemStatus.Cancelled);
        var error = new FileTranscriptionQueueItemViewModel("bad.wav", FileTranscriptionQueueItemStatus.Error);
        var unsupported = new FileTranscriptionQueueItemViewModel("notes.txt", FileTranscriptionQueueItemStatus.Unsupported);
        sut.Items.Add(completed);
        sut.Items.Add(cancelled);
        sut.Items.Add(error);
        sut.Items.Add(unsupported);
        sut.SelectedItem = completed;

        sut.ClearQueueCommand.Execute(null);

        Assert.Equal(["active.wav", "queued.wav"], sut.Items.Select(i => i.FilePath).ToArray());
        Assert.Equal(
            [FileTranscriptionQueueItemStatus.Transcribing, FileTranscriptionQueueItemStatus.Queued],
            sut.Items.Select(i => i.Status).ToArray());
        Assert.Same(sut.Items[0], sut.SelectedItem);
        Assert.True(sut.HasItems);
        Assert.Equal(
            Loc.Instance.GetString("FileTranscription.QueueStatusFormat", 0, 0, 0, 1, 2),
            sut.StatusText);

        processor.Release("active.wav");
        await WaitForAsync(() => sut.Items[1].Status == FileTranscriptionQueueItemStatus.Transcribing);
        processor.Release("queued.wav");
        await WaitForAsync(() => sut.Items.All(i => i.Status == FileTranscriptionQueueItemStatus.Completed));
    }

    [Fact]
    public void ClearQueue_RemovesAllInactiveItemsAndResetsEmptyState()
    {
        var sut = CreateSut(new FakeProcessor());
        var completed = new FileTranscriptionQueueItemViewModel("done.wav", FileTranscriptionQueueItemStatus.Completed);
        var cancelled = new FileTranscriptionQueueItemViewModel("cancelled.wav", FileTranscriptionQueueItemStatus.Cancelled);
        var error = new FileTranscriptionQueueItemViewModel("bad.wav", FileTranscriptionQueueItemStatus.Error);
        var unsupported = new FileTranscriptionQueueItemViewModel("notes.txt", FileTranscriptionQueueItemStatus.Unsupported);
        sut.Items.Add(completed);
        sut.Items.Add(cancelled);
        sut.Items.Add(error);
        sut.Items.Add(unsupported);
        sut.SelectedItem = error;

        sut.ClearQueueCommand.Execute(null);

        Assert.Empty(sut.Items);
        Assert.Null(sut.SelectedItem);
        Assert.False(sut.HasItems);
        Assert.False(sut.ClearQueueCommand.CanExecute(null));
        Assert.Equal(Loc.Instance["FileTranscription.StatusDefault"], sut.StatusText);
    }

    [Fact]
    public void WatchFolderLanguageOptions_IncludeChineseSpokenLanguage()
    {
        var sut = CreateSut(new FakeProcessor());

        var option = Assert.Single(sut.WatchFolderLanguageOptions, option => option.Id == "zh");
        Assert.Equal("中文", option.DisplayName);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    private static FileTranscriptionViewModel CreateSut(IFileTranscriptionProcessor processor) =>
        new(processor, new FakeSettingsService(), new WatchFolderService());

    private sealed class FakeProcessor : IFileTranscriptionProcessor
    {
        public List<string> StartedPaths { get; } = [];
        public Dictionary<string, Exception> Failures { get; } = [];
        public Func<string, FileTranscriptionProcessResult>? ResultFactory { get; set; }

        public Task<FileTranscriptionProcessResult> ProcessAsync(
            string filePath,
            Action<FileTranscriptionProcessProgress> onProgress,
            FileTranscriptionProcessOptions? options,
            CancellationToken cancellationToken)
        {
            StartedPaths.Add(filePath);
            cancellationToken.ThrowIfCancellationRequested();
            onProgress(new FileTranscriptionProcessProgress(
                FileTranscriptionQueueItemStatus.Transcribing,
                "transcribing"));

            if (Failures.TryGetValue(filePath, out var failure))
                throw failure;

            return Task.FromResult(ResultFactory?.Invoke(filePath) ?? new FileTranscriptionProcessResult(
                new TranscriptionResult
                {
                    Text = filePath,
                    Duration = 1,
                    ProcessingTime = 0.5,
                    DetectedLanguage = "en"
                },
                $"processed {filePath}"));
        }
    }

    private sealed class BlockingProcessor : IFileTranscriptionProcessor
    {
        private readonly Dictionary<string, TaskCompletionSource> _gates = [];
        public List<string> StartedPaths { get; } = [];

        public async Task<FileTranscriptionProcessResult> ProcessAsync(
            string filePath,
            Action<FileTranscriptionProcessProgress> onProgress,
            FileTranscriptionProcessOptions? options,
            CancellationToken cancellationToken)
        {
            StartedPaths.Add(filePath);
            onProgress(new FileTranscriptionProcessProgress(
                FileTranscriptionQueueItemStatus.Transcribing,
                "transcribing"));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates[filePath] = gate;
            using var _ = cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
            await gate.Task;
            return new FileTranscriptionProcessResult(
                new TranscriptionResult
                {
                    Text = filePath,
                    Duration = 1,
                    ProcessingTime = 0.5,
                    DetectedLanguage = "en"
                },
                $"processed {filePath}");
        }

        public void Release(string path)
        {
            _gates[path].TrySetResult();
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; private set; } = AppSettings.Default;
        public event Action<AppSettings>? SettingsChanged;

        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }
}
