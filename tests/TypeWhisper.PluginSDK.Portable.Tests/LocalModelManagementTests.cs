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
    public async Task ExplicitUnloadKeepsPluginAndDownloadedModelsAvailableForReload()
    {
        await using var runtime = Create(); await runtime.InitializeAsync();
        Assert.Equal(LocalTranscriptionPlugin.ModelId, await runtime.UnloadAsync(default));
        Assert.True(runtime.Enabled);
        Assert.False(runtime.Ready);
        Assert.Null(runtime.ActiveModelId);
        Assert.False(_lifetime.Disposed);
        Assert.True(runtime.Models.Single(model => model.Model.Id == LocalTranscriptionPlugin.ModelId).Downloaded);
        _engine.Verify(engine => engine.UnloadModelAsync(), Times.Once);
        await runtime.ActivateAsync(LocalTranscriptionPlugin.ModelId);
        Assert.True(runtime.Ready);
        Assert.Equal(2, _loads.Count);
    }

    [Fact]
    public async Task FailedUnloadDoesNotPublishAnUnloadedModel()
    {
        await using var runtime = Create(); await runtime.InitializeAsync();
        _engine.Setup(engine => engine.UnloadModelAsync()).ThrowsAsync(new IOException("Unload failed"));
        await Assert.ThrowsAsync<IOException>(() => runtime.UnloadAsync(default));
        Assert.True(runtime.Ready);
        Assert.False(runtime.Busy);
        Assert.False(_lifetime.Disposed);
    }

    [Fact]
    public async Task CanceledUnloadDoesNotCallNativeEngine()
    {
        await using var runtime = Create(); await runtime.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.UnloadAsync(cancellation.Token));
        Assert.True(runtime.Ready);
        _engine.Verify(engine => engine.UnloadModelAsync(), Times.Never);
    }

    [Fact]
    public async Task CancellationDuringUnloadDrainsNativeWorkAndReflectsReleasedResources()
    {
        await using var runtime = Create(); await runtime.InitializeAsync();
        var native = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Setup(engine => engine.UnloadModelAsync()).Returns(native.Task);
        using var cancellation = new CancellationTokenSource();
        var unloading = runtime.UnloadAsync(cancellation.Token);
        cancellation.Cancel();
        Assert.False(unloading.IsCompleted);
        Assert.True(runtime.Busy);
        native.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unloading);
        Assert.False(runtime.Ready);
        Assert.False(runtime.Busy);
        Assert.True(runtime.Enabled);
    }

    [Fact]
    public async Task FullDecodeRetainsSdkSegmentsAndProbabilityWithoutReconstructingTiming()
    {
        var response = new PluginTranscriptionResult("Hallo Welt", "de", 2, 0.2f)
        { Segments = [new("Hallo", 0.12, 0.83), new("Welt", 1.1, 1.72)] };
        _engine.Setup(e => e.TranscribePcmAsync(It.IsAny<ReadOnlyMemory<float>>(), "de", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        await using var runtime = Create(); await runtime.InitializeAsync();
        var decoded = await runtime.DecodeResultAsync([0.1f], "de", false, default);
        Assert.Same(response, decoded);
        Assert.Same(response.Segments, decoded.Segments);
        Assert.Empty(decoded.TokenTimings);
    }

    [Fact]
    public async Task CancelDuringNativeDecodeDrainsThenRejectsLateResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new TaskCompletionSource<PluginTranscriptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Setup(e => e.TranscribePcmAsync(It.IsAny<ReadOnlyMemory<float>>(), null, false, It.IsAny<CancellationToken>()))
            .Returns(() => { entered.SetResult(); return native.Task; });
        await using var runtime = Create(); await runtime.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        var decoding = runtime.DecodeResultAsync([0.1f], null, false, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        Assert.False(decoding.IsCompleted);
        native.SetResult(new("Late text", "en", 1, null));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoding);
    }

    [Fact]
    public async Task NativeTranslationPassesTranslateFlagOnlyForCapableLoadedModel()
    {
        _downloaded.Add("canary");
        _engine.SetupGet(e => e.SupportsTranslation).Returns(() => _loads.LastOrDefault() == "canary");
        _engine.SetupGet(e => e.SupportedLanguages).Returns(["en", "de"]);
        _engine.Setup(e => e.TranscribePcmAsync(It.IsAny<ReadOnlyMemory<float>>(), "de", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PluginTranscriptionResult("Good morning", "de", 1, null));
        await using var runtime = Create(); await runtime.InitializeAsync();
        Assert.False(runtime.SupportsTranslation);
        await Assert.ThrowsAsync<NotSupportedException>(() => runtime.DecodeAsync([0f], false, translate: true));
        _engine.Verify(e => e.TranscribePcmAsync(It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        await runtime.ActivateAsync("canary"); runtime.SelectLanguage("de");
        Assert.True(runtime.SupportsTranslation);
        Assert.Equal("Good morning", (await runtime.DecodeAsync([0f], false, translate: true)).Text);
        _engine.Verify(e => e.TranscribePcmAsync(It.IsAny<ReadOnlyMemory<float>>(), "de", true, It.IsAny<CancellationToken>()), Times.Once);
        await runtime.ActivateAsync(LocalTranscriptionPlugin.ModelId);
        Assert.False(runtime.SupportsTranslation);
        await Assert.ThrowsAsync<NotSupportedException>(() => runtime.DecodeAsync([0f], false, translate: true));
        _engine.Verify(e => e.TranscribePcmAsync(It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

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
    [Theory]
    [InlineData(null)]
    [InlineData(0.95f)]
    public async Task SelectedLanguageIsPersistedAndPassedToTranscription(float? probability)
    {
        _engine.SetupGet(e => e.SupportedLanguages).Returns(["en", "de", "fr", "es"]);
        _engine.Setup(e => e.TranscribePcmAsync(It.IsAny<ReadOnlyMemory<float>>(), "de", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PluginTranscriptionResult("Hallo", "de", 1, probability));
        await using var runtime = Create(); await runtime.InitializeAsync(); runtime.SelectLanguage("de");
        var result = await runtime.DecodeAsync([0f], false);
        Assert.Equal("Hallo", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(probability, result.NoSpeechProbability);
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

    private void AllowRemoval()
    {
        _downloaded.Add("canary");
        _engine.SetupGet(e => e.SupportsModelRemoval).Returns(true);
        _engine.Setup(e => e.RemoveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string id, CancellationToken _) => { _downloaded.Remove(id); return Task.CompletedTask; });
    }

    [Fact]
    public async Task RemovalPreservesLoadedModelAndSavedSelection()
    {
        AllowRemoval();
        await using var runtime = Create(); await runtime.InitializeAsync();
        await runtime.RemoveAsync("canary", runtime.Generation, default);
        Assert.DoesNotContain("canary", _downloaded);
        Assert.Contains(LocalTranscriptionPlugin.ModelId, _downloaded);
        Assert.Equal(LocalTranscriptionPlugin.ModelId, runtime.ActiveModelId);
        Assert.Equal(LocalTranscriptionPlugin.ModelId, new VocabularyHostServices(_root).GetSetting<string>("SelectedModelId"));
        Assert.Single(_loads);
        Assert.False(runtime.Busy);
        Assert.Null(runtime.RemovingModelId);
    }

    [Fact]
    public async Task LoadedAndPluginSelectedModelsCannotBeRemoved()
    {
        AllowRemoval();
        await using var runtime = Create(); await runtime.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RemoveAsync(LocalTranscriptionPlugin.ModelId, runtime.Generation, default));
        _engine.SetupGet(e => e.SelectedModelId).Returns("canary");
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RemoveAsync("canary", runtime.Generation, default));
        _engine.Verify(e => e.RemoveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemovalRejectsReactivatedPackageAndUnknownModel()
    {
        AllowRemoval();
        await using var runtime = Create(); await runtime.InitializeAsync();
        var oldGeneration = runtime.Generation;
        await runtime.SetEnabledAsync(false); await runtime.SetEnabledAsync(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RemoveAsync("canary", oldGeneration, default));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.RemoveAsync("../canary", runtime.Generation, default));
        _engine.Verify(e => e.RemoveModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnsupportedRemovalAndFalseSuccessPreserveRetry()
    {
        _downloaded.Add("canary");
        await using var runtime = Create(); await runtime.InitializeAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => runtime.RemoveAsync("canary", runtime.Generation, default));
        _engine.SetupGet(e => e.SupportsModelRemoval).Returns(true);
        _engine.Setup(e => e.RemoveModelAsync("canary", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => runtime.RemoveAsync("canary", runtime.Generation, default));
        Assert.False(runtime.Busy);
        AllowRemoval();
        await runtime.RemoveAsync("canary", runtime.Generation, default);
        Assert.Null(runtime.Error);
    }

    [Fact]
    public async Task CancellationDrainsRemovalBeforeCompetingChanges()
    {
        AllowRemoval();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Setup(e => e.RemoveModelAsync("canary", It.IsAny<CancellationToken>()))
            .Returns(async () => { entered.SetResult(); await finish.Task; _downloaded.Remove("canary"); });
        await using var runtime = Create(); await runtime.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        var operation = runtime.RemoveAsync("canary", runtime.Generation, cancellation.Token);
        await entered.Task; cancellation.Cancel();
        Assert.False(operation.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ActivateAsync("canary"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SetEnabledAsync(false));
        finish.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(runtime.Busy);
        Assert.Contains("some files may already", runtime.Feedback);
        Assert.True(runtime.Ready);
    }

    [Fact]
    public async Task MissingModelRemovalDoesNotCallPluginAgain()
    {
        AllowRemoval();
        await using var runtime = Create(); await runtime.InitializeAsync();
        await runtime.RemoveAsync("canary", runtime.Generation, default);
        await runtime.RemoveAsync("canary", runtime.Generation, default);
        _engine.Verify(e => e.RemoveModelAsync("canary", It.IsAny<CancellationToken>()), Times.Once);
    }
}
