using System.IO;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class WatchFolderExportBuilderTests
{
    [Fact]
    public void Build_CreatesMarkdownArtifact()
    {
        var artifact = WatchFolderExportBuilder.Build(
            WatchFolderOutputFormat.Markdown,
            Result("hello"),
            "audio.wav",
            "Mock",
            new DateTime(2026, 4, 24, 12, 0, 0));

        Assert.Equal("md", artifact.FileExtension);
        Assert.Contains("# Transcription: audio.wav", artifact.Content);
        Assert.Contains("- Engine: Mock", artifact.Content);
        Assert.Contains("hello", artifact.Content);
    }

    [Fact]
    public void Build_CreatesPlainTextArtifact()
    {
        var artifact = WatchFolderExportBuilder.Build(
            WatchFolderOutputFormat.PlainText,
            Result("plain"),
            "audio.wav",
            "Mock",
            DateTime.Now);

        Assert.Equal("txt", artifact.FileExtension);
        Assert.Equal("plain", artifact.Content);
    }

    [Fact]
    public void Build_CreatesSubtitleArtifacts()
    {
        var result = Result("caption", [new TranscriptionSegment("caption", 0, 1.25)]);

        var srt = WatchFolderExportBuilder.Build(WatchFolderOutputFormat.Srt, result, "audio.wav", "Mock", DateTime.Now);
        var vtt = WatchFolderExportBuilder.Build(WatchFolderOutputFormat.Vtt, result, "audio.wav", "Mock", DateTime.Now);

        Assert.Equal("srt", srt.FileExtension);
        Assert.Contains("00:00:00,000 --> 00:00:01,250", srt.Content);
        Assert.Equal("vtt", vtt.FileExtension);
        Assert.StartsWith("WEBVTT", vtt.Content);
    }

    [Fact]
    public void Build_SubtitleWithoutSegments_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WatchFolderExportBuilder.Build(WatchFolderOutputFormat.Srt, Result("caption"), "audio.wav", "Mock", DateTime.Now));

        Assert.Contains("segments", ex.Message);
    }

    private static WatchFolderTranscriptionResult Result(
        string text,
        IReadOnlyList<TranscriptionSegment>? segments = null) =>
        new(text, "en", 1, 0.1, segments ?? [], "mock", "tiny");
}

