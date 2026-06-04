using System.IO;
using System.Linq;
using Moq;
using TypeWhisper.Core;
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
    public async Task Recorder_UsesTranslationPipeline_WhenQuickTranslationTargetIsConfigured()
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
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            TranslationTargetLanguage = "de"
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
            ModelManagerService.GetPluginModelId(plugin.PluginId, "tiny"));

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
    public async Task Recorder_ShowsTranslationFailure_WhenQuickTranslationIsRequired()
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
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            TranslationTargetLanguage = "de"
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
            ModelManagerService.GetPluginModelId(plugin.PluginId, "tiny"));

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
        private readonly string _text;
        private readonly string? _detectedLanguage;

        public FakeRecorderTranscriptionPlugin(string text, string? detectedLanguage)
        {
            _text = text;
            _detectedLanguage = detectedLanguage;
        }

        public string PluginId => "com.typewhisper.recorder-test";
        public string PluginName => "Recorder Test";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "recorder-test";
        public string ProviderDisplayName => "Recorder Test";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = [new("tiny", "Tiny")];
        public string? SelectedModelId { get; private set; } = "tiny";
        public bool SupportsTranslation => false;

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
            return Task.FromResult(new PluginTranscriptionResult(_text, _detectedLanguage, 1.0, null));
        }
    }
}
