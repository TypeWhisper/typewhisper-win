using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit.Abstractions;

namespace TypeWhisper.Plugin.VoxtralLocal.Tests;

public sealed class PortablePackageTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "voxtral-local-package-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task StagedPackageLoadsThroughTheHostWithoutNativeFiles()
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var manifest = PortablePluginPackage.ReadManifest(project);
        var package = Path.Combine(project, "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "portable-host", "Plugins", manifest.Id);

        Assert.True(File.Exists(Path.Combine(package, "THIRD-PARTY-NOTICES.md")));
        // transcribe.cpp and the model are downloaded on first use, never shipped.
        Assert.Empty(Directory.EnumerateFiles(package, "transcribe*.dll", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(package, "ggml*.dll", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(package, "runtimes")));

        var host = new VocabularyHostServices(Path.Combine(_root, "data"));
        await using (var loaded = await PortablePluginPackage.LoadAsync(package, host, new(1, 1, 6)))
        {
            Assert.Equal(manifest.Id, loaded.Plugin.PluginId);
            var engine = Assert.IsAssignableFrom<IPcmTranscriptionEnginePlugin>(loaded.Plugin);
            Assert.Contains("de", engine.SupportedLanguages);
            Assert.False(engine.SupportsStreaming);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => PortablePluginPackage.LoadAsync(package, host, new(1, 1, 5)));
    }

    /// <summary>
    /// Opt-in check against the real runtime and model: set VOXTRAL_LOCAL_MODEL to the downloaded Q4_K_M GGUF,
    /// VOXTRAL_LOCAL_RUNTIME to transcribe-native-0.3.1-windows-x86_64-cpu-vulkan.tar.gz and VOXTRAL_LOCAL_WAV to a
    /// 16 kHz mono 16-bit WAV file with speech. VOXTRAL_LOCAL_DEVICE=cpu tests the processor instead of the graphics card.
    /// </summary>
    [LiveModelFact]
    public async Task TranscribesRealAudioWithTheDownloadedModel()
    {
        var host = new VocabularyHostServices(Path.Combine(_root, "data"));
        var model = VoxtralLocalModel.MiniQ4;
        var directory = Path.Combine(host.PluginAssetDirectory, "Models", model.Id);
        Directory.CreateDirectory(directory);
        // A hard link avoids copying 2.8 GB into the throwaway profile.
        Assert.True(CreateHardLink(Path.Combine(directory, model.FileName), Environment.GetEnvironmentVariable("VOXTRAL_LOCAL_MODEL")!, 0),
            "Keep the model on the temp volume.");
        var archive = Environment.GetEnvironmentVariable("VOXTRAL_LOCAL_RUNTIME")!;
        var runtime = TranscribeCppAsset.Runtime with { Url = "https://example.invalid/runtime.tar.gz" };
        Assert.Equal(runtime.Sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive))));
        var useCpu = Environment.GetEnvironmentVariable("VOXTRAL_LOCAL_DEVICE") == "cpu";

        using var plugin = new VoxtralLocalPlugin(new HttpClient(new ArchiveHandler(archive)), model, runtime);
        await plugin.ActivateAsync(host);
        await plugin.DownloadModelAsync(model.Id, null, default);
        Assert.True(plugin.IsModelDownloaded(model.Id));
        plugin.SelectModel(model.Id);
        plugin.SetAccelerationPreference(useCpu ? TranscriptionAccelerationPreference.Cpu : TranscriptionAccelerationPreference.Auto);
        var timer = Stopwatch.StartNew();
        await plugin.LoadModelAsync(model.Id, default);
        output.WriteLine($"Loaded in {timer.ElapsedMilliseconds} ms on {plugin.AccelerationStatus.DisplayText} ({plugin.AccelerationStatus.Detail})");

        var wav = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("VOXTRAL_LOCAL_WAV")!);
        PluginTranscriptionResult result = null!;
        // The first run after loading shows whether the graphics card still compiles its shaders then.
        foreach (var run in new[] { "first", "second" })
        {
            timer.Restart();
            result = await plugin.TranscribeAsync(wav, "de", false, null, default);
            output.WriteLine($"{run} run: {result.DurationSeconds:0.0} s of audio in {timer.ElapsedMilliseconds} ms: {result.Text}");
        }
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.Equal(useCpu ? TranscriptionAccelerationBackend.Cpu : TranscriptionAccelerationBackend.AmdVulkan, plugin.AccelerationStatus.ActiveBackend);
        output.WriteLine(plugin.TextSettings[0].Description);
        if (!useCpu)
        {
            // In the app process the backends stay registered, so switching to the processor must not need a restart.
            await plugin.SaveTextSettingAsync(VoxtralLocalPlugin.AccelerationSetting, "Cpu", default);
            timer.Restart();
            var onCpu = await plugin.TranscribeAsync(wav, "de", false, null, default);
            output.WriteLine($"after switching to the processor: {timer.ElapsedMilliseconds} ms: {onCpu.Text}");
            Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
            Assert.False(string.IsNullOrWhiteSpace(onCpu.Text));
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.TranscribePcmAsync(new float[16000], null, false, cancelled.Token));
        await plugin.DeactivateAsync();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class ArchiveHandler(string archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(archive)) });
    }

    private sealed class LiveModelFactAttribute : FactAttribute
    {
        public LiveModelFactAttribute()
        {
            if (!VoxtralLocalPlugin.RuntimePlatform) Skip = "transcribe.cpp ships its runtime for Windows x64 only.";
            else if (new[] { "VOXTRAL_LOCAL_MODEL", "VOXTRAL_LOCAL_RUNTIME", "VOXTRAL_LOCAL_WAV" }
                .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
                Skip = "Set VOXTRAL_LOCAL_MODEL, VOXTRAL_LOCAL_RUNTIME and VOXTRAL_LOCAL_WAV to run the real model.";
        }
    }
}
