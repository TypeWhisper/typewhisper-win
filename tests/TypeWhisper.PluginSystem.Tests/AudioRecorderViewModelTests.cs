using System.IO;
using System.Linq;
using Moq;
using TypeWhisper.Core;
using TypeWhisper.Core.Audio;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class AudioRecorderViewModelTests
{
    [Fact]
    public void RecorderTranslationMode_PersistsRecorderSettingsWithoutChangingGlobalTranslation()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";

        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            TranslationTargetLanguage = "de",
            LastTranslationTargetLanguage = "de",
            RecorderTranscriptionTask = "translate",
            RecorderTranslationTargetLanguage = "fr"
        });
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var modelManager = new ModelManagerService(pluginManager, settings);
        using var sut = new AudioRecorderViewModel(
            audio,
            modelManager,
            settings,
            new AudioFileService(),
            new FakeErrorLogService(),
            new PostProcessingPipeline(),
            Mock.Of<ITranslationService>());

        Assert.True(sut.TranslationModeEnabled);
        Assert.Equal("fr", sut.TranslationTargetLanguage);

        sut.TranslationModeEnabled = false;

        Assert.Equal("de", settings.Current.TranslationTargetLanguage);
        Assert.Equal("de", settings.Current.LastTranslationTargetLanguage);
        Assert.Equal("transcribe", settings.Current.RecorderTranscriptionTask);
        Assert.Null(settings.Current.RecorderTranslationTargetLanguage);
    }

    [Fact]
    public async Task Recorder_UsesTranslationPipeline_WhenRecorderTranslationTargetIsConfigured()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";
        TypeWhisperEnvironment.EnsureDirectories();

        var existingTranscripts = Directory
            .EnumerateFiles(TypeWhisperEnvironment.AudioPath, "recording-*.txt")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        var selectedModelId = ModelManagerService.GetPluginModelId(
            FakeRecorderTranscriptionPlugin.PluginIdValue,
            "tiny");
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            SelectedModelId = selectedModelId,
            RecorderTranscriptionTask = "translate",
            RecorderTranslationTargetLanguage = "de"
        });
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new FakeRecorderTranscriptionPlugin("hello there", "en");
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });
        var modelManager = new ModelManagerService(pluginManager, settings);
        TestPluginManagerFactory.SetPrivateField(
            modelManager,
            "_activeModelId",
            selectedModelId);

        var translation = new Mock<ITranslationService>();
        translation
            .Setup(t => t.TranslateAsync(
                "hello there",
                "en",
                "de",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("hallo da");

        var sut = new AudioRecorderViewModel(
            audio,
            modelManager,
            settings,
            new AudioFileService(),
            new FakeErrorLogService(),
            new PostProcessingPipeline(),
            translation.Object);

        sut.ToggleRecordingCommand.Execute(null);
        captures.Created.Single().RaiseData(BuildPcm16Chunk(), bytesRecorded: 3200);
        sut.ToggleRecordingCommand.Execute(null);

        var transcriptPath = await WaitForNewTranscriptFileAsync(existingTranscripts);
        try
        {
            var transcript = await File.ReadAllTextAsync(transcriptPath);

            Assert.Equal("hallo da", transcript);
            Assert.Contains(sut.Recordings, item => item.Transcript == "hallo da");
        }
        finally
        {
            TryDeleteRecordingFiles(transcriptPath);
        }
    }

    [Fact]
    public async Task Recorder_ShowsTranslationFailure_WhenRecorderTranslationIsRequired()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";
        TypeWhisperEnvironment.EnsureDirectories();

        var existingRecordings = Directory
            .EnumerateFiles(TypeWhisperEnvironment.AudioPath, "recording-*.wav")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        var selectedModelId = ModelManagerService.GetPluginModelId(
            FakeRecorderTranscriptionPlugin.PluginIdValue,
            "tiny");
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            SelectedModelId = selectedModelId,
            RecorderTranscriptionTask = "translate",
            RecorderTranslationTargetLanguage = "de"
        });
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new FakeRecorderTranscriptionPlugin("hello there", "en");
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });
        var modelManager = new ModelManagerService(pluginManager, settings);
        TestPluginManagerFactory.SetPrivateField(
            modelManager,
            "_activeModelId",
            selectedModelId);

        var translation = new Mock<ITranslationService>();
        translation
            .Setup(t => t.TranslateAsync(
                "hello there",
                "en",
                "de",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("translation exploded"));

        var sut = new AudioRecorderViewModel(
            audio,
            modelManager,
            settings,
            new AudioFileService(),
            new FakeErrorLogService(),
            new PostProcessingPipeline(),
            translation.Object);
        var existingRecorderItems = sut.Recordings.Count;

        sut.ToggleRecordingCommand.Execute(null);
        captures.Created.Single().RaiseData(BuildPcm16Chunk(), bytesRecorded: 3200);
        sut.ToggleRecordingCommand.Execute(null);

        await WaitUntilAsync(() => sut.Recordings.Count > existingRecorderItems);
        try
        {
            Assert.Contains("translation exploded", sut.StatusText);
            Assert.DoesNotContain(sut.Recordings, item => item.Transcript == "hello there");
        }
        finally
        {
            TryDeleteNewRecordings(existingRecordings);
        }
    }

    [Fact]
    public async Task Recorder_LogsFailureAndKeepsRecording_WhenProviderThrowsExternalComponentException()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";
        TypeWhisperEnvironment.EnsureDirectories();

        var existingRecordings = Directory
            .EnumerateFiles(TypeWhisperEnvironment.AudioPath, "recording-*.wav")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        var selectedModelId = ModelManagerService.GetPluginModelId(
            FakeRecorderTranscriptionPlugin.PluginIdValue,
            "tiny");
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            SelectedModelId = selectedModelId
        });
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new FakeRecorderTranscriptionPlugin(
            (_, _, _, _, _) => Task.FromException<PluginTranscriptionResult>(
                new InvalidOperationException("External component has thrown an exception.")));
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });
        var modelManager = new ModelManagerService(pluginManager, settings);
        TestPluginManagerFactory.SetPrivateField(modelManager, "_activeModelId", selectedModelId);

        var errorLog = new FakeErrorLogService();
        var sut = new AudioRecorderViewModel(
            audio,
            modelManager,
            settings,
            new AudioFileService(),
            errorLog,
            new PostProcessingPipeline(),
            Mock.Of<ITranslationService>());

        sut.ToggleRecordingCommand.Execute(null);
        captures.Created.Single().RaiseData(BuildPcm16Chunk(), bytesRecorded: 3200);
        sut.ToggleRecordingCommand.Execute(null);

        var item = await WaitForRecordingAsync(
            sut,
            recording => recording.ErrorMessage?.Contains(
                "External component has thrown an exception",
                StringComparison.Ordinal) == true
                && !recording.IsTranscribing);

        try
        {
            Assert.True(File.Exists(item.FilePath));
            Assert.Null(item.Transcript);
            Assert.False(File.Exists(Path.ChangeExtension(item.FilePath, ".txt")));
            var entry = Assert.Single(errorLog.Entries);
            Assert.Equal(ErrorCategory.Transcription, entry.Category);
            Assert.Contains("External component has thrown an exception", entry.Message);
        }
        finally
        {
            TryDeleteNewRecordings(existingRecordings);
        }
    }

    [Fact]
    public async Task Recorder_RetryTranscribesExistingFailedRecording_AndWritesTranscriptFile()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";
        TypeWhisperEnvironment.EnsureDirectories();

        var existingRecordings = Directory
            .EnumerateFiles(TypeWhisperEnvironment.AudioPath, "recording-*.wav")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        var selectedModelId = ModelManagerService.GetPluginModelId(
            FakeRecorderTranscriptionPlugin.PluginIdValue,
            "tiny");
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            SelectedModelId = selectedModelId
        });
        var callCount = 0;
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new FakeRecorderTranscriptionPlugin((_, _, _, _, _) =>
        {
            callCount++;
            return callCount == 1
                ? Task.FromException<PluginTranscriptionResult>(
                    new InvalidOperationException("External component has thrown an exception."))
                : Task.FromResult(new PluginTranscriptionResult("retry transcript", "en", 1));
        });
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });
        var modelManager = new ModelManagerService(pluginManager, settings);
        TestPluginManagerFactory.SetPrivateField(modelManager, "_activeModelId", selectedModelId);

        var sut = new AudioRecorderViewModel(
            audio,
            modelManager,
            settings,
            new AudioFileService(),
            new FakeErrorLogService(),
            new PostProcessingPipeline(),
            Mock.Of<ITranslationService>());

        sut.ToggleRecordingCommand.Execute(null);
        captures.Created.Single().RaiseData(BuildPcm16Chunk(), bytesRecorded: 3200);
        sut.ToggleRecordingCommand.Execute(null);

        var failedItem = await WaitForRecordingAsync(
            sut,
            recording => recording.ErrorMessage is not null && !recording.IsTranscribing);

        sut.TranscribeRecordingCommand.Execute(failedItem);

        var succeededItem = await WaitForRecordingAsync(
            sut,
            recording => recording.FilePath == failedItem.FilePath
                && recording.Transcript == "retry transcript"
                && !recording.IsTranscribing);

        try
        {
            Assert.Null(succeededItem.ErrorMessage);
            var transcriptPath = Path.ChangeExtension(succeededItem.FilePath, ".txt");
            Assert.Equal("retry transcript", await File.ReadAllTextAsync(transcriptPath));
        }
        finally
        {
            TryDeleteNewRecordings(existingRecordings);
        }
    }

    [Fact]
    public async Task Recorder_DeleteIsDisabledWhileRecordingIsTranscribing()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";
        TypeWhisperEnvironment.EnsureDirectories();

        var existingRecordings = Directory
            .EnumerateFiles(TypeWhisperEnvironment.AudioPath, "recording-*.wav")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        var selectedModelId = ModelManagerService.GetPluginModelId(
            FakeRecorderTranscriptionPlugin.PluginIdValue,
            "tiny");
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            SelectedModelId = selectedModelId
        });
        var pendingTranscription = new TaskCompletionSource<PluginTranscriptionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var plugin = new FakeRecorderTranscriptionPlugin((_, _, _, _, _) => pendingTranscription.Task);
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });
        var modelManager = new ModelManagerService(pluginManager, settings);
        TestPluginManagerFactory.SetPrivateField(modelManager, "_activeModelId", selectedModelId);

        var sut = new AudioRecorderViewModel(
            audio,
            modelManager,
            settings,
            new AudioFileService(),
            new FakeErrorLogService(),
            new PostProcessingPipeline(),
            Mock.Of<ITranslationService>());

        sut.ToggleRecordingCommand.Execute(null);
        captures.Created.Single().RaiseData(BuildPcm16Chunk(), bytesRecorded: 3200);
        sut.ToggleRecordingCommand.Execute(null);

        var transcribingItem = await WaitForRecordingAsync(
            sut,
            recording => recording.IsTranscribing);

        try
        {
            Assert.False(sut.DeleteRecordingCommand.CanExecute(transcribingItem));
            sut.DeleteRecordingCommand.Execute(transcribingItem);
            Assert.Contains(sut.Recordings, recording => recording.FilePath == transcribingItem.FilePath);
            Assert.True(File.Exists(transcribingItem.FilePath));

            pendingTranscription.SetResult(new PluginTranscriptionResult("done", "en", 1));
            await WaitForRecordingAsync(
                sut,
                recording => recording.FilePath == transcribingItem.FilePath
                    && recording.Transcript == "done"
                    && !recording.IsTranscribing);
        }
        finally
        {
            if (!pendingTranscription.Task.IsCompleted)
                pendingTranscription.SetResult(new PluginTranscriptionResult("done", "en", 1));
            TryDeleteNewRecordings(existingRecordings);
        }
    }

    [Fact]
    public async Task Recorder_RetryUsesConfiguredLanguageTaskAndRecorderTranslation()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";
        TypeWhisperEnvironment.EnsureDirectories();

        var existingRecordings = Directory
            .EnumerateFiles(TypeWhisperEnvironment.AudioPath, "recording-*.wav")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fileName = $"recording-{DateTime.Now:yyyy-MM-dd-HHmmssfff}.wav";
        var safeFileName = Path.GetFileName(fileName);
        var filePath = Path.Join(TypeWhisperEnvironment.AudioPath, safeFileName);
        await File.WriteAllBytesAsync(filePath, WavEncoder.Encode(BuildSamples()));

        try
        {
            var devices = new FakeAudioInputDeviceProvider("USB Microphone");
            var captures = new FakeAudioInputCaptureFactory();
            using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
            var selectedModelId = ModelManagerService.GetPluginModelId(
                FakeRecorderTranscriptionPlugin.PluginIdValue,
                "tiny");
            var settings = new FakeSettingsService(AppSettings.Default with
            {
                SelectedModelId = selectedModelId,
                Language = "de",
                TranslationTargetLanguage = "es",
                RecorderTranscriptionTask = "translate",
                RecorderTranslationTargetLanguage = "fr"
            });
            using var pluginManager = TestPluginManagerFactory.Create(settings);
            var plugin = new FakeRecorderTranscriptionPlugin((_, _, _, _, _) =>
                Task.FromResult(new PluginTranscriptionResult("hello there", "de", 1)));
            TestPluginManagerFactory.SetPrivateField(
                pluginManager,
                "_transcriptionEngines",
                new List<ITranscriptionEnginePlugin> { plugin });
            var modelManager = new ModelManagerService(pluginManager, settings);
            TestPluginManagerFactory.SetPrivateField(modelManager, "_activeModelId", selectedModelId);

            var translation = new Mock<ITranslationService>();
            translation
                .Setup(t => t.TranslateAsync(
                    "hello there",
                    "de",
                    "fr",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("bonjour");

            var sut = new AudioRecorderViewModel(
                audio,
                modelManager,
                settings,
                new AudioFileService(),
                new FakeErrorLogService(),
                new PostProcessingPipeline(),
                translation.Object);
            var item = Assert.Single(sut.Recordings, recording => recording.FilePath == filePath);

            sut.TranscribeRecordingCommand.Execute(item);

            var succeededItem = await WaitForRecordingAsync(
                sut,
                recording => recording.FilePath == filePath
                    && recording.Transcript == "bonjour"
                    && !recording.IsTranscribing);

            Assert.Null(succeededItem.ErrorMessage);
            var call = Assert.Single(plugin.Calls);
            Assert.Equal("de", call.Language);
            Assert.True(call.Translate);
            translation.Verify(t => t.TranslateAsync(
                "hello there",
                "de",
                "fr",
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            TryDeleteNewRecordings(existingRecordings);
        }
    }

    [Fact]
    public void Recorder_IgnoresUnrelatedSettingsChangesWithoutRefreshingSystemAudioDevices()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";

        var devices = new FakeAudioInputDeviceProvider("USB Microphone");
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(devices, captures, Timeout.InfiniteTimeSpan);
        var systemAudioFactory = new FakeSystemAudioLoopbackCaptureFactory(
            [new SystemAudioOutputDevice("wave-link-monitor", "Wave Link Monitor")]);
        using var capture = new RecorderCaptureService(
            audio,
            new SystemAudioCaptureService(systemAudioFactory));
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            RecorderSystemAudioDeviceId = "wave-link-monitor"
        });
        using var pluginManager = TestPluginManagerFactory.Create(settings);
        var modelManager = new ModelManagerService(pluginManager, settings);
        using var sut = new AudioRecorderViewModel(
            capture,
            modelManager,
            settings,
            new AudioFileService(),
            new FakeErrorLogService(),
            new PostProcessingPipeline(),
            Mock.Of<ITranslationService>());

        Assert.Equal(1, systemAudioFactory.AvailableDeviceRequestCount);

        settings.Save(settings.Current with
        {
            LiveTranscriptionEnabled = !settings.Current.LiveTranscriptionEnabled
        });

        Assert.Equal(1, systemAudioFactory.AvailableDeviceRequestCount);
    }

    private static byte[] BuildPcm16Chunk()
    {
        var buffer = new byte[3200];
        for (var i = 0; i < buffer.Length; i += 2)
        {
            var sample = (short)12000;
            var bytes = BitConverter.GetBytes(sample);
            buffer[i] = bytes[0];
            buffer[i + 1] = bytes[1];
        }

        return buffer;
    }

    private static float[] BuildSamples() =>
        Enumerable.Repeat(0.25f, 1600).ToArray();

    private static async Task<string> WaitForNewTranscriptFileAsync(ISet<string> existingTranscripts)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var transcripts = Directory
                .EnumerateFiles(TypeWhisperEnvironment.AudioPath, "recording-*.txt")
                .Select(path => new FileInfo(path))
                .Where(file => !existingTranscripts.Contains(file.FullName))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            var transcript = transcripts.FirstOrDefault();
            if (transcript is not null)
                return transcript.FullName;

            await Task.Delay(50);
        }

        throw new TimeoutException("Recorder did not save a transcript file.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(50);
        }

        throw new TimeoutException("Condition was not met.");
    }

    private static async Task<RecordingItem> WaitForRecordingAsync(
        AudioRecorderViewModel sut,
        Func<RecordingItem, bool> predicate)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            RecordingItem? item;
            try
            {
                item = sut.Recordings.FirstOrDefault(predicate);
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(50);
                continue;
            }
            if (item is not null)
                return item;

            await Task.Delay(50);
        }

        throw new TimeoutException("Recorder item did not reach the expected state.");
    }

    private static void TryDeleteRecordingFiles(string transcriptPath)
    {
        try
        {
            File.Delete(transcriptPath);
            var wavPath = Path.ChangeExtension(transcriptPath, ".wav");
            if (wavPath is not null)
                File.Delete(wavPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Audio recorder test cleanup failed for '{transcriptPath}': {ex}");
        }
    }

    private static void TryDeleteNewRecordings(ISet<string> existingRecordings)
    {
        try
        {
            foreach (var wavPath in Directory
                .EnumerateFiles(TypeWhisperEnvironment.AudioPath, "recording-*.wav")
                .Where(wavPath => !existingRecordings.Contains(wavPath)))
            {
                File.Delete(wavPath);
                var txtPath = Path.ChangeExtension(wavPath, ".txt");
                if (!string.IsNullOrEmpty(txtPath))
                    File.Delete(txtPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Audio recorder test cleanup failed for new recordings in '{TypeWhisperEnvironment.AudioPath}': {ex}");
        }
    }

    private sealed class FakeRecorderTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        public const string PluginIdValue = "com.typewhisper.recorder-test";

        private readonly Func<byte[], string?, bool, string?, CancellationToken, Task<PluginTranscriptionResult>>
            _transcribeAsync;

        public FakeRecorderTranscriptionPlugin(string text, string? detectedLanguage)
            : this((_, _, _, _, _) =>
                Task.FromResult(new PluginTranscriptionResult(text, detectedLanguage, 1.0, null)))
        {
        }

        public FakeRecorderTranscriptionPlugin(
            Func<byte[], string?, bool, string?, CancellationToken, Task<PluginTranscriptionResult>> transcribeAsync)
        {
            _transcribeAsync = transcribeAsync;
        }

        public List<RecorderTranscriptionCall> Calls { get; } = [];

        public string PluginId => PluginIdValue;
        public string PluginName => "Recorder Test";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "recorder-test";
        public string ProviderDisplayName => "Recorder Test";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = [new("tiny", "Tiny")];
        public string? SelectedModelId { get; private set; } = "tiny";
        public bool SupportsTranslation => true;

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
            Calls.Add(new RecorderTranscriptionCall(language, translate, prompt));
            return _transcribeAsync(wavAudio, language, translate, prompt, ct);
        }
    }

    private sealed class FakeErrorLogService : IErrorLogService
    {
        private readonly List<ErrorLogEntry> _entries = [];

        public IReadOnlyList<ErrorLogEntry> Entries => _entries;

        public event Action? EntriesChanged;

        public void AddEntry(string message, string category = "general")
        {
            _entries.Add(ErrorLogEntry.Create(message, category));
            EntriesChanged?.Invoke();
        }

        public void ClearAll() => _entries.Clear();

        public string ExportDiagnostics() => "";
    }

    private sealed record RecorderTranscriptionCall(
        string? Language,
        bool Translate,
        string? Prompt);
}
