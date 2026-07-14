using System.IO;
using System.Reflection;
using Moq;
using TypeWhisper.Core.Audio;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class FileTranscriptionProcessorTests
{
    [Fact]
    public async Task ProcessAsync_AppliesNumberBoostingAndDictionaryInOrderAndPreservesMetadata()
    {
        using var harness = new Harness();
        var filePath = harness.WriteAudioFile(harness.TempPath, "clip.wav");

        var result = await harness.Processor.ProcessAsync(
            filePath,
            _ => { },
            new FileTranscriptionProcessOptions(Language: "en"),
            CancellationToken.None);

        Assert.Equal("23 TYPEWHISPER", result.ProcessedText);
        Assert.Equal(["vocabulary:23 type whisper", "dictionary:23 TypeWhisper"], harness.PostProcessingCalls);
        Assert.Equal("en", result.RawResult.DetectedLanguage);
        Assert.Equal(4.2, result.RawResult.Duration);
        var segment = Assert.Single(result.RawResult.Segments);
        Assert.Equal("23 type whisper", segment.Text);
        Assert.Equal(0.25, segment.Start);
        Assert.Equal(1.75, segment.End);
    }

    [Fact]
    public async Task WatchFolder_AppliesTheSamePostProcessingAndWritesProcessedText()
    {
        using var harness = new Harness();
        using var watchFolder = new WatchFolderService(harness.DataPath);
        harness.WriteAudioFile(harness.WatchPath, "clip.wav");
        var outputPath = Path.Combine(harness.OutputPath, "clip.txt");
        var sut = new FileTranscriptionViewModel(
            harness.Processor,
            harness.ModelManager,
            harness.Settings.Object,
            harness.AudioFile,
            harness.Dictionary.Object,
            harness.Vocabulary.Object,
            harness.Pipeline,
            watchFolder);

        sut.StartWatchFolderFromSettings();

        await WaitForAsync(() => watchFolder.History.Count == 1);
        var history = Assert.Single(watchFolder.History);
        Assert.True(history.Success, history.ErrorMessage);
        Assert.Equal("23 TYPEWHISPER", await File.ReadAllTextAsync(outputPath));
        Assert.Equal(["vocabulary:23 type whisper", "dictionary:23 TypeWhisper"], harness.PostProcessingCalls);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
                return;

            await Task.Delay(100);
        }

        Assert.True(condition());
    }

    private sealed class Harness : IDisposable
    {
        private const string ProviderId = "test-transcription";
        private const string ModelId = "mock";
        private readonly PluginManager _pluginManager;

        public Harness()
        {
            Loc.Instance.Initialize();
            Loc.Instance.CurrentLanguage = "en";

            TempPath = Path.Combine(Path.GetTempPath(), $"tw_file_processor_test_{Guid.NewGuid():N}");
            DataPath = Path.Combine(TempPath, "data");
            WatchPath = Path.Combine(TempPath, "watch");
            OutputPath = Path.Combine(TempPath, "output");
            Directory.CreateDirectory(DataPath);
            Directory.CreateDirectory(WatchPath);
            Directory.CreateDirectory(OutputPath);

            var settings = AppSettings.Default with
            {
                SelectedModelId = ModelManagerService.GetPluginModelId(ProviderId, ModelId),
                TranscriptionNumberNormalizationEnabled = true,
                VocabularyBoostingEnabled = true,
                ModelAutoUnloadSeconds = 0,
                WatchFolderPath = WatchPath,
                WatchFolderOutputPath = OutputPath,
                WatchFolderOutputFormat = "txt",
                WatchFolderLanguage = "en"
            };
            Settings.SetupGet(service => service.Current).Returns(settings);

            Vocabulary
                .Setup(service => service.Apply(It.IsAny<string>()))
                .Returns((string text) =>
                {
                    PostProcessingCalls.Add($"vocabulary:{text}");
                    return text.Replace("type whisper", "TypeWhisper", StringComparison.Ordinal);
                });
            Dictionary
                .Setup(service => service.ApplyCorrections(It.IsAny<string>()))
                .Returns((string text) =>
                {
                    PostProcessingCalls.Add($"dictionary:{text}");
                    return text.Replace("TypeWhisper", "TYPEWHISPER", StringComparison.Ordinal);
                });

            var selectedModelId = ModelId;
            var plugin = new Mock<ITranscriptionEnginePlugin>();
            plugin.SetupGet(value => value.PluginId).Returns(ProviderId);
            plugin.SetupGet(value => value.PluginName).Returns("Test transcription");
            plugin.SetupGet(value => value.PluginVersion).Returns("1.0.0");
            plugin.SetupGet(value => value.ProviderId).Returns(ProviderId);
            plugin.SetupGet(value => value.ProviderDisplayName).Returns("Test transcription");
            plugin.SetupGet(value => value.IsConfigured).Returns(true);
            plugin.SetupGet(value => value.TranscriptionModels).Returns([new PluginModelInfo(ModelId, "Mock")]);
            plugin.SetupGet(value => value.SelectedModelId).Returns(() => selectedModelId);
            plugin.Setup(value => value.SelectModel(It.IsAny<string>()))
                .Callback((string modelId) => selectedModelId = modelId);
            plugin
                .Setup(value => value.TranscribeWithLanguageHintsAsync(
                    It.IsAny<byte[]>(),
                    It.IsAny<IReadOnlyList<string>>(),
                    It.IsAny<bool>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginTranscriptionResult("twenty three type whisper", "en", 4.2)
                {
                    Segments = [new PluginTranscriptionSegment("twenty three type whisper", 0.25, 1.75)]
                });

            var workflows = new Mock<IWorkflowService>();
            workflows.Setup(service => service.Workflows).Returns([]);
            _pluginManager = new PluginManager(
                new PluginLoader(),
                new PluginEventBus(),
                Mock.Of<IActiveWindowService>(),
                workflows.Object,
                Settings.Object,
                []);
            typeof(PluginManager)
                .GetField("_transcriptionEngines", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_pluginManager, new List<ITranscriptionEnginePlugin> { plugin.Object });

            ModelManager = new ModelManagerService(_pluginManager, Settings.Object);
            Processor = new FileTranscriptionProcessor(
                ModelManager,
                Settings.Object,
                AudioFile,
                Dictionary.Object,
                Vocabulary.Object,
                Pipeline);
        }

        public string TempPath { get; }
        public string DataPath { get; }
        public string WatchPath { get; }
        public string OutputPath { get; }
        public Mock<ISettingsService> Settings { get; } = new();
        public Mock<IDictionaryService> Dictionary { get; } = new();
        public Mock<IVocabularyBoostingService> Vocabulary { get; } = new();
        public List<string> PostProcessingCalls { get; } = [];
        public AudioFileService AudioFile { get; } = new();
        public PostProcessingPipeline Pipeline { get; } = new();
        public ModelManagerService ModelManager { get; }
        public FileTranscriptionProcessor Processor { get; }

        public string WriteAudioFile(string directory, string fileName)
        {
            var path = Path.Combine(directory, fileName);
            File.WriteAllBytes(path, WavEncoder.Encode(Enumerable.Repeat(0.05f, 1600).ToArray()));
            return path;
        }

        public void Dispose()
        {
            ModelManager.Dispose();
            _pluginManager.Dispose();
            try { Directory.Delete(TempPath, recursive: true); } catch { }
        }
    }
}
