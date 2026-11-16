using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using TypeWhisper.Plugin.Diarization.Nemotron;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Diarization.Nemotron.Tests;

public sealed class NemotronDiarizationPluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nemotron-diarization-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> _served = new(StringComparer.Ordinal);
    private int _requests;

    public void Dispose()
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        // Best-effort removal of the temporary folder: a file Windows still holds open must not fail the test.
        try { Directory.Delete(_root, true); }
        catch (IOException) { /* Left in the temporary folder. */ }
        catch (UnauthorizedAccessException) { /* Left in the temporary folder. */ }
    }

    private VocabularyHostServices Host() => new(Path.Combine(_root, "data"));

    // Small stand-ins for the pinned files, served by a fake transport and verified like the real ones.
    private IReadOnlyList<NemotronModelFile> Files(bool corrupt = false)
    {
        var files = new List<NemotronModelFile>();
        foreach (var name in new[] { "model.onnx", "model.onnx_data" })
        {
            var content = RandomNumberGenerator.GetBytes(4096);
            var url = "https://huggingface.co/test/" + name;
            _served[url] = corrupt && name == "model.onnx_data" ? [.. content.Reverse()] : content;
            files.Add(new(name, url, content.Length, Convert.ToHexStringLower(SHA256.HashData(content))));
        }

        return files;
    }

    private HttpClient Http() => new(new Transport(this));

    private sealed class Transport(NemotronDiarizationPluginTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner._requests);
            return Task.FromResult(owner._served.TryGetValue(request.RequestUri!.ToString(), out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }
    }

    private sealed class ScriptedStep(float[] logitsPerFrame) : INemotronStep
    {
        public NemotronStepOutput Run(float[] features, int melFrames, float[] cached, int cachedFrames, long[] mask, CancellationToken cancellationToken)
        {
            var step = (melFrames + 7) / 8;
            var logits = new float[(cachedFrames + step) * 8 * 8];
            for (var row = 0; row < logits.Length / 8; row++) logitsPerFrame.CopyTo(logits, row * 8);
            return new(logits, new float[step * 512], new float[512]);
        }
    }

    // Fails its run, or cancels the call first when given its cancellation source.
    private sealed class FailingStep(CancellationTokenSource? cancel = null) : INemotronStep
    {
        public NemotronStepOutput Run(float[] features, int melFrames, float[] cached, int cachedFrames, long[] mask, CancellationToken cancellationToken)
        {
            cancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The device was removed.");
        }
    }

    private static PluginDiarizationRequest Seconds(double seconds, int rate = 16000) => new(new float[(int)(seconds * rate)], rate);

    [Fact]
    public async Task ModelsAreDownloadedVerifiedAndRemoved()
    {
        using var plugin = new NemotronDiarizationPlugin(Http(), files: Files());
        var host = Host();
        await plugin.ActivateAsync(host);
        var changes = 0;
        host.CapabilitiesChanged += () => changes++;
        Assert.False(plugin.AreDiarizationModelsInstalled);
        Assert.Equal("~380 MB", plugin.DiarizationModel.SizeDescription);
        Assert.Null(((IAudioDiarizationProvider)plugin).SupportedSpeakerCounts);

        var reports = new List<double>();
        await plugin.PrepareDiarizationModelsAsync(new Recorder(reports), default);
        Assert.True(plugin.AreDiarizationModelsInstalled);
        Assert.Equal(1, reports[^1]);
        Assert.Equal(1, changes);
        var requests = _requests;

        // Installed models are not downloaded again.
        await plugin.PrepareDiarizationModelsAsync(null, default);
        Assert.Equal(requests, _requests);

        await plugin.DeleteDiarizationModelsAsync(default);
        Assert.False(plugin.AreDiarizationModelsInstalled);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "data"), "model.onnx*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CorruptDownloadIsNotInstalled()
    {
        using var plugin = new NemotronDiarizationPlugin(Http(), files: Files(corrupt: true));
        await plugin.ActivateAsync(Host());

        await Assert.ThrowsAsync<IOException>(() => plugin.PrepareDiarizationModelsAsync(null, default));

        Assert.False(plugin.AreDiarizationModelsInstalled);
    }

    [Fact]
    public async Task RequestsTheModelCannotServeAreClassified()
    {
        using var plugin = new NemotronDiarizationPlugin(Http(), files: Files());
        await plugin.ActivateAsync(Host());

        var missing = await Assert.ThrowsAsync<PluginDiarizationException>(() => plugin.DiarizeAsync(Seconds(1), null, default));
        Assert.Equal(PluginDiarizationFailureKind.ModelsNotInstalled, missing.FailureKind);
        var rate = await Assert.ThrowsAsync<PluginDiarizationException>(() => plugin.DiarizeAsync(Seconds(1, 8000), null, default));
        Assert.Equal(PluginDiarizationFailureKind.UnsupportedAudio, rate.FailureKind);
        var count = await Assert.ThrowsAsync<PluginDiarizationException>(() => plugin.DiarizeAsync(Seconds(1) with { SpeakerCount = 2 }, null, default));
        Assert.Equal(PluginDiarizationFailureKind.UnsupportedSpeakerCount, count.FailureKind);
    }

    [Fact]
    public async Task DiarizationUsesTheSavedProfileAndDeviceAndReloadsWhenTheyChange()
    {
        var opened = new List<(string Profile, string? Adapter)>();
        var adapters = new[] { new DirectMlAdapter(1, "Fixture GPU", 8192) };
        // Speaker 3 is active everywhere, the others nowhere.
        var logits = Enumerable.Range(0, 8).Select(speaker => speaker == 3 ? 6f : -6f).ToArray();
        using var plugin = new NemotronDiarizationPlugin(Http(), adapters: () => adapters, files: Files(),
            openStep: (_, profile, adapter) => { opened.Add((profile.Id, adapter?.Name)); return new ScriptedStep(logits); });
        await plugin.ActivateAsync(Host());
        await plugin.PrepareDiarizationModelsAsync(null, default);

        var result = await plugin.DiarizeAsync(Seconds(3), null, default);
        var turn = Assert.Single(result.Turns);
        Assert.Equal(("S1", 0.0), (turn.SpeakerLabel, turn.Start));
        Assert.InRange(turn.End, 2.99, 3.01);
        Assert.Equal("nemotron3-onnx", result.Engine);
        Assert.EndsWith("/fast32", result.ModelVersion);

        await plugin.DiarizeAsync(Seconds(1), null, default);
        await plugin.SaveTextSettingAsync("profile", "offline", default);
        await plugin.SaveTextSettingAsync("device", "directml:1:Fixture GPU", default);
        await plugin.DiarizeAsync(Seconds(1), null, default);

        Assert.Equal([("fast32", null), ("offline", "Fixture GPU")], opened);
    }

    [Fact]
    public async Task AFailedRunReleasesTheModelAndACancelledRunKeepsIt()
    {
        using var cancellation = new CancellationTokenSource();
        var steps = new Queue<INemotronStep>([new FailingStep(), new FailingStep(cancellation)]);
        var opened = 0;
        using var plugin = new NemotronDiarizationPlugin(Http(), adapters: () => [], files: Files(),
            openStep: (_, _, _) => { opened++; return steps.Dequeue(); });
        await plugin.ActivateAsync(Host());
        await plugin.PrepareDiarizationModelsAsync(null, default);

        // The next call opens the model again instead of reusing the session that failed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.DiarizeAsync(Seconds(1), null, default));
        Assert.Null(plugin.LoadedDevice);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.DiarizeAsync(Seconds(1), null, cancellation.Token));
        Assert.NotNull(plugin.LoadedDevice);
        Assert.Equal(2, opened);
    }

    [Fact]
    public async Task DeviceChoiceAppearsOnlyWithAGraphicsCardAndRejectsUnknownValues()
    {
        IReadOnlyList<DirectMlAdapter> adapters = [];
        using var plugin = new NemotronDiarizationPlugin(Http(), adapters: () => adapters, files: Files());
        var host = Host();
        await plugin.ActivateAsync(host);

        var profile = Assert.Single(plugin.TextSettings);
        Assert.Equal(("profile", "fast32"), (profile.Id, profile.Value));
        Assert.Equal(["fast32", "offline"], profile.Choices.Select(choice => choice.Value));

        adapters = [new(0, "Fixture GPU", 4096)];
        var device = plugin.TextSettings.Single(setting => setting.Id == "device");
        Assert.Equal("cpu", device.Value);
        Assert.Equal(["cpu", "directml:0:Fixture GPU"], device.Choices.Select(choice => choice.Value));

        await plugin.SaveTextSettingAsync("device", "directml:0:Fixture GPU", default);
        Assert.Equal("Fixture GPU", plugin.SelectedAdapter?.Name);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("device", "directml:0:Other GPU", default));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("device", "directml:Fixture GPU", default));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("profile", "turbo", default));

        // A saved graphics card that is gone falls back to the CPU.
        adapters = [];
        Assert.Null(plugin.SelectedAdapter);
    }

    [Fact]
    public async Task GraphicsCardsWithTheSameDescriptionAreChosenApart()
    {
        IReadOnlyList<DirectMlAdapter> adapters = [new(0, "Fixture GPU", 4096), new(2, "Fixture GPU", 4096)];
        using var plugin = new NemotronDiarizationPlugin(Http(), adapters: () => adapters, files: Files());
        var host = Host();
        await plugin.ActivateAsync(host);

        var device = plugin.TextSettings.Single(setting => setting.Id == "device");
        Assert.Equal(["cpu", "directml:0:Fixture GPU", "directml:2:Fixture GPU"], device.Choices.Select(choice => choice.Value));
        Assert.Equal(["CPU", "Fixture GPU #1 (DirectML)", "Fixture GPU #3 (DirectML)"], device.Choices.Select(choice => choice.Title));

        await plugin.SaveTextSettingAsync("device", "directml:2:Fixture GPU", default);
        Assert.Equal(2, plugin.SelectedAdapter?.Index);
        Assert.Equal("directml:2:Fixture GPU", plugin.TextSettings.Single(setting => setting.Id == "device").Value);

        // A card whose index moved is found by its description, like a value that names the description only.
        adapters = [new(1, "Fixture GPU", 4096)];
        Assert.Equal(1, plugin.SelectedAdapter?.Index);
        host.SetSetting("device", "directml:Fixture GPU");
        Assert.Equal(1, plugin.SelectedAdapter?.Index);
        Assert.Equal("directml:1:Fixture GPU", plugin.TextSettings.Single(setting => setting.Id == "device").Value);
    }

    [Fact]
    public async Task PackageInstallsAndOffersALocalDiarizationProvider()
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var manifest = PortablePluginPackage.ReadManifest(project);
        var source = Path.Combine(project, "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "portable-host", "Plugins", manifest.Id);
        Assert.Equal(("1.0.0", "1.3.0", "local"), (manifest.Version, manifest.MinHostVersion, manifest.Locality));
        Assert.Equal(["diarization"], manifest.Categories);
        foreach (var license in new[] { "ONNXRuntime-LICENSE.txt", "DirectML-LICENSE.txt", "OpenMDW-1.1.txt" })
            Assert.True(File.Exists(Path.Combine(source, "Licenses", license)), license);
        foreach (var architecture in new[] { "win-x64", "win-arm64" })
            foreach (var library in new[] { "onnxruntime.dll", "DirectML.dll" })
                Assert.True(File.Exists(Path.Combine(source, "runtimes", architecture, "native", library)), architecture + "/" + library);

        var archive = Path.Combine(_root, "package.zip");
        Directory.CreateDirectory(_root);
        ZipFile.CreateFromDirectory(source, archive, CompressionLevel.Fastest, false);
        _served["https://github.com/TypeWhisper/typewhisper-win/releases/download/plugin-fixture-v1.0.0/package.zip"] = File.ReadAllBytes(archive);
        var host = Host();
        var store = new PortablePluginStore(Path.Combine(_root, "store"), new(1, 3, 0), Http(), _ => host);
        await store.InitializeAsync();
        await store.InstallAsync(new PortableCatalogEntry
        {
            Id = manifest.Id, Name = manifest.Name, Version = manifest.Version, MinHostVersion = manifest.MinHostVersion!,
            DownloadUrl = "https://github.com/TypeWhisper/typewhisper-win/releases/download/plugin-fixture-v1.0.0/package.zip",
            Size = new FileInfo(archive).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))),
            SupportedArchitectures = [PortablePluginCatalog.Architecture]
        });
        await using var runtime = new PortablePluginRuntimeRegistry(store, new(1, 3, 0), _ => host);
        await runtime.InitializeAsync();
        Assert.Null(await runtime.SetEnabledAsync(manifest.Id, true));

        var provider = Assert.Single(runtime.DiarizationProviders);
        Assert.Equal((manifest.Id, true, false), (provider.ProviderId, provider.IsLocal, provider.ModelsInstalled));
        await runtime.UseConfigurationAsync(manifest.Id, (plugin, _) =>
        {
            Assert.Equal(manifest.Version, plugin.PluginVersion);
            Assert.NotEmpty(((IPluginTextSettings)plugin).TextSettings);
            return Task.FromResult(true);
        });
        await Assert.ThrowsAsync<PluginDiarizationException>(() => runtime.DiarizeAsync(manifest.Id, Seconds(1)));
        Assert.Null(await runtime.SetEnabledAsync(manifest.Id, false));
    }

    [NemotronModelFact]
    public Task PinnedModelDiarizesARecordingOnTheCpu() => DiarizeWithThePinnedModelAsync(null);

    // Runs only on a PC with a DirectML graphics card besides the model; it must not fall back to the CPU.
    [NemotronModelFact]
    public async Task PinnedModelDiarizesARecordingWithDirectMl()
    {
        if (DxgiAdapters.List().FirstOrDefault() is not { } adapter) return;
        var device = await DiarizeWithThePinnedModelAsync(adapter);
        Assert.Equal(adapter.Name, device);
    }

    private async Task<string?> DiarizeWithThePinnedModelAsync(DirectMlAdapter? adapter)
    {
        var directory = NemotronModelFactAttribute.ModelDirectory!;
        var audioPath = Environment.GetEnvironmentVariable("TYPEWHISPER_DIARIZATION_AUDIO");
        var samples = audioPath is { Length: > 0 } && File.Exists(audioPath) ? Wav.ReadMono16k(audioPath) : TwoVoices(20);
        var files = NemotronModelAssets.DefaultFiles.Select(file => file with { DownloadUrl = "https://invalid.test/" + file.FileName }).ToArray();
        using var plugin = new NemotronDiarizationPlugin(Http(), adapters: () => adapter is null ? [] : [adapter], files: files);
        var host = Host();
        await plugin.ActivateAsync(host);
        if (adapter is not null) await plugin.SaveTextSettingAsync("device", $"directml:{adapter.Index}:{adapter.Name}", default);
        // The pinned files are copied into the plugin's model directory and verified there like a download.
        var target = Path.Combine(host.PluginAssetDirectory, "Models", "nemotron-3-diarization-" + NemotronModelAssets.Revision[..12]);
        Directory.CreateDirectory(target);
        foreach (var file in files) File.Copy(Path.Combine(directory, file.FileName), Path.Combine(target, file.FileName));
        await plugin.PrepareDiarizationModelsAsync(null, default);
        Assert.True(plugin.AreDiarizationModelsInstalled);

        var started = Stopwatch.GetTimestamp();
        var result = await plugin.DiarizeAsync(new(samples), null, default);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Console.WriteLine($"{result.Turns.Count} turns, {result.Turns.Select(turn => turn.SpeakerLabel).Distinct().Count()} speakers, "
            + $"{samples.Length / 16000.0:0.0} s audio in {elapsed.TotalSeconds:0.0} s on {plugin.LoadedDevice}");
        Assert.NotEmpty(result.Turns);
        Assert.Equal("S1", result.Turns[0].SpeakerLabel);
        Assert.All(result.Turns, turn => Assert.True(turn.End > turn.Start));
        return plugin.LoadedDevice;
    }

    // Two synthetic voices taking turns; enough for the model to report speech, not a quality measure.
    private static float[] TwoVoices(int seconds) => Enumerable.Range(0, seconds * 16000).Select(i =>
    {
        var low = (i / 32000) % 2 == 0;
        var f0 = low ? 110.0 : 220.0;
        var t = i / 16000.0;
        var voice = Enumerable.Range(1, 12).Sum(h => Math.Sin(2 * Math.PI * f0 * h * t) / h);
        return (float)(0.1 * voice * (0.6 + (0.4 * Math.Sin(2 * Math.PI * 4 * t))));
    }).ToArray();

    private sealed class Recorder(List<double> reports) : IProgress<double>
    {
        public void Report(double value) => reports.Add(value);
    }
}
