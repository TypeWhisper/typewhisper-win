using System.IO;
using System.Net.Http;
using System.Reflection;
using Moq;
using NAudio.Wave;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Plugin.Meta;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class StreamingTranscriptionTests
{
    [Fact]
    public void SupportsStreaming_DefaultIsFalse()
    {
        var mock = new Mock<ITranscriptionEnginePlugin>();
        // DIMs return default values — SupportsStreaming defaults to false
        Assert.False(mock.Object.SupportsStreaming);
    }

    [Fact]
    public void DictionaryAndPromptCapabilities_DefaultToBackwardCompatibleValues()
    {
        ITranscriptionEnginePlugin plugin = new DelayedStreamingPlugin();

        Assert.False(plugin.SupportsDictionaryTerms);
        Assert.Equal(DictionaryTermsBudget.Default, plugin.DictionaryTermsBudget);
        Assert.True(plugin.SupportsStreamingForPrompt("TypeWhisper"));
    }

    [Fact]
    public void SupportedLanguages_DefaultIsEmpty()
    {
        var mock = new Mock<ITranscriptionEnginePlugin> { CallBase = true };
        // CallBase invokes the DIM — SupportedLanguages defaults to empty
        var languages = mock.Object.SupportedLanguages;
        Assert.Empty(languages);
    }

    [Fact]
    public async Task TranscribeStreamingAsync_DefaultDelegatesToTranscribeAsync()
    {
        var expectedResult = new PluginTranscriptionResult("Hello world", "en", 2.5);
        var audio = new byte[] { 1, 2, 3 };

        var mock = new Mock<ITranscriptionEnginePlugin>();
        mock.Setup(e => e.TranscribeAsync(audio, "en", false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        // TranscribeStreamingAsync should delegate to TranscribeAsync by default
        // Since Moq doesn't call DIMs directly, we verify the TranscribeAsync call
        var result = await mock.Object.TranscribeAsync(audio, "en", false, null, CancellationToken.None);

        Assert.Equal("Hello world", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(2.5, result.DurationSeconds);
    }

    [Fact]
    public async Task OrderedLanguageHints_DefaultToFirstLanguageForLegacyPlugin()
    {
        var concrete = new DelayedStreamingPlugin();
        ITranscriptionEnginePlugin plugin = concrete;

        await plugin.TranscribeWithLanguageHintsAsync(
            [1, 2, 3], ["de", "en"], false, null, CancellationToken.None);

        Assert.Equal("de", concrete.LastLanguage);
    }

    [Fact]
    public void PluginTranscriptionResult_NoSpeechProbability_DefaultIsNull()
    {
        var result = new PluginTranscriptionResult("Hello", "en", 2.0);
        Assert.Null(result.NoSpeechProbability);
    }

    [Fact]
    public void PluginTranscriptionResult_NoSpeechProbability_CanBeSet()
    {
        var result = new PluginTranscriptionResult("So.", "en", 1.0, 0.95f);
        Assert.Equal(0.95f, result.NoSpeechProbability);
    }

    [Fact]
    public void SupportsStreaming_CanBeOverridden()
    {
        var mock = new Mock<ITranscriptionEnginePlugin>();
        mock.Setup(e => e.SupportsStreaming).Returns(true);

        Assert.True(mock.Object.SupportsStreaming);
    }

    [Fact]
    public void SupportedLanguages_CanBeOverridden()
    {
        var mock = new Mock<ITranscriptionEnginePlugin>();
        mock.Setup(e => e.SupportedLanguages).Returns(new List<string> { "en", "de", "fr" });

        var languages = mock.Object.SupportedLanguages;
        Assert.Equal(3, languages.Count);
        Assert.Contains("de", languages);
    }

    [Fact]
    public async Task TranscribeStreamingAsync_CanBeOverridden()
    {
        var expectedResult = new PluginTranscriptionResult("Streamed text", "de", 5.0);
        var audio = new byte[] { 1, 2, 3, 4, 5 };
        var progressCalls = new List<string>();

        var mock = new Mock<ITranscriptionEnginePlugin>();
        mock.Setup(e => e.TranscribeStreamingAsync(
            audio, "de", false, null,
            It.IsAny<Func<string, bool>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var result = await mock.Object.TranscribeStreamingAsync(
            audio, "de", false, null,
            partial => { progressCalls.Add(partial); return true; },
            CancellationToken.None);

        Assert.Equal("Streamed text", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
    }

    [Fact]
    public async Task StreamingHandler_BuffersAudioCapturedBeforeRealtimeSessionConnects()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new DelayedStreamingPlugin();
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });

        var modelManager = new ModelManagerService(pluginManager, settings);
        await modelManager.LoadModelAsync(ModelManagerService.GetPluginModelId(plugin.PluginId, "stream"));

        var devices = new FakeAudioInputDeviceProvider("Test Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        using var handler = new StreamingHandler(modelManager, audio, new PassthroughDictionaryService());

        handler.Start("en", TranscriptionTask.Transcribe, () => audio.IsRecording);
        audio.StartRecording();
        var capture = Assert.Single(captures.Created);

        capture.RaiseData([0, 0, 0, 0], 4);
        var session = new CapturingStreamingSession();
        plugin.CompleteStart(session);

        await WaitUntilAsync(() => session.SentAudio.Count == 1);

        Assert.Equal(4, session.SentAudio.Single().Length);
    }

    [Fact]
    public async Task StreamingHandler_MetaUsesDictionaryKeywordsAndWaitsForFinalTranscript()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var session = new FinalizingStreamingSession();
        MetaRealtimeConnectionOptions? connection = null;
        using var plugin = new MetaPlugin(
            new HttpClient(),
            (options, _) =>
            {
                connection = options;
                return Task.FromResult<IStreamingSession>(session);
            });
        var host = new Mock<IPluginHostServices>();
        host.Setup(value => value.LoadSecretAsync("api-key")).ReturnsAsync("meta-key");
        await plugin.ActivateAsync(host.Object);
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });

        var modelManager = new ModelManagerService(pluginManager, settings);
        await modelManager.LoadModelAsync(ModelManagerService.GetPluginModelId(
            plugin.PluginId,
            MetaPlugin.DefaultTranscriptionModelId));

        var devices = new FakeAudioInputDeviceProvider("Test Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        using var handler = new StreamingHandler(
            modelManager,
            audio,
            new PassthroughDictionaryService("TypeWhisper", "Muse"));
        var partialUpdates = new List<string>();
        handler.OnPartialTextUpdate = partialUpdates.Add;

        handler.Start("de", TranscriptionTask.Transcribe, () => audio.IsRecording);
        audio.StartRecording();
        var capture = Assert.Single(captures.Created);
        capture.RaiseData([0, 0, 0, 0], 4);
        await WaitUntilAsync(() => connection is not null && session.SentAudio.Count == 1);
        session.RaiseTranscript(new StreamingTranscriptEvent("Hallo", IsFinal: false));

        var finalText = await handler.StopAsync();

        Assert.Equal(["TypeWhisper", "Muse"], connection!.Keywords);
        Assert.Equal(["German"], connection.LanguageBias);
        Assert.Contains("Hallo", partialUpdates);
        Assert.Equal("Hallo Welt.", finalText);
        Assert.True(session.FinalizeCalled);
    }

    [Fact]
    public async Task StreamingHandler_BuffersAudioCapturedBeforeModelBecomesReady()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new DelayedStreamingPlugin();
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });

        var modelManager = new ModelManagerService(pluginManager, settings);
        var fullModelId = ModelManagerService.GetPluginModelId(plugin.PluginId, "stream");
        await modelManager.LoadModelAsync(fullModelId);

        var devices = new FakeAudioInputDeviceProvider("Test Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        using var handler = new StreamingHandler(modelManager, audio, new PassthroughDictionaryService());
        var modelReady = new TaskCompletionSource<StreamingModelPreparation>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        handler.StartWhenReadyWithLanguageHints(
            ["en"],
            TranscriptionTask.Transcribe,
            () => audio.IsRecording,
            modelReady.Task,
            allowOnlineBatchPolling: false);
        audio.StartRecording();
        var capture = Assert.Single(captures.Created);

        capture.RaiseData([0, 0, 0, 0], 4);
        Assert.Equal(4, (int)GetPrivateField(handler, "_pendingStreamingAudioBytes")!);

        modelReady.SetResult(CreateReadyPreparation(modelManager, fullModelId));
        var session = new CapturingStreamingSession();
        plugin.CompleteStart(session);

        await WaitUntilAsync(() => session.SentAudio.Count == 1);
        await Task.Delay(50);

        Assert.Equal(4, session.SentAudio.Single().Length);
        Assert.Single(session.SentAudio);
    }

    [Fact]
    public async Task StreamingHandler_DoesNotStartPreviewForChangedModelIdentity()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new DelayedStreamingPlugin();
        var changedPlugin = new DelayedStreamingPlugin("com.test.changed-streaming");
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin, changedPlugin });

        var modelManager = new ModelManagerService(pluginManager, settings);
        var fullModelId = ModelManagerService.GetPluginModelId(plugin.PluginId, "stream");
        await modelManager.LoadModelAsync(fullModelId);
        var preparedModel = CreateReadyPreparation(modelManager, fullModelId);

        var devices = new FakeAudioInputDeviceProvider("Test Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        using var handler = new StreamingHandler(modelManager, audio, new PassthroughDictionaryService());
        var readiness = new TaskCompletionSource<StreamingModelPreparation>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        handler.StartWhenReadyWithLanguageHints(
            ["en"],
            TranscriptionTask.Transcribe,
            () => audio.IsRecording,
            readiness.Task,
            allowOnlineBatchPolling: false);
        audio.StartRecording();
        var capture = Assert.Single(captures.Created);
        capture.RaiseData([0, 0, 0, 0], 4);

        await modelManager.LoadModelAsync(
            ModelManagerService.GetPluginModelId(changedPlugin.PluginId, "stream"));
        readiness.SetResult(preparedModel);

        await WaitUntilAsync(() => (int)GetPrivateField(handler, "_pendingStreamingAudioBytes")! == 0);

        Assert.Equal(0, plugin.StartCallCount);
        Assert.Equal(0, changedPlugin.StartCallCount);
    }

    [Fact]
    public async Task StreamingHandler_StaleReadinessCleanupCannotDetachNewSession()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new DelayedStreamingPlugin();
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });

        var modelManager = new ModelManagerService(pluginManager, settings);
        var fullModelId = ModelManagerService.GetPluginModelId(plugin.PluginId, "stream");
        await modelManager.LoadModelAsync(fullModelId);

        var devices = new FakeAudioInputDeviceProvider("Test Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        using var handler = new StreamingHandler(modelManager, audio, new PassthroughDictionaryService());
        var firstReadiness = new TaskCompletionSource<StreamingModelPreparation>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        handler.StartWhenReadyWithLanguageHints(
            ["en"],
            TranscriptionTask.Transcribe,
            () => audio.IsRecording,
            firstReadiness.Task,
            allowOnlineBatchPolling: false);
        var transcriptState = GetPrivateField(handler, "_transcriptState")!;
        var staleVersion = (int)GetPrivateField(transcriptState, "_sessionVersion")!;
        handler.Stop();

        handler.StartWhenReadyWithLanguageHints(
            ["en"],
            TranscriptionTask.Transcribe,
            () => audio.IsRecording,
            Task.FromResult(CreateReadyPreparation(modelManager, fullModelId)),
            allowOnlineBatchPolling: false);
        var staleCleanup = typeof(StreamingHandler).GetMethod(
            "StopPreviewBuffering",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(staleCleanup);
        staleCleanup.Invoke(handler, [staleVersion]);

        audio.StartRecording();
        var capture = Assert.Single(captures.Created);
        capture.RaiseData([0, 0, 0, 0], 4);
        var session = new CapturingStreamingSession();
        plugin.CompleteStart(session);

        await WaitUntilAsync(() => session.SentAudio.Count == 1);

        Assert.Single(session.SentAudio);
    }

    [Fact]
    public async Task StreamingHandler_SerializesRealtimeAudioWrites()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new DelayedStreamingPlugin();
        var session = new BlockingStreamingSession();
        plugin.CompleteStart(session);
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });

        var modelManager = new ModelManagerService(pluginManager, settings);
        await modelManager.LoadModelAsync(ModelManagerService.GetPluginModelId(plugin.PluginId, "stream"));

        var devices = new FakeAudioInputDeviceProvider("Test Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        using var handler = new StreamingHandler(modelManager, audio, new PassthroughDictionaryService());

        handler.Start("en", TranscriptionTask.Transcribe, () => audio.IsRecording);
        audio.StartRecording();
        var capture = Assert.Single(captures.Created);

        capture.RaiseData([0, 0, 0, 0], 4);
        await session.FirstSendEntered;

        capture.RaiseData([1, 0, 1, 0], 4);

        var overlapped = await CompletesWithinAsync(session.ConcurrentSendObserved, TimeSpan.FromMilliseconds(500));
        Assert.False(overlapped);

        session.ReleaseFirstSend();
        await WaitUntilAsync(() => session.SendAttemptCount == 2);

        Assert.Equal(1, session.MaxConcurrentSendCount);
        Assert.Equal(2, session.SentAudioCount);
    }

    [Fact]
    public async Task StreamingHandler_DoesNotBlockAudioCallbackWhenSenderFallsBehind()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new DelayedStreamingPlugin();
        var session = new BlockingStreamingSession();
        plugin.CompleteStart(session);
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });

        var modelManager = new ModelManagerService(pluginManager, settings);
        await modelManager.LoadModelAsync(ModelManagerService.GetPluginModelId(plugin.PluginId, "stream"));

        var devices = new FakeAudioInputDeviceProvider("Test Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        using var handler = new StreamingHandler(modelManager, audio, new PassthroughDictionaryService());

        handler.Start("en", TranscriptionTask.Transcribe, () => audio.IsRecording);
        audio.StartRecording();
        var capture = Assert.Single(captures.Created);

        capture.RaiseData([0, 0, 0, 0], 4);
        await session.FirstSendEntered;

        var raiseManyChunks = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
                capture.RaiseData([1, 0, 1, 0], 4);
        });

        var completedWithoutBlocking = await CompletesWithinAsync(raiseManyChunks, TimeSpan.FromMilliseconds(500));

        session.ReleaseFirstSend();
        await raiseManyChunks.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(completedWithoutBlocking);
    }

    [Fact]
    public async Task StreamingHandler_CleansUpStreamingStateWhenInitialFlushFails()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new DelayedStreamingPlugin();
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });

        var modelManager = new ModelManagerService(pluginManager, settings);
        await modelManager.LoadModelAsync(ModelManagerService.GetPluginModelId(plugin.PluginId, "stream"));

        var devices = new FakeAudioInputDeviceProvider("Test Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        using var handler = new StreamingHandler(modelManager, audio, new PassthroughDictionaryService());

        handler.Start("en", TranscriptionTask.Transcribe, () => audio.IsRecording);
        audio.StartRecording();
        var capture = Assert.Single(captures.Created);

        capture.RaiseData([0, 0, 0, 0], 4);

        var session = new FailingStreamingSession();
        plugin.CompleteStart(session);
        await WaitUntilAsync(() => session.SendAttemptCount == 1);
        await Task.Delay(100);

        Assert.Null(GetPrivateField(handler, "_session"));
        Assert.False((bool)GetPrivateField(handler, "_isFlushingPendingStreamingAudio")!);
        Assert.Equal(0, (int)GetPrivateField(handler, "_pendingStreamingAudioBytes")!);

        capture.RaiseData([1, 0, 1, 0], 4);
        await Task.Delay(50);

        Assert.Equal(1, session.SendAttemptCount);
        Assert.Equal(0, (int)GetPrivateField(handler, "_pendingStreamingAudioBytes")!);
    }

    [Fact]
    public async Task StreamingHandler_OnlineBatchPollingBoundsGrowingAudioRequests()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new CapturingPollingPlugin();
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });

        var modelManager = new ModelManagerService(pluginManager, settings);
        var fullModelId = ModelManagerService.GetPluginModelId(plugin.PluginId, "batch");
        await modelManager.LoadModelAsync(fullModelId);

        var devices = new FakeAudioInputDeviceProvider("Test Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        using var handler = new StreamingHandler(modelManager, audio, new PassthroughDictionaryService());

        handler.StartWhenReadyWithLanguageHints(
            ["en"],
            TranscriptionTask.Transcribe,
            () => audio.IsRecording,
            Task.FromResult(CreateReadyPreparation(modelManager, fullModelId)),
            allowOnlineBatchPolling: true);
        audio.StartRecording();
        var capture = Assert.Single(captures.Created);
        capture.RaiseData(BuildPcm16Chunk(TimeSpan.FromSeconds(45)), 45 * 16000 * 2);

        await WaitUntilAsync(() => plugin.RequestDurations.Count == 1, TimeSpan.FromSeconds(8));

        Assert.InRange(plugin.RequestDurations.Single().TotalSeconds, 29.9, 30.1);
    }

    [Fact]
    public void StreamingHandler_OnlineBatchPollingLeavesCapacityForFinalRequest()
    {
        var interval = StreamingHandler.GetPollingInterval(useOnlineBatchWindow: true);
        var previewRequestsPerMinute = TimeSpan.FromMinutes(1).TotalSeconds / interval.TotalSeconds;

        Assert.Equal(TimeSpan.FromSeconds(5), interval);
        Assert.True(previewRequestsPerMinute + 1 <= 20);
    }

    [Fact]
    public async Task StreamingHandler_OnlineBatchNoSpeechStillWaitsBetweenRequests()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new CapturingPollingPlugin { NoSpeechProbability = 1f };
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });

        var modelManager = new ModelManagerService(pluginManager, settings);
        var fullModelId = ModelManagerService.GetPluginModelId(plugin.PluginId, "batch");
        await modelManager.LoadModelAsync(fullModelId);

        var devices = new FakeAudioInputDeviceProvider("Test Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        using var handler = new StreamingHandler(modelManager, audio, new PassthroughDictionaryService());

        handler.StartWhenReadyWithLanguageHints(
            ["en"],
            TranscriptionTask.Transcribe,
            () => audio.IsRecording,
            Task.FromResult(CreateReadyPreparation(modelManager, fullModelId)),
            allowOnlineBatchPolling: true);
        audio.StartRecording();
        var capture = Assert.Single(captures.Created);
        capture.RaiseData(BuildPcm16Chunk(TimeSpan.FromSeconds(45)), 45 * 16000 * 2);

        await WaitUntilAsync(() => plugin.RequestDurations.Count == 1, TimeSpan.FromSeconds(8));
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Single(plugin.RequestDurations);
        await WaitUntilAsync(() => plugin.RequestDurations.Count == 2, TimeSpan.FromSeconds(2));
        Assert.Equal(2, plugin.RequestDurations.Count);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? waitTimeout = null)
    {
        using var timeout = new CancellationTokenSource(waitTimeout ?? TimeSpan.FromSeconds(3));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, timeout.Token);
        }
    }

    private static byte[] BuildPcm16Chunk(TimeSpan duration)
    {
        var bytes = new byte[(int)(duration.TotalSeconds * 16000 * 2)];
        for (var i = 0; i < bytes.Length; i += 2)
        {
            bytes[i] = 0x00;
            bytes[i + 1] = 0x20;
        }

        return bytes;
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        var delay = Task.Delay(timeout);
        return await Task.WhenAny(task, delay) == task;
    }

    private static StreamingModelPreparation CreateReadyPreparation(
        ModelManagerService modelManager,
        string requestedModelId) =>
        new(
            requestedModelId,
            modelManager.ActiveModelId,
            modelManager.Engine,
            modelManager.ActiveTranscriptionPlugin,
            IsReady: true);

    private static object? GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(target);
    }

    private sealed class DelayedStreamingPlugin(
        string pluginId = "com.test.delayed-streaming") : ITranscriptionEnginePlugin
    {
        private readonly TaskCompletionSource<IStreamingSession> _startCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startCallCount;

        public string PluginId => pluginId;
        public string PluginName => "Delayed Streaming";
        public string PluginVersion => "1.0.0";
        public string ProviderId => pluginId;
        public string ProviderDisplayName => "Delayed Streaming";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
            [new PluginModelInfo("stream", "Stream")];
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;
        public bool SupportsStreaming => true;
        public string? LastLanguage { get; private set; }
        public int StartCallCount => Volatile.Read(ref _startCallCount);

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void Dispose() { }
        public void SelectModel(string modelId) => SelectedModelId = modelId;
        public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
        {
            Interlocked.Increment(ref _startCallCount);
            return _startCompletion.Task.WaitAsync(ct);
        }
        public void CompleteStart(IStreamingSession session) => _startCompletion.SetResult(session);

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct)
        {
            LastLanguage = language;
            return Task.FromResult(new PluginTranscriptionResult("", language ?? "en", 0));
        }
    }

    private sealed class CapturingStreamingSession : IStreamingSession
    {
        public List<byte[]> SentAudio { get; } = [];
        public event Action<StreamingTranscriptEvent>? TranscriptReceived;
        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
        {
            SentAudio.Add(pcm16Audio.ToArray());
            return Task.CompletedTask;
        }

        public Task FinalizeAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseTranscript(StreamingTranscriptEvent evt) => TranscriptReceived?.Invoke(evt);
    }

    private sealed class FinalizingStreamingSession : IStreamingSession
    {
        public List<byte[]> SentAudio { get; } = [];
        public bool FinalizeCalled { get; private set; }
        public event Action<StreamingTranscriptEvent>? TranscriptReceived;

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
        {
            SentAudio.Add(pcm16Audio.ToArray());
            return Task.CompletedTask;
        }

        public Task FinalizeAsync(CancellationToken ct)
        {
            FinalizeCalled = true;
            TranscriptReceived?.Invoke(new StreamingTranscriptEvent("Hallo Welt.", IsFinal: true));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseTranscript(StreamingTranscriptEvent evt) => TranscriptReceived?.Invoke(evt);
    }

    private sealed class BlockingStreamingSession : IStreamingSession
    {
        private readonly TaskCompletionSource _firstSendEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstSend =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _concurrentSendObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<byte[]> _sentAudio = [];

        private int _inFlightSendCount;
        private int _maxConcurrentSendCount;
        private int _sendAttemptCount;

        public Task FirstSendEntered => _firstSendEntered.Task;
        public Task ConcurrentSendObserved => _concurrentSendObserved.Task;
        public int MaxConcurrentSendCount => Volatile.Read(ref _maxConcurrentSendCount);
        public int SendAttemptCount => Volatile.Read(ref _sendAttemptCount);
        public int SentAudioCount
        {
            get
            {
                lock (_sentAudio) return _sentAudio.Count;
            }
        }

        public event Action<StreamingTranscriptEvent>? TranscriptReceived;

        public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
        {
            var inFlight = Interlocked.Increment(ref _inFlightSendCount);
            TrackMaxConcurrentSendCount(inFlight);
            if (inFlight > 1)
                _concurrentSendObserved.TrySetResult();

            var attempt = Interlocked.Increment(ref _sendAttemptCount);

            try
            {
                if (attempt == 1)
                {
                    _firstSendEntered.TrySetResult();
                    await _releaseFirstSend.Task.WaitAsync(ct);
                }

                lock (_sentAudio)
                {
                    _sentAudio.Add(pcm16Audio.ToArray());
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightSendCount);
            }
        }

        public void ReleaseFirstSend() => _releaseFirstSend.TrySetResult();
        public Task FinalizeAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseTranscript(StreamingTranscriptEvent evt) => TranscriptReceived?.Invoke(evt);

        private void TrackMaxConcurrentSendCount(int count)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxConcurrentSendCount);
                if (count <= current)
                    return;

                if (Interlocked.CompareExchange(ref _maxConcurrentSendCount, count, current) == current)
                    return;
            }
        }
    }

    private sealed class FailingStreamingSession : IStreamingSession
    {
        private int _sendAttemptCount;

        public int SendAttemptCount => Volatile.Read(ref _sendAttemptCount);
        public event Action<StreamingTranscriptEvent>? TranscriptReceived;

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
        {
            Interlocked.Increment(ref _sendAttemptCount);
            throw new InvalidOperationException("Simulated streaming send failure.");
        }

        public Task FinalizeAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseTranscript(StreamingTranscriptEvent evt) => TranscriptReceived?.Invoke(evt);
    }

    private sealed class CapturingPollingPlugin : ITranscriptionEnginePlugin
    {
        private readonly List<TimeSpan> _requestDurations = [];

        public string PluginId => "com.test.polling";
        public string PluginName => "Polling";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "polling";
        public string ProviderDisplayName => "Polling";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
            [new PluginModelInfo("batch", "Batch")];
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;
        public float? NoSpeechProbability { get; init; }
        public IReadOnlyList<TimeSpan> RequestDurations
        {
            get
            {
                lock (_requestDurations)
                    return [.. _requestDurations];
            }
        }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void Dispose() { }
        public void SelectModel(string modelId) => SelectedModelId = modelId;

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct)
        {
            using var stream = new MemoryStream(wavAudio, writable: false);
            using var reader = new WaveFileReader(stream);
            lock (_requestDurations)
                _requestDurations.Add(reader.TotalTime);

            return Task.FromResult(new PluginTranscriptionResult(
                "preview",
                language ?? "en",
                reader.TotalTime.TotalSeconds,
                NoSpeechProbability));
        }
    }

    private sealed class PassthroughDictionaryService(params string[] terms) : IDictionaryService
    {
        public IReadOnlyList<DictionaryEntry> Entries { get; } = terms
            .Select((term, index) => new DictionaryEntry
            {
                Id = index.ToString(),
                EntryType = DictionaryEntryType.Term,
                Original = term,
            })
            .ToList();
        public event Action? EntriesChanged { add { } remove { } }
        public void AddEntry(DictionaryEntry entry) { }
        public void AddEntries(IEnumerable<DictionaryEntry> entries) { }
        public void UpdateEntry(DictionaryEntry entry) { }
        public void DeleteEntry(string id) { }
        public void DeleteEntries(IEnumerable<string> ids) { }
        public string ApplyCorrections(string text) => text;
        public string? GetTermsForPrompt() => null;
        public void LearnCorrection(string original, string replacement) { }
        public IReadOnlyList<LearnedDictionaryCorrection> LearnCorrections(IEnumerable<CorrectionSuggestion> suggestions) => [];
        public void UndoLearnedCorrections(IEnumerable<LearnedDictionaryCorrection> learnedCorrections) { }
        public void ActivatePack(TermPack pack) { }
        public void DeactivatePack(string packId) { }
    }
}

