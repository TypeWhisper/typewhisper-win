using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;
using Xunit.Abstractions;

public sealed class PortableParakeetTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("parakeet-tdt-0.6b")]
    [InlineData("canary-180m-flash")]
    [Trait("Category", "LocalParakeet")]
    public async Task PublishedPluginTranscribesPcmAndProvidesModelMetadata(string modelId)
    {
        var packageDirectory = Environment.GetEnvironmentVariable("TYPEWHISPER_TEST_PARAKEET_PACKAGE");
        var modelDirectory = Environment.GetEnvironmentVariable("TYPEWHISPER_TEST_PARAKEET_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(packageDirectory), "Set TYPEWHISPER_TEST_PARAKEET_PACKAGE to a published portable sherpa-onnx plugin.");
        Assert.False(string.IsNullOrWhiteSpace(modelDirectory), "Set TYPEWHISPER_TEST_PARAKEET_MODEL to an existing Parakeet model directory.");
        var data = Path.Join(Path.GetTempPath(), "typewhisper-portable-parakeet-" + Guid.NewGuid());
        try
        {
            var assets = Directory.GetParent(modelDirectory!)!.Parent!.FullName;
            var host = new VocabularyHostServices(data, assetDirectory: assets);
            Assert.False(((IPluginHostServices)host).AllowLegacyDataMigration);
            await using var package = await PortablePluginPackage.LoadAsync(packageDirectory!, host, new(1, 1, 0));
            var engine = Assert.IsAssignableFrom<IPcmTranscriptionEnginePlugin>(package.Plugin);
            Assert.Equal("com.typewhisper.sherpa-onnx", engine.PluginId);
            Assert.DoesNotContain(engine.GetType().Assembly.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "PresentationCore");
            engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
            var metadata = Assert.Single(engine.TranscriptionModels, model => model.Id == modelId);
            Assert.Equal(metadata.LanguageCount, metadata.LanguageCodes.Count);
            Assert.Contains("de", metadata.LanguageCodes);
            await engine.LoadModelAsync(modelId, CancellationToken.None);
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(synth.GetInstalledVoices().First(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en").VoiceInfo.Name);
            using var audio = new MemoryStream();
            synth.SetOutputToAudioStream(audio, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak("Bananas are yellow. This is a test of local transcription.");
            var bytes = audio.ToArray();
            var samples = Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2) / 32768f).ToArray();
            var result = await engine.TranscribePcmAsync(samples, null, false, CancellationToken.None);
            output.WriteLine($"Plugin: {engine.PluginId}; result: {result.Text}; tokens: {result.TokenTimings.Count}");
            Assert.Contains("bananas", result.Text.ToLowerInvariant());
            Assert.Contains("local transcription", result.Text.ToLowerInvariant());
            if (modelId == "parakeet-tdt-0.6b") Assert.NotEmpty(result.TokenTimings);
            Assert.All(result.TokenTimings, timing =>
            {
                Assert.True(timing.EndSeconds > timing.StartSeconds);
                Assert.InRange(timing.StartSeconds, 0, samples.Length / 16000d);
                Assert.InRange(timing.EndSeconds, 0, samples.Length / 16000d);
            });
            await engine.UnloadModelAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.TranscribePcmAsync(samples, null, false, CancellationToken.None));
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    // Canary dropped most speech after roughly 30 seconds when decoded in one pass (#542).
    [Theory]
    [InlineData("parakeet-tdt-0.6b")]
    [InlineData("canary-180m-flash")]
    [Trait("Category", "LocalParakeet")]
    public async Task PublishedPluginKeepsEverySectionOfMinuteLongDictation(string modelId)
    {
        var packageDirectory = Environment.GetEnvironmentVariable("TYPEWHISPER_TEST_PARAKEET_PACKAGE");
        var modelDirectory = Environment.GetEnvironmentVariable("TYPEWHISPER_TEST_PARAKEET_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(packageDirectory), "Set TYPEWHISPER_TEST_PARAKEET_PACKAGE to a published portable sherpa-onnx plugin.");
        Assert.False(string.IsNullOrWhiteSpace(modelDirectory), "Set TYPEWHISPER_TEST_PARAKEET_MODEL to an existing Parakeet model directory.");
        var data = Path.Join(Path.GetTempPath(), "typewhisper-portable-parakeet-" + Guid.NewGuid());
        try
        {
            var assets = Directory.GetParent(modelDirectory!)!.Parent!.FullName;
            var host = new VocabularyHostServices(data, assetDirectory: assets);
            await using var package = await PortablePluginPackage.LoadAsync(packageDirectory!, host, new(1, 1, 0));
            var engine = Assert.IsAssignableFrom<IPcmTranscriptionEnginePlugin>(package.Plugin);
            engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
            await engine.LoadModelAsync(modelId, CancellationToken.None);
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(synth.GetInstalledVoices().First(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en").VoiceInfo.Name);
            using var audio = new MemoryStream();
            synth.SetOutputToAudioStream(audio, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            string[] sections =
            [
                "Bananas are yellow, and we bought a whole bunch of them at the market this morning.",
                "After breakfast we walked down to the river and watched the boats drift slowly past the old mill.",
                "My sister called in the afternoon to ask whether we still wanted to visit her in the mountains next month.",
                "The train leaves early on Saturday, so we should pack our bags on Friday evening before dinner.",
                "Please remember to water the tomatoes in the garden, because the weather has been very dry lately.",
                "Our neighbor offered to feed the cat while we are away, which is a great relief for everyone.",
                "In the evening we cooked a large pot of vegetable soup and invited a few friends to join us.",
                "Someone brought a guitar, and we sang old songs by the fireplace until almost midnight.",
                "The next morning it was raining heavily, so we stayed inside and played chess by the window.",
                "My brother won three games in a row and would not stop talking about it for the rest of the day.",
                "Finally, the elephant painted a purple lighthouse."
            ];
            synth.Speak(string.Join(" ", sections));
            var bytes = audio.ToArray();
            var samples = Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2) / 32768f).ToArray();
            Assert.True(samples.Length > 55 * 16000, $"Synthesized speech is only {samples.Length / 16000d:F1}s long.");
            var result = await engine.TranscribePcmAsync(samples, "en", false, CancellationToken.None);
            output.WriteLine($"{modelId}; {samples.Length / 16000d:F1}s; result: {result.Text}");
            var text = result.Text.ToLowerInvariant();
            foreach (var marker in new[] { "bananas", "river", "mountains", "saturday", "tomatoes", "cat", "soup", "fireplace", "chess", "brother", "elephant", "purple lighthouse" })
                Assert.Contains(marker, text);
            await engine.UnloadModelAsync();
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
    }
}
