using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.WinUI;
using Xunit;

public sealed class LocalModelManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "models-" + Guid.NewGuid());
    private readonly Mock<IPcmTranscriptionEnginePlugin> _engine = new();
    private readonly HashSet<string> _downloaded = [LocalTranscriptionPlugin.ModelId];
    private readonly List<string> _loads = [];
    private readonly Lifetime _lifetime = new();
    private sealed class Lifetime : IAsyncDisposable
    {
        public bool Disposed;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    public LocalModelManagementTests()
    {
        _engine.SetupGet(e => e.TranscriptionModels).Returns([new(LocalTranscriptionPlugin.ModelId, "Parakeet"), new("canary", "Canary")]);
        _engine.SetupGet(e => e.SupportsModelDownload).Returns(true);
        _engine.Setup(e => e.IsModelDownloaded(It.IsAny<string>())).Returns((string id) => _downloaded.Contains(id));
        _engine.Setup(e => e.LoadModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string id, CancellationToken _) => _loads.Add(id)).Returns(Task.CompletedTask);
        _engine.Setup(e => e.UnloadModelAsync()).Returns(Task.CompletedTask);
    }
    private LocalTranscriptionPlugin Create(IPluginHostServices? host = null) => new(host ?? new VocabularyHostServices(_root),
        () => Task.FromResult(new LocalTranscriptionLease(_engine.Object, _lifetime)));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task MissingModelKeepsPluginAvailableForDownload()
    {
        _downloaded.Clear();
        await using var runtime = Create(); await runtime.InitializeAsync();
        Assert.True(runtime.Enabled); Assert.False(runtime.Ready); Assert.Equal(2, runtime.Models.Count);
        Assert.Empty(_loads);
    }
    [Fact]
    public async Task SuccessfulSwitchPersistsAndRestoresSelection()
    {
        _downloaded.Add("canary");
        await using (var runtime = Create()) { await runtime.InitializeAsync(); await runtime.ActivateAsync("canary"); Assert.Equal("canary", runtime.ActiveModelId); }
        _loads.Clear();
        await using var restarted = Create(); await restarted.InitializeAsync();
        Assert.Equal(["canary"], _loads); Assert.Equal("canary", restarted.ActiveModelId);
    }
    [Fact]
    public async Task FailedSwitchRestoresPreviousModelAndSavedSelection()
    {
        _downloaded.Add("canary");
        _engine.Setup(e => e.LoadModelAsync("canary", It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("invalid model"));
        await using var runtime = Create(); await runtime.InitializeAsync();
        await Assert.ThrowsAsync<IOException>(() => runtime.ActivateAsync("canary"));
        Assert.True(runtime.Ready); Assert.Equal(LocalTranscriptionPlugin.ModelId, runtime.ActiveModelId);
        Assert.Equal(LocalTranscriptionPlugin.ModelId, new VocabularyHostServices(_root).GetSetting<string>("SelectedModelId"));
        Assert.Equal(2, _loads.Count);
    }
    [Fact]
    public async Task PersistenceFailureRollsBackLoadedModel()
    {
        _downloaded.Add("canary");
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.SetSetting("SelectedModelId", "canary")).Throws(new IOException("disk full"));
        await using var runtime = Create(host.Object); await runtime.SetEnabledAsync(true);
        await Assert.ThrowsAsync<IOException>(() => runtime.ActivateAsync("canary"));
        Assert.Equal(LocalTranscriptionPlugin.ModelId, runtime.ActiveModelId);
        Assert.Equal([LocalTranscriptionPlugin.ModelId, "canary", LocalTranscriptionPlugin.ModelId], _loads);
    }
    [Fact]
    public async Task DownloadReportsProgressAndDoesNotSelectTheModel()
    {
        _engine.Setup(e => e.DownloadModelAsync("canary", It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IProgress<double> progress, CancellationToken ct) => { progress.Report(0.4); progress.Report(2); _downloaded.Add("canary"); return Task.CompletedTask; });
        await using var runtime = Create(); await runtime.InitializeAsync(); await runtime.DownloadAsync("canary");
        Assert.True(runtime.Models.Single(m => m.Model.Id == "canary").Downloaded);
        Assert.Equal(LocalTranscriptionPlugin.ModelId, runtime.ActiveModelId); Assert.Equal(1, runtime.Progress); Assert.False(runtime.Busy);
    }
    [Fact]
    public async Task CancelDrainsDownloadAndRejectsCompetingOperations()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Setup(e => e.DownloadModelAsync("canary", It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, IProgress<double> progress, CancellationToken ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
        await using var runtime = Create(); await runtime.InitializeAsync();
        var downloading = runtime.DownloadAsync("canary"); await started.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ActivateAsync(LocalTranscriptionPlugin.ModelId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SetEnabledAsync(false));
        Assert.False(_lifetime.Disposed); runtime.CancelDownload(); await downloading;
        Assert.False(runtime.Busy); Assert.Null(runtime.DownloadingModelId); Assert.Null(runtime.Error);
        Assert.True(runtime.Ready); Assert.Contains("canceled", runtime.Feedback);
    }
    [Fact]
    public async Task FailedDownloadCanBeRetried()
    {
        _engine.SetupSequence(e => e.DownloadModelAsync("canary", It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline")).Returns(Task.CompletedTask);
        await using var runtime = Create(); await runtime.InitializeAsync(); await runtime.DownloadAsync("canary");
        Assert.Contains("offline", runtime.Error); Assert.True(runtime.Ready);
        _downloaded.Add("canary"); await runtime.DownloadAsync("canary"); Assert.Null(runtime.Error);
    }
    [Fact]
    public async Task SelectedLanguageIsPersistedAndPassedToTranscription()
    {
        _engine.SetupGet(e => e.SupportedLanguages).Returns(["en", "de", "fr", "es"]);
        _engine.Setup(e => e.TranscribePcmAsync(It.IsAny<ReadOnlyMemory<float>>(), "de", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PluginTranscriptionResult("Hallo", "de", 1, null));
        await using var runtime = Create(); await runtime.InitializeAsync(); runtime.SelectLanguage("de");
        var result = await runtime.DecodeAsync([0f], false);
        Assert.Equal("Hallo", result.Text);
        Assert.Equal("de", new VocabularyHostServices(_root).GetSetting<string>("Language"));
        Assert.Throws<ArgumentException>(() => runtime.SelectLanguage("xx"));
    }
    [Fact]
    public async Task InitialLoadFailureLeavesCatalogAvailableAndRetryWorks()
    {
        _engine.SetupSequence(e => e.LoadModelAsync(LocalTranscriptionPlugin.ModelId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("load failed")).Returns(Task.CompletedTask);
        await using var runtime = Create(); await runtime.InitializeAsync();
        Assert.True(runtime.Enabled); Assert.False(runtime.Ready); Assert.NotNull(runtime.Error);
        await runtime.ActivateAsync(LocalTranscriptionPlugin.ModelId);
        Assert.True(runtime.Ready); Assert.Null(runtime.Error);
    }
}
