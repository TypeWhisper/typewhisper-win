using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TypeWhisper.Plugin.Qwen3Local;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using Xunit.Abstractions;

public sealed class LocalInferenceTests(ITestOutputHelper output)
{
    internal static string PackageDirectory => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
        "portable-host/Plugins/com.typewhisper.qwen3-local"));

    [Fact]
    public async Task PackageInstallsAndLoadsInPortableHostWithoutModelOrCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "qwen-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var rid in new[] { "win-x64", "win-arm64" })
            {
                Assert.True(File.Exists(Path.Combine(PackageDirectory, "runtimes", rid, "native", "qwenort.dll")));
                var native = File.ReadAllBytes(Path.Combine(PackageDirectory, "runtimes", rid, "native", "sherpa-onnx-c-api.dll"));
                Assert.True(native.AsSpan().IndexOf("qwenort.dll\0"u8) >= 0);
                Assert.True(native.AsSpan().IndexOf("onnxruntime.dll\0"u8) < 0);
            }
            Assert.False(File.Exists(Path.Combine(PackageDirectory, "TypeWhisper.PluginSDK.dll")));
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackageDirectory, "manifest.json")));
            Assert.True(json.RootElement.GetProperty("isLocal").GetBoolean());
            Assert.Equal("transcription", json.RootElement.GetProperty("categories")[0].GetString());
            var zip = Path.Combine(root, "plugin.zip");
            ZipFile.CreateFromDirectory(PackageDirectory, zip, CompressionLevel.Fastest, false);
            using var http = QwenTests.Http(() => new StreamContent(File.OpenRead(zip)));
            var host = new TestHost(Path.Combine(root, "assets"));
            var entry = new PortableCatalogEntry
            {
                Id = "com.typewhisper.qwen3-local", Name = "Qwen3 ASR (Local)", Version = "1.0.0", MinHostVersion = "1.1.2",
                DownloadUrl = "https://fixture.invalid/qwen.zip", Size = new FileInfo(zip).Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))),
                SupportedArchitectures = ["x64", "arm64"], Categories = ["transcription"]
            };
            var store = new PortablePluginStore(Path.Combine(root, "store"), new(1, 1, 2), http, _ => host);
            await store.InitializeAsync(); await store.InstallAsync(entry);
            await using (var package = await PortablePluginPackage.LoadAsync(store.Resolve(entry.Id), host, new(1, 1, 2)))
            {
                var engine = Assert.IsAssignableFrom<IPcmTranscriptionEnginePlugin>(package.Plugin);
                Assert.True(engine.SupportsModelDownload); Assert.True(engine.SupportsModelRemoval);
                Assert.False(engine.SupportsTranslation); Assert.False(engine.SupportsStreaming);
                Assert.False(engine.IsModelDownloaded(Qwen3LocalPlugin.ModelId));
                Assert.DoesNotContain(engine.GetType().Assembly.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "WindowsBase");
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.LoadModelAsync(Qwen3LocalPlugin.ModelId, default));
            }
            var restarted = new PortablePluginStore(store.Root, new(1, 1, 2), http, _ => host);
            await restarted.InitializeAsync(); Assert.True(restarted.IsInstalled(entry.Id));
            await restarted.UninstallAsync(entry.Id); Assert.False(restarted.IsInstalled(entry.Id));
        }
        finally
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            try { Directory.Delete(root, true); }
            catch (IOException ex) { output.WriteLine("Package cleanup: " + ex.Message); }
        }
    }

    [LocalModelFact]
    public async Task RealCpuInferenceGermanEnglishLongAudioCancellationAndReload()
    {
        var archive = Environment.GetEnvironmentVariable("QWEN_LOCAL_TEST_ARCHIVE")!;
        var assetRoot = Environment.GetEnvironmentVariable("QWEN_LOCAL_TEST_ASSETS")!;
        var wavRoot = Environment.GetEnvironmentVariable("QWEN_LOCAL_TEST_WAVS")!;
        // Exercise the production download, hash verification and extraction code against
        // the previously downloaded official archive. No audio leaves the process.
        using var http = QwenTests.Http(() => new StreamContent(File.OpenRead(archive)));
        var assets = new QwenModelAssets(http);
        var modelDirectory = Path.Combine(assetRoot, "Models", Qwen3LocalPlugin.ModelId);
        await assets.DownloadAsync(modelDirectory, null, default);
        var metrics = new List<object>();
        await using var package = await PortablePluginPackage.LoadAsync(PackageDirectory, new TestHost(assetRoot), new(1, 1, 2));
        var engine = (IPcmTranscriptionEnginePlugin)package.Plugin;
        engine.SelectModel(Qwen3LocalPlugin.ModelId);
        var timer = Stopwatch.StartNew(); await engine.LoadModelAsync(Qwen3LocalPlugin.ModelId, default);
        output.WriteLine($"CPU model load: {timer.Elapsed.TotalSeconds:F3}s");
        foreach (var (file, language, expected) in new[] { ("de.wav", "de", "Bergbau"), ("f1_noise.wav", "en", "Charles") })
        {
            var wav = await File.ReadAllBytesAsync(Path.Combine(wavRoot, file));
            timer.Restart(); var result = await engine.TranscribeAsync(wav, language, false, null, default);
            Assert.Contains(expected, result.Text, StringComparison.OrdinalIgnoreCase);
            var seconds = timer.Elapsed.TotalSeconds;
            output.WriteLine($"{file}: audio={result.DurationSeconds:F3}s elapsed={seconds:F3}s RTF={seconds / result.DurationSeconds:F3} text={result.Text}");
            metrics.Add(new { file, result.Text, result.DurationSeconds, elapsedSeconds = seconds, rtf = seconds / result.DurationSeconds });
        }
        var german = QwenAudio.DecodeWav(await File.ReadAllBytesAsync(Path.Combine(wavRoot, "de.wav")));
        var repeated = Enumerable.Range(0, 5).SelectMany(_ => german.Concat(new float[8000])).ToArray();
        timer.Restart(); var longResult = await engine.TranscribePcmAsync(repeated, null, false, default);
        Assert.True(longResult.Segments.Count >= 2);
        Assert.Equal((double)repeated.Length / 16000, longResult.DurationSeconds);
        Assert.True(longResult.Text.Split("Bergbau", StringSplitOptions.None).Length >= 5, longResult.Text);
        output.WriteLine($"Long audio: {longResult.DurationSeconds:F3}s elapsed={timer.Elapsed.TotalSeconds:F3}s text={longResult.Text}");
        metrics.Add(new { file = "repeated-de", longResult.Text, longResult.DurationSeconds, elapsedSeconds = timer.Elapsed.TotalSeconds });
        var silence = await engine.TranscribePcmAsync(new float[16000], null, false, default); Assert.Empty(silence.Text);
        using var cts = new CancellationTokenSource(); cts.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TranscribePcmAsync(german, null, false, cts.Token));
        await engine.UnloadModelAsync(); await engine.LoadModelAsync(Qwen3LocalPlugin.ModelId, default);
        var reloaded = await engine.TranscribePcmAsync(german, "de", false, default);
        Assert.Contains("Bergbau", reloaded.Text);
        output.WriteLine($"Reload: {reloaded.Text}");
        output.WriteLine($"Peak process working set: {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MiB");
        await File.WriteAllTextAsync(Path.Combine(assetRoot, "qwen-local-validation.json"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class LocalModelFactAttribute : FactAttribute
{
    public LocalModelFactAttribute()
    {
        if (new[] { "QWEN_LOCAL_TEST_ARCHIVE", "QWEN_LOCAL_TEST_ASSETS", "QWEN_LOCAL_TEST_WAVS" }
            .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "Set the QWEN_LOCAL_TEST_ARCHIVE, QWEN_LOCAL_TEST_ASSETS and QWEN_LOCAL_TEST_WAVS paths to run real local inference.";
    }
}