public sealed class WatchFolderServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dataDir;
    private readonly string _watchDir;

    public WatchFolderServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tw_watch_test_{Guid.NewGuid():N}");
        _dataDir = Path.Combine(_tempDir, "data");
        _watchDir = Path.Combine(_tempDir, "watch");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_watchDir);
    }

    [Fact]
    public async Task Start_InitialScan_WritesMarkdownAndSuccessHistory()
    {
        var source = WriteAudioFile("meeting.wav");
        using var service = new WatchFolderService(_dataDir);

        service.Start(Options(), FakeTranscriber("transcribed"));

        await WaitForAsync(() => File.Exists(Path.Combine(_watchDir, "meeting.md")) && service.History.Count == 1);
        Assert.Contains("transcribed", await File.ReadAllTextAsync(Path.Combine(_watchDir, "meeting.md")));
        Assert.Single(service.History);
        Assert.True(service.History[0].Success);
        Assert.Equal(Path.GetFileName(source), service.History[0].FileName);
    }

    [Fact]
    public async Task Start_NewFileAfterWatching_WritesOutput()
    {
        using var service = new WatchFolderService(_dataDir);
        service.Start(Options(WatchFolderOutputFormat.PlainText), FakeTranscriber("new file"));

        WriteAudioFile("later.wav");

        await WaitForAsync(() => File.Exists(Path.Combine(_watchDir, "later.txt")) && service.History.Count == 1);
        Assert.Equal("new file", await File.ReadAllTextAsync(Path.Combine(_watchDir, "later.txt")));
        Assert.True(service.History[0].Success);
    }

    [Fact]
    public async Task Start_PersistedFingerprint_SkipsDuplicateFile()
    {
        WriteAudioFile("meeting.wav");
        var calls = 0;
        using (var first = new WatchFolderService(_dataDir))
        {
            first.Start(Options(WatchFolderOutputFormat.PlainText), async (request, ct) =>
            {
                calls++;
                await Task.Yield();
                return Result("first");
            });
            await WaitForAsync(() => File.Exists(Path.Combine(_watchDir, "meeting.txt")) && first.History.Count == 1);
        }

        using var second = new WatchFolderService(_dataDir);
        second.Start(Options(WatchFolderOutputFormat.PlainText), async (request, ct) =>
        {
            calls++;
            await Task.Yield();
            return Result("second");
        });

        await Task.Delay(900);
        Assert.Equal(1, calls);
        Assert.Single(second.History);
    }

    [Fact]
    public async Task Start_OutputFolderAndDeleteSource_WritesOutputAndDeletesOnlyAfterSuccess()
    {
        var source = WriteAudioFile("clip.wav");
        var outputDir = Path.Combine(_tempDir, "out");
        using var service = new WatchFolderService(_dataDir);

        service.Start(
            Options(WatchFolderOutputFormat.PlainText, outputDir, deleteSource: true),
            FakeTranscriber("done"));

        var output = Path.Combine(outputDir, "clip.txt");
        await WaitForAsync(() => File.Exists(output) && service.History.Count == 1);

        Assert.Equal("done", await File.ReadAllTextAsync(output));
        Assert.False(File.Exists(source));
        Assert.Equal(output, service.History[0].OutputPath);
    }

    [Fact]
    public async Task Start_DeleteSourceAfterChangedEvent_DoesNotRecordDeletedFileFailure()
    {
        var source = WriteAudioFile("delete-me.wav");
        using var service = new WatchFolderService(_dataDir);

        service.Start(
            Options(WatchFolderOutputFormat.PlainText, deleteSource: true),
            async (request, ct) =>
            {
                await File.AppendAllBytesAsync(request.FilePath, [5, 6], ct);
                await Task.Delay(200, ct);
                return Result("done");
            });

        var output = Path.Combine(_watchDir, "delete-me.txt");
        await WaitForAsync(() => File.Exists(output) && !File.Exists(source) && service.History.Count == 1);
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Single(service.History);
        Assert.True(service.History[0].Success);
    }

    [Fact]
    public async Task Start_ErrorHistoryDoesNotFingerprintFile_AndAllowsRetry()
    {
        WriteAudioFile("retry.wav");
        using var service = new WatchFolderService(_dataDir);
        var fail = true;

        service.Start(Options(WatchFolderOutputFormat.PlainText), async (request, ct) =>
        {
            await Task.Yield();
            if (fail)
                throw new InvalidOperationException("boom");

            return Result("recovered");
        });

        await WaitForAsync(() => service.History.Count == 1 && !service.History[0].Success);
        Assert.Contains("boom", service.History[0].ErrorMessage);

        fail = false;
        service.Stop();
        service.Start(Options(WatchFolderOutputFormat.PlainText), FakeTranscriber("recovered"));

        await WaitForAsync(() => File.Exists(Path.Combine(_watchDir, "retry.txt")) && service.History.Count == 2);
        Assert.True(service.History[0].Success);
        Assert.False(service.History[1].Success);
    }

    [Fact]
    public async Task Start_RepeatedRescan_DoesNotDuplicateSameFailure()
    {
        WriteAudioFile("broken.wav");
        using var service = new WatchFolderService(_dataDir);

        service.Start(Options(WatchFolderOutputFormat.PlainText), async (request, ct) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        });

        await WaitForAsync(() => service.History.Count == 1 && !service.History[0].Success);
        await Task.Delay(TimeSpan.FromSeconds(6));

        Assert.Single(service.History);
        Assert.Contains("boom", service.History[0].ErrorMessage);
    }

    [Fact]
    public async Task Constructor_LoadsPersistedHistory()
    {
        WriteAudioFile("history.wav");
        using (var service = new WatchFolderService(_dataDir))
        {
            service.Start(Options(), FakeTranscriber("saved"));
            await WaitForAsync(() => service.History.Count == 1);
        }

        using var reloaded = new WatchFolderService(_dataDir);

        Assert.Single(reloaded.History);
        Assert.Equal("history.wav", reloaded.History[0].FileName);
    }

    private WatchFolderOptions Options(
        WatchFolderOutputFormat format = WatchFolderOutputFormat.Markdown,
        string? outputPath = null,
        bool deleteSource = false) =>
        new(_watchDir, outputPath, format, deleteSource);

    private string WriteAudioFile(string fileName)
    {
        var path = Path.Combine(_watchDir, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private static Func<WatchFolderTranscriptionRequest, CancellationToken, Task<WatchFolderTranscriptionResult>> FakeTranscriber(string text) =>
        (request, ct) => Task.FromResult(Result(text));

    private static WatchFolderTranscriptionResult Result(string text) =>
        new(
            text,
            "en",
            1,
            0.1,
            [new TranscriptionSegment(text, 0, 1)],
            "mock",
            "tiny");

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (condition())
                return;

            await Task.Delay(100);
        }

        Assert.True(condition());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
