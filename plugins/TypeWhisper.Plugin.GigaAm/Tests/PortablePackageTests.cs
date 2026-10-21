using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeWhisper.Plugin.GigaAm;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using Xunit.Abstractions;

namespace TypeWhisper.Plugin.GigaAm.Tests;

public sealed class PortablePackageTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gigaam-package-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task StagedPackageLoadsThroughTheHostAndCarriesItsNotices()
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var manifest = PortablePluginPackage.ReadManifest(project);
        var package = Path.Combine(project, "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "portable-host", "Plugins", manifest.Id);

        Assert.True(File.Exists(Path.Combine(package, GigaAmPlugin.PreprocessorFileName)));
        Assert.True(File.Exists(Path.Combine(package, "THIRD-PARTY-NOTICES.md")));
        Assert.True(File.Exists(Path.Combine(package, "runtimes", "win-x64", "native", "onnxruntime.dll")));
        foreach (var license in new[] { "onnx-asr-LICENSE.txt", "GigaAM-LICENSE.txt", "ONNXRuntime-LICENSE.txt", "DotNet-LICENSE.txt" })
            Assert.Contains("Permission is hereby granted", File.ReadAllText(Path.Combine(package, "Licenses", license)));
        Assert.False(Directory.Exists(Path.Combine(package, "runtimes", "linux-x64")));

        var host = new VocabularyHostServices(Path.Combine(_root, "data"));
        await using (var loaded = await PortablePluginPackage.LoadAsync(package, host, new(1, 1, 6)))
        {
            Assert.Equal(manifest.Id, loaded.Plugin.PluginId);
            var engine = Assert.IsAssignableFrom<IPcmTranscriptionEnginePlugin>(loaded.Plugin);
            Assert.Equal(["ru"], engine.SupportedLanguages);
            Assert.False(engine.SupportsStreaming);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => PortablePluginPackage.LoadAsync(package, host, new(1, 1, 5)));
    }

    /// <summary>
    /// Opt-in check against the real model: set GIGAAM_MODEL_DIR to a folder with the four downloaded model files and
    /// GIGAAM_AUDIO to a 16 kHz mono 16-bit WAV file with Russian speech.
    /// </summary>
    [LiveModelFact]
    public async Task TranscribesRealAudioWithTheDownloadedModel()
    {
        var host = new VocabularyHostServices(Path.Combine(_root, "data"));
        var directory = Path.Combine(host.PluginAssetDirectory, "Models", GigaAmPlugin.ModelId);
        Directory.CreateDirectory(directory);
        // Hard links avoid copying the 214 MB encoder into the throwaway profile.
        foreach (var file in GigaAmModelAssets.DefaultFiles)
            Assert.True(CreateHardLink(Path.Combine(directory, file.Name), Path.Combine(Environment.GetEnvironmentVariable("GIGAAM_MODEL_DIR")!, file.Name), 0),
                "Keep the model folder on the temp volume.");
        using var plugin = new GigaAmPlugin();
        await plugin.ActivateAsync(host);
        Assert.True(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
        plugin.SelectModel(GigaAmPlugin.ModelId);
        await plugin.LoadModelAsync(GigaAmPlugin.ModelId, default);

        var timer = Stopwatch.StartNew();
        var result = await plugin.TranscribeAsync(await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("GIGAAM_AUDIO")!), "ru", false, null, default);

        output.WriteLine($"{result.DurationSeconds:0.0} s of audio in {timer.ElapsedMilliseconds} ms: {result.Text}");
        Assert.Matches(@"\p{IsCyrillic}", result.Text);
        await plugin.DeactivateAsync();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class LiveModelFactAttribute : FactAttribute
    {
        public LiveModelFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "The live model check runs the Windows ONNX Runtime package.";
            else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GIGAAM_MODEL_DIR"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GIGAAM_AUDIO")))
                Skip = "Set GIGAAM_MODEL_DIR and GIGAAM_AUDIO to run the real model.";
        }
    }
}
