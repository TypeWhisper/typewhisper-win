using System.Text.Json;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WatchedFolderProcessorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "typewhisper-watch-" + Guid.NewGuid());
    private string Input => Path.Combine(_root, "input");
    private string Output => Path.Combine(_root, "output");
    private string State => Path.Combine(_root, "watch.json");
    public WatchedFolderProcessorTests() { Directory.CreateDirectory(Input); Directory.CreateDirectory(Output); }
    private WatchedFolderProcessor Create(string format = "txt")
    {
        var watcher = new WatchedFolderProcessor(State);
        Assert.True(watcher.Configure(new(Input, Output, format)));
        watcher.Start(); return watcher;
    }
    private string Source(string name = "meeting.wav") { var path = Path.Combine(Input, name); File.WriteAllBytes(path, [1, 2, 3]); return path; }
    private static Task<FileTranscriptionOutput> Decode(string path, Action<string> stage, CancellationToken ct) =>
        Task.FromResult(new FileTranscriptionOutput("Grüße, 世界!", "test", "model", 1, []));

    [Fact]
    public async Task RequiresExplicitStartAndStableRevisionThenPublishesOnlyOnceAcrossRestart()
    {
        var source = Source(); var watcher = Create(); watcher.Stop();
        var calls = 0;
        Task<FileTranscriptionOutput> Process(string p, Action<string> s, CancellationToken c) { calls++; return Decode(p,s,c); }
        await watcher.PollAsync(true, Process); Assert.Empty(watcher.Files);
        watcher.Start(); await watcher.PollAsync(true, Process); Assert.Equal(0, calls);
        await watcher.PollAsync(true, Process); Assert.Equal(1, calls);
        var result = Assert.Single(watcher.Files); Assert.Equal("Completed", result.Status);
        Assert.Equal("Grüße, 世界!", File.ReadAllText(result.ExportPath!)); Assert.True(File.Exists(source));
        var restored = new WatchedFolderProcessor(State); Assert.False(restored.Watching); restored.Start();
        await restored.PollAsync(true, Process); await restored.PollAsync(true, Process); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ChangedAndLockedFilesWaitUntilStableAndReadable()
    {
        var source = Source(); var watcher = Create();
        await watcher.PollAsync(true, Decode);
        File.AppendAllText(source, "still writing");
        await watcher.PollAsync(true, Decode); Assert.Empty(watcher.Files);
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.None))
        { await watcher.PollAsync(true, Decode); Assert.Empty(watcher.Files); }
        await watcher.PollAsync(true, Decode); Assert.Equal("Completed", Assert.Single(watcher.Files).Status);
    }

    [Fact]
    public async Task IdleProviderGateAndConcurrentPollDoNotDuplicateRequests()
    {
        Source(); var watcher = Create();
        await watcher.PollAsync(false, Decode); Assert.Empty(watcher.Files);
        await watcher.PollAsync(true, Decode);
        var release = new TaskCompletionSource<FileTranscriptionOutput>();
        var run = watcher.PollAsync(true, (_, _, _) => release.Task);
        // Discovery runs asynchronously; wait until it reaches the provider.
        for (var i = 0; i < 100 && watcher.Files.Count == 0; i++) await Task.Delay(10);
        Assert.True(watcher.Busy);
        await watcher.PollAsync(true, (_, _, _) => throw new Exception("Duplicate request"));
        Assert.False(watcher.Configure(new(Input, Output)));
        release.SetResult(await Decode("", _ => {}, default)); await run;
        Assert.Single(watcher.Files);
    }

    [Fact]
    public async Task ExportFailureRetainsTextAndRetriesWithoutCallingProviderAgain()
    {
        Source(); var watcher = Create("srt"); var calls = 0;
        Task<FileTranscriptionOutput> Process(string p, Action<string> s, CancellationToken c) { calls++; return Decode(p,s,c); }
        await watcher.PollAsync(true, Process); await watcher.PollAsync(true, Process);
        Assert.Equal("Failed", Assert.Single(watcher.Files).Status); Assert.NotNull(watcher.Files[0].Result);
        await watcher.PollAsync(true, Process); Assert.Equal(1, calls);
        watcher.Stop(); Assert.True(watcher.Configure(new(Input, Output, "txt")));
        Assert.True(watcher.RetryFailures()); await watcher.PollAsync(false, Process);
        Assert.Equal(1, calls); Assert.Equal("Completed", watcher.Files[0].Status);
    }

    [Fact]
    public async Task FailureDoesNotBlockOtherFilesAndRetryIsExplicit()
    {
        Source("a.wav"); Source("b.mp3"); Source("ignored.txt"); var watcher = Create();
        Task<FileTranscriptionOutput> Process(string p, Action<string> s, CancellationToken c) =>
            p.EndsWith("a.wav") ? throw new InvalidOperationException("Provider unavailable") : Decode(p,s,c);
        await watcher.PollAsync(true, Process); await watcher.PollAsync(true, Process); await watcher.PollAsync(true, Process);
        Assert.Equal(new[] {"Failed", "Completed"}, watcher.Files.Select(f=>f.Status));
        Assert.True(watcher.RetryFailures()); await watcher.PollAsync(true, Decode);
        Assert.All(watcher.Files, f => Assert.Equal("Completed", f.Status));
    }

    [Fact]
    public async Task ShutdownDrainsProviderAndDoesNotAcceptLateResult()
    {
        Source(); var watcher = Create(); await watcher.PollAsync(true, Decode);
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource<FileTranscriptionOutput>();
        var run = watcher.PollAsync(true, (_,_,_) => { entered.SetResult(); return release.Task; }); await entered.Task;
        var shutdown = watcher.ShutdownAsync(); Assert.False(shutdown.IsCompleted);
        release.SetResult(await Decode("", _=>{}, default)); await Task.WhenAll(run, shutdown);
        Assert.Equal("Failed", Assert.Single(watcher.Files).Status); Assert.Empty(Directory.GetFiles(Output));
        watcher.Start(); Assert.False(watcher.Watching);
    }

    [Fact]
    public async Task SourceRevisionChangesCreateDistinctExportsWithoutOverwriting()
    {
        var source = Source(); var watcher = Create();
        await watcher.PollAsync(true, Decode); await watcher.PollAsync(true, Decode);
        File.AppendAllText(source, "new revision");
        await watcher.PollAsync(true, Decode); await watcher.PollAsync(true, Decode);
        Assert.Equal(2, watcher.Files.Count); Assert.Equal(2, Directory.GetFiles(Output).Length);
    }

    [Fact]
    public void CorruptStateBlocksWritesAndPreservesOriginal()
    {
        File.WriteAllText(State, "{broken"); var watcher = new WatchedFolderProcessor(State);
        Assert.NotNull(watcher.Error); Assert.False(watcher.Configure(new(Input, Output)));
        watcher.Start(); Assert.False(watcher.Watching); Assert.Equal("{broken", File.ReadAllText(State));
    }

    [Fact]
    public async Task InterruptedCheckpointRequiresExplicitRetry()
    {
        var source = Source(); var info = new FileInfo(source);
        File.WriteAllText(State, JsonSerializer.Serialize(new { Version=1, Settings=new WatchedFolderSettings(Input,Output),
            Files=new[] {new WatchedFile(Guid.NewGuid(), source, info.Length, info.LastWriteTimeUtc.Ticks,"Processing")} }));
        var watcher = new WatchedFolderProcessor(State); watcher.Start();
        await watcher.PollAsync(true, (_,_,_)=>throw new Exception("No automatic replay"));
        await watcher.PollAsync(true, (_,_,_)=>throw new Exception("No automatic replay"));
        Assert.Equal("Failed", Assert.Single(watcher.Files).Status);
        Assert.True(watcher.RetryFailures()); await watcher.PollAsync(true, Decode);
        Assert.Equal("Completed", watcher.Files[0].Status);
    }

    [Fact]
    public async Task DefaultOutputNeedsOnlyOneFolderAndDoesNotScanSubfolders()
    {
        Source(); Directory.CreateDirectory(Path.Combine(Input, "nested"));
        File.WriteAllBytes(Path.Combine(Input, "nested", "ignored.wav"), [1]);
        var watcher = new WatchedFolderProcessor(State);
        Assert.True(watcher.Configure(new(Input, "", StartWithApp: true)));
        Assert.Equal(Path.Combine(Input, "Transcripts"), watcher.Settings!.Output);
        Assert.False(watcher.Watching); watcher.Start();
        await watcher.PollAsync(true, Decode); await watcher.PollAsync(true, Decode);
        Assert.Single(watcher.Files); Assert.EndsWith("meeting.txt", watcher.Files[0].ExportPath);
        Assert.True(new WatchedFolderProcessor(State).Settings!.StartWithApp);
    }

    [Fact]
    public async Task FailedCheckpointPreventsProviderCallAndCanRecoverWithoutRestart()
    {
        Source(); var watcher = Create(); await watcher.PollAsync(true, Decode);
        File.Move(State, State + ".backup"); Directory.CreateDirectory(State);
        await watcher.PollAsync(true, (_,_,_) => throw new Exception("Must save before sending audio"));
        Assert.NotNull(watcher.Error); Assert.False(watcher.Watching);
        Directory.Delete(State); File.Move(State + ".backup", State);
        Assert.True(watcher.RetrySavingProgress()); Assert.Null(watcher.Error);
        watcher.Start(); await watcher.PollAsync(true, Decode);
        Assert.Equal("Completed", Assert.Single(watcher.Files).Status);
    }

    [Fact]
    public async Task ExportRetryCanUseNewOutputFolderWithoutRepeatingTranscription()
    {
        Source(); var watcher = Create("srt");
        await watcher.PollAsync(true, Decode); await watcher.PollAsync(true, Decode);
        watcher.Stop(); var other = Path.Combine(_root, "other");
        Assert.True(watcher.Configure(new(Input, other)));
        watcher.RetryFailures(); await watcher.PollAsync(false, (_,_,_) => throw new Exception("Result already exists"));
        Assert.Equal("Completed", watcher.Files[0].Status);
        Assert.Equal(other, Path.GetDirectoryName(watcher.Files[0].ExportPath));
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Settings\":null,\"Files\":[null]}")]
    [InlineData("{\"Version\":999,\"Settings\":null,\"Files\":[]}")]
    [InlineData("{\"Version\":1,\"Settings\":{\"Input\":null,\"Output\":null},\"Files\":[]}")]
    public void InvalidSchemaNeverOverwritesSavedData(string json)
    {
        File.WriteAllText(State, json); var watcher = new WatchedFolderProcessor(State);
        Assert.NotNull(watcher.Error); Assert.False(watcher.RetrySavingProgress());
        Assert.Equal(json, File.ReadAllText(State));
    }

    [Fact]
    public async Task ChangingInputFolderKeepsPreviousResultsIsolatedAndRestoresThemWhenReturning()
    {
        Source(); var watcher = Create("srt");
        await watcher.PollAsync(true, Decode); await watcher.PollAsync(true, Decode);
        Assert.Equal("Failed", Assert.Single(watcher.Files).Status);
        watcher.Stop(); var second = Path.Combine(_root, "second"); Directory.CreateDirectory(second);
        Assert.True(watcher.Configure(new(second, Output)));
        Assert.Empty(watcher.Files); watcher.RetryFailures();
        await watcher.PollAsync(true, (_,_,_) => throw new Exception("Do not process a different folder"));
        Assert.Empty(Directory.GetFiles(Output));
        watcher.Stop(); Assert.True(watcher.Configure(new(Input, Output)));
        Assert.Equal("Failed", Assert.Single(watcher.Files).Status);
        watcher.RetryFailures(); await watcher.PollAsync(false, (_,_,_) => throw new Exception("Reuse the saved text"));
        Assert.Equal("Completed", Assert.Single(watcher.Files).Status);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