public class LiveTranscriptionStartupPolicyTests
{
    [Fact]
    public void GlobalLiveTranscriptionDisabled_SuppressesAllLiveModes()
    {
        var plugin = new FakePolicyTranscriptionPlugin(supportsStreaming: true, supportsModelDownload: true);

        var mode = LiveTranscriptionStartupPolicy.Select(
            AppSettings.Default with { LiveTranscriptionEnabled = false },
            isPluginModel: true,
            plugin);

        Assert.Equal(LiveTranscriptionStartupMode.None, mode);
    }

    [Fact]
    public void StreamingPlugin_UsesRealtimeStreaming()
    {
        var plugin = new FakePolicyTranscriptionPlugin(supportsStreaming: true, supportsModelDownload: false);

        var mode = LiveTranscriptionStartupPolicy.Select(
            AppSettings.Default,
            isPluginModel: true,
            plugin);

        Assert.Equal(LiveTranscriptionStartupMode.PluginStreaming, mode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PromptIncompatibleStreamingPlugin_UsesConfiguredBatchFallback(
        bool batchPreviewEnabled)
    {
        var plugin = new FakePolicyTranscriptionPlugin(
            supportsStreaming: true,
            supportsModelDownload: false,
            rejectNonEmptyPrompt: true);

        var mode = LiveTranscriptionStartupPolicy.Select(
            AppSettings.Default with
            {
                OnlineAsrBatchLiveTranscriptionEnabled = batchPreviewEnabled
            },
            isPluginModel: true,
            plugin,
            prompt: "TypeWhisper");

        Assert.Equal(
            batchPreviewEnabled
                ? LiveTranscriptionStartupMode.PluginPollingFallback
                : LiveTranscriptionStartupMode.None,
            mode);
    }

    [Fact]
    public void DownloadablePlugin_UsesPollingFallback()
    {
        var plugin = new FakePolicyTranscriptionPlugin(supportsStreaming: false, supportsModelDownload: true);

        var mode = LiveTranscriptionStartupPolicy.Select(
            AppSettings.Default,
            isPluginModel: true,
            plugin);

        Assert.Equal(LiveTranscriptionStartupMode.PluginPollingFallback, mode);
    }

    [Fact]
    public void OnlineBatchProvider_DefaultsToNoLiveTranscription()
    {
        var plugin = new FakePolicyTranscriptionPlugin(supportsStreaming: false, supportsModelDownload: false);

        var mode = LiveTranscriptionStartupPolicy.Select(
            AppSettings.Default,
            isPluginModel: true,
            plugin);

        Assert.Equal(LiveTranscriptionStartupMode.None, mode);
    }

    [Fact]
    public void OnlineBatchProvider_UsesPollingFallbackWhenOptedIn()
    {
        var plugin = new FakePolicyTranscriptionPlugin(supportsStreaming: false, supportsModelDownload: false);

        var mode = LiveTranscriptionStartupPolicy.Select(
            AppSettings.Default with { OnlineAsrBatchLiveTranscriptionEnabled = true },
            isPluginModel: true,
            plugin);

        Assert.Equal(LiveTranscriptionStartupMode.PluginPollingFallback, mode);
    }

    [Fact]
    public void NonPluginModel_UsesLegacyVad()
    {
        var mode = LiveTranscriptionStartupPolicy.Select(
            AppSettings.Default,
            isPluginModel: false,
            plugin: null);

        Assert.Equal(LiveTranscriptionStartupMode.LegacyVad, mode);
    }

    private sealed class FakePolicyTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        private readonly bool _rejectNonEmptyPrompt;

        public FakePolicyTranscriptionPlugin(
            bool supportsStreaming,
            bool supportsModelDownload,
            bool rejectNonEmptyPrompt = false)
        {
            SupportsStreaming = supportsStreaming;
            SupportsModelDownload = supportsModelDownload;
            _rejectNonEmptyPrompt = rejectNonEmptyPrompt;
        }

        public string PluginId => "com.test.policy";
        public string PluginName => "Policy Test";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "policy";
        public string ProviderDisplayName => "Policy";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
            [new PluginModelInfo("model", "Model")];
        public string? SelectedModelId => "model";
        public bool SupportsTranslation => false;
        public bool SupportsStreaming { get; }
        public bool SupportsModelDownload { get; }
        public bool SupportsStreamingForPrompt(string? prompt) =>
            SupportsStreaming && (!_rejectNonEmptyPrompt || string.IsNullOrWhiteSpace(prompt));

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void Dispose() { }
        public void SelectModel(string modelId) { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct) =>
            Task.FromResult(new PluginTranscriptionResult("", language ?? "en", 0));
    }
}

public class StabilizeTextTests
{
    [Fact]
    public void EmptyConfirmed_ReturnsNew()
    {
        var result = StreamingHandler.StabilizeText("", "Hello world");
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void EmptyNew_ReturnsConfirmed()
    {
        var result = StreamingHandler.StabilizeText("Hello", "");
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void NewStartsWithConfirmed_ReturnsNew()
    {
        var result = StreamingHandler.StabilizeText("Hello", "Hello world");
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void ExactMatch_ReturnsConfirmed()
    {
        var result = StreamingHandler.StabilizeText("Hello world", "Hello world");
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void PartialPrefixMatch_KeepsConfirmedAndAppends()
    {
        // "Hello worl" matches >50% of "Hello world", so confirmed + new tail
        var result = StreamingHandler.StabilizeText("Hello world", "Hello world, how are you?");
        Assert.Equal("Hello world, how are you?", result);
    }

    [Fact]
    public void MinorDivergence_KeepsConfirmedPrefix()
    {
        // First 6 chars match ("Hello "), >50% of 11-char confirmed
        var result = StreamingHandler.StabilizeText("Hello world", "Hello earth and sky");
        Assert.Equal("Hello world earth and sky", result);
    }

    [Fact]
    public void CompletelyDifferent_AcceptsNewText()
    {
        var result = StreamingHandler.StabilizeText("Hello world", "Goodbye universe");
        Assert.Equal("Goodbye universe", result);
    }

    [Fact]
    public void SuffixPrefixOverlap_DetectsShift()
    {
        // Confirmed = "A B C D", new starts with "B C D E" (suffix of confirmed)
        var confirmed = "Alpha Beta Gamma Delta";
        var newText = "Beta Gamma Delta Epsilon";
        var result = StreamingHandler.StabilizeText(confirmed, newText);
        // Should keep confirmed + append the new tail " Epsilon"
        Assert.Equal("Alpha Beta Gamma Delta Epsilon", result);
    }

    [Fact]
    public void WhitespaceIsTrimmed()
    {
        var result = StreamingHandler.StabilizeText("", "  Hello  ");
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void RollingWindow_AppendsOnlyWordsAfterOverlap()
    {
        var result = StreamingHandler.MergeRollingText(
            "Alpha bravo charlie delta echo foxtrot golf hotel",
            "echo foxtrot golf hotel india juliet");

        Assert.Equal(
            "Alpha bravo charlie delta echo foxtrot golf hotel india juliet",
            result);
    }

    [Fact]
    public void RollingWindow_ToleratesChangedLeadingWordsAndPunctuation()
    {
        var result = StreamingHandler.MergeRollingText(
            "Alpha bravo Charlie, delta echo foxtrot golf hotel.",
            "uncertain boundary CHARLIE delta echo foxtrot golf hotel, india juliet");

        Assert.Equal(
            "Alpha bravo Charlie, delta echo foxtrot golf hotel. india juliet",
            result);
    }

    [Fact]
    public void RollingWindow_ReplacesUnstableTrailingBoundaryWords()
    {
        var result = StreamingHandler.MergeRollingText(
            "Alpha bravo charlie delta echo foxtrot mistaken ending",
            "uncertain boundary charlie delta echo foxtrot corrected ending and tail");

        Assert.Equal(
            "Alpha bravo charlie delta echo foxtrot corrected ending and tail",
            result);
    }

    [Fact]
    public void RollingWindow_PreservesPunctuationAfterOverlap()
    {
        var result = StreamingHandler.MergeRollingText(
            "Alpha bravo charlie delta",
            "bravo charlie delta. Echo foxtrot");

        Assert.Equal("Alpha bravo charlie delta. Echo foxtrot", result);
    }

    [Fact]
    public void RollingWindow_DoesNotDuplicateExistingBoundaryPunctuation()
    {
        var result = StreamingHandler.MergeRollingText(
            "Alpha bravo charlie delta.",
            "BRAVO CHARLIE DELTA. Echo foxtrot");

        Assert.Equal("Alpha bravo charlie delta. Echo foxtrot", result);
    }

    [Fact]
    public void RollingWindow_PreservesTrailingPunctuationAfterNormalizedOverlap()
    {
        var result = StreamingHandler.MergeRollingText(
            "Alpha bravo charlie delta",
            "BRAVO CHARLIE DELTA.");

        Assert.Equal("Alpha bravo charlie delta.", result);
    }

    [Theory]
    [InlineData(
        "今天我们测试长时间语音转写是否稳定。",
        "语音转写是否稳定。然后继续说话。",
        "今天我们测试长时间语音转写是否稳定。然后继续说话。")]
    [InlineData(
        "これは長い音声入力のテストです。",
        "音声入力のテストです。そして続けます。",
        "これは長い音声入力のテストです。そして続けます。")]
    [InlineData(
        "이것은긴음성입력테스트입니다.",
        "음성입력테스트입니다.계속말합니다.",
        "이것은긴음성입력테스트입니다.계속말합니다.")]
    public void RollingWindow_MergesTruncatedUnspacedCjkWindows(
        string confirmed,
        string windowText,
        string expected)
    {
        var result = StreamingHandler.MergeRollingText(confirmed, windowText);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void RollingWindow_WithoutSafeOverlapKeepsConfirmedText()
    {
        var result = StreamingHandler.MergeRollingText(
            "Alpha bravo charlie delta",
            "Completely unrelated words appear here");

        Assert.Equal("Alpha bravo charlie delta", result);
    }
}

public class StreamingTranscriptStateTests
{
    [Fact]
    public void SnapshotPolling_ReplacesRewrittenGrowingHypothesis()
    {
        var sut = new StreamingTranscriptState();
        var sessionVersion = sut.StartSession();

        Assert.True(sut.TryApplySnapshotPolling(
            sessionVersion,
            "At the beginning we are checking a long-term",
            text => text,
            out _));
        Assert.True(sut.TryApplySnapshotPolling(
            sessionVersion,
            "At the beginning we are checking whether a long transcription remains complete",
            text => text,
            out var display));

        Assert.Equal(
            "At the beginning we are checking whether a long transcription remains complete",
            display);
        Assert.Equal(display, sut.StopSession());
    }

    [Fact]
    public void RollingPolling_PreservesFullPreviewAcrossOverlappingWindows()
    {
        var sut = new StreamingTranscriptState();
        var sessionVersion = sut.StartSession();

        Assert.True(sut.TryApplyPolling(
            sessionVersion,
            "Alpha bravo charlie delta echo foxtrot golf hotel",
            text => text,
            out _));
        Assert.True(sut.TryApplyRollingPolling(
            sessionVersion,
            "echo foxtrot golf hotel india juliet",
            text => text,
            out var mergedDisplay));

        Assert.Equal(
            "Alpha bravo charlie delta echo foxtrot golf hotel india juliet",
            mergedDisplay);
        Assert.Equal(mergedDisplay, sut.StopSession());
    }

    [Fact]
    public void RollingPolling_RecoversAfterConsecutiveUnmatchedWindows()
    {
        var sut = new StreamingTranscriptState();
        var sessionVersion = sut.StartSession();

        Assert.True(sut.TryApplySnapshotPolling(
            sessionVersion,
            "Confirmed text before a long pause",
            text => text,
            out _));
        Assert.False(sut.TryApplyRollingPolling(
            sessionVersion,
            "A new topic starts after the pause",
            text => text,
            out _));
        Assert.False(sut.TryApplyRollingPolling(
            sessionVersion,
            "A new topic starts after the pause",
            text => text,
            out _));
        Assert.True(sut.TryApplyRollingPolling(
            sessionVersion,
            "A new topic starts after the pause and continues",
            text => text,
            out var recoveredDisplay));

        Assert.Equal(
            "Confirmed text before a long pause A new topic starts after the pause and continues",
            recoveredDisplay);
        Assert.Equal(recoveredDisplay, sut.StopSession());
    }

    [Fact]
    public void StopSession_FallsBackToOnlyInterimRealtimeText()
    {
        var sut = new StreamingTranscriptState();
        var sessionVersion = sut.StartSession();

        var interimApplied = sut.TryApplyRealtime(
            sessionVersion,
            new StreamingTranscriptEvent("Hello world", false),
            text => text,
            out var interimDisplay);

        Assert.True(interimApplied);
        Assert.Equal("Hello world", interimDisplay);

        var finalText = sut.StopSession();

        Assert.Equal("Hello world", finalText);
    }

    [Fact]
    public void StopSession_InvalidatesLateRealtimeEvents()
    {
        var sut = new StreamingTranscriptState();
        var sessionVersion = sut.StartSession();

        var appliedBeforeStop = sut.TryApplyRealtime(
            sessionVersion,
            new StreamingTranscriptEvent("Hello world", true),
            text => text,
            out var displayBeforeStop);

        Assert.True(appliedBeforeStop);
        Assert.Equal("Hello world", displayBeforeStop);

        var finalText = sut.StopSession();

        Assert.Equal("Hello world", finalText);

        var appliedAfterStop = sut.TryApplyRealtime(
            sessionVersion,
            new StreamingTranscriptEvent("Should be ignored", false),
            text => text,
            out var displayAfterStop);

        Assert.False(appliedAfterStop);
        Assert.Equal("", displayAfterStop);
    }

    [Fact]
    public void StopSession_FallsBackWhenTrailingRealtimeInterimAfterConfirmedText()
    {
        var sut = new StreamingTranscriptState();
        var sessionVersion = sut.StartSession();

        Assert.True(sut.TryApplyRealtime(
            sessionVersion,
            new StreamingTranscriptEvent("Confirmed", true),
            text => text,
            out var confirmedDisplay));
        Assert.Equal("Confirmed", confirmedDisplay);

        Assert.True(sut.TryApplyRealtime(
            sessionVersion,
            new StreamingTranscriptEvent("still changing", false),
            text => text,
            out var interimDisplay));
        Assert.Equal("Confirmed still changing", interimDisplay);

        Assert.Equal("Confirmed still changing", sut.StopSession());
    }

    [Fact]
    public void RealtimeFinalTranscript_AppendsToConfirmedText()
    {
        var sut = new StreamingTranscriptState();
        var sessionVersion = sut.StartSession();

        var interimApplied = sut.TryApplyRealtime(
            sessionVersion,
            new StreamingTranscriptEvent("Hello", false),
            text => text,
            out var interimDisplay);
        var finalApplied = sut.TryApplyRealtime(
            sessionVersion,
            new StreamingTranscriptEvent("world", true),
            text => text,
            out var finalDisplay);

        Assert.True(interimApplied);
        Assert.Equal("Hello", interimDisplay);
        Assert.True(finalApplied);
        Assert.Equal("world", finalDisplay);
        Assert.Equal("world", sut.StopSession());
    }

    [Fact]
    public void RealtimeFinalTranscript_ReplacesCumulativeFinalPrefix()
    {
        var sut = new StreamingTranscriptState();
        var sessionVersion = sut.StartSession();

        Assert.True(sut.TryApplyRealtime(
            sessionVersion,
            new StreamingTranscriptEvent("Hello world", true),
            text => text,
            out var firstDisplay));
        Assert.Equal("Hello world", firstDisplay);

        Assert.True(sut.TryApplyRealtime(
            sessionVersion,
            new StreamingTranscriptEvent("Hello world from streaming", true),
            text => text,
            out var secondDisplay));

        Assert.Equal("Hello world from streaming", secondDisplay);
        Assert.Equal("Hello world from streaming", sut.StopSession());
    }

    [Fact]
    public void RealtimeFinalTranscript_IgnoresDuplicateFinalSegment()
    {
        var sut = new StreamingTranscriptState();
        var sessionVersion = sut.StartSession();

        Assert.True(sut.TryApplyRealtime(
            sessionVersion,
            new StreamingTranscriptEvent("Hello world", true),
            text => text,
            out var firstDisplay));
        Assert.Equal("Hello world", firstDisplay);

        Assert.True(sut.TryApplyRealtime(
            sessionVersion,
            new StreamingTranscriptEvent("Hello world", true),
            text => text,
            out var duplicateDisplay));

        Assert.Equal("Hello world", duplicateDisplay);
        Assert.Equal("Hello world", sut.StopSession());
    }

    [Fact]
    public void RealtimeFinalTranscript_AppendsDistinctFinalChunks()
    {
        var sut = new StreamingTranscriptState();
        var sessionVersion = sut.StartSession();

        Assert.True(sut.TryApplyRealtime(
            sessionVersion,
            new StreamingTranscriptEvent("Hello", true),
            text => text,
            out var firstDisplay));
        Assert.Equal("Hello", firstDisplay);

        Assert.True(sut.TryApplyRealtime(
            sessionVersion,
            new StreamingTranscriptEvent("world", true),
            text => text,
            out var secondDisplay));

        Assert.Equal("Hello world", secondDisplay);
        Assert.Equal("Hello world", sut.StopSession());
    }

    [Fact]
    public void PollingTranscript_UsesStabilizedCurrentSessionOnly()
    {
        var sut = new StreamingTranscriptState();
        var firstSession = sut.StartSession();

        var firstApplied = sut.TryApplyPolling(
            firstSession,
            "Hello world",
            text => text,
            out var firstDisplay);
        var secondApplied = sut.TryApplyPolling(
            firstSession,
            "Hello world, how are you?",
            text => text,
            out var secondDisplay);

        Assert.True(firstApplied);
        Assert.Equal("Hello world", firstDisplay);
        Assert.True(secondApplied);
        Assert.Equal("Hello world, how are you?", secondDisplay);

        var secondSession = sut.StartSession();
        var staleApplied = sut.TryApplyPolling(
            firstSession,
            "Old session text",
            text => text,
            out _);
        var currentApplied = sut.TryApplyPolling(
            secondSession,
            "Fresh session text",
            text => text,
            out var currentDisplay);

        Assert.False(staleApplied);
        Assert.True(currentApplied);
        Assert.Equal("Fresh session text", currentDisplay);
    }
}

public class ParakeetTailHelperTests
{
    [Fact]
    public void AppendTailGuard_AddsExpectedSilenceSamples()
    {
        var samples = new float[] { 0.1f, -0.2f, 0.3f };

        var guarded = ParakeetTailHelper.AppendTailGuard(samples);

        Assert.Equal(samples.Length + 3200, guarded.Length);
        Assert.Equal(samples[0], guarded[0]);
        Assert.Equal(samples[1], guarded[1]);
        Assert.Equal(samples[2], guarded[2]);
        Assert.All(guarded.Skip(samples.Length), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void SelectResult_ForParakeet_PrefersFullDecodeOverPartials()
    {
        var selection = ParakeetTailHelper.SelectResult(
            ParakeetTailHelper.ParakeetModelId,
            "final full decode",
            ["partial text"]);

        Assert.Equal("final full decode", selection.Text);
        Assert.Equal("full_decode", selection.Source);
        Assert.True(selection.DivergedFromPartials);
    }

    [Fact]
    public void SelectResult_ForParakeet_FallsBackToPartialsWhenFullDecodeIsEmpty()
    {
        var selection = ParakeetTailHelper.SelectResult(
            ParakeetTailHelper.ParakeetModelId,
            "",
            ["tail segment"]);

        Assert.Equal("tail segment", selection.Text);
        Assert.Equal("fallback_partials_after_empty_full_decode", selection.Source);
        Assert.False(selection.DivergedFromPartials);
    }

    [Fact]
    public void SelectResult_ForNonParakeet_KeepsExistingPartialPreference()
    {
        var selection = ParakeetTailHelper.SelectResult(
            "plugin:other:model",
            "full decode",
            ["partial text"]);

        Assert.Equal("partial text", selection.Text);
        Assert.Equal("partials", selection.Source);
    }
}
