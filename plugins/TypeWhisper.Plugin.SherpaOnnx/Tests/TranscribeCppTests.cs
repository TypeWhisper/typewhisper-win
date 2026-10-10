using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Plugin.SherpaOnnx;
using TypeWhisper.PluginSDK.Models;

/// <summary>The GPU path: transcribe.cpp's bindings, its pinned downloads and how the plugin places a model.</summary>
public sealed class TranscribeCppTests : IDisposable
{
    private const string Root = "transcribe-native-windows-x86_64-cpu-vulkan";
    private const string ModelId = "test-model";
    private static readonly byte[] Gguf = Encoding.UTF8.GetBytes("GGUF test weights");
    private static readonly byte[] Encoder = Encoding.UTF8.GetBytes("encoder");
    private static readonly byte[] Tokens = Encoding.UTF8.GetBytes("tokens");
    // Built once: every archive carries its own timestamps and so its own hash.
    private static readonly byte[] RuntimeArchive = Archive();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "transcribecpp-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BindingsUseTheX64LayoutsOfTranscribeH()
    {
        // Offsets follow the C declarations in include/transcribe.h 0.3.1 on Windows x64.
        Assert.Equal(24, Marshal.SizeOf<TranscribeCppNative.ModelLoadParams>());
        Assert.Equal(24, Marshal.SizeOf<TranscribeCppNative.SessionParams>());
        Assert.Equal(104, Marshal.SizeOf<TranscribeCppNative.RunParams>());
        Assert.Equal(64, Marshal.SizeOf<TranscribeCppNative.DeviceInfo>());
        Assert.Equal(24, Marshal.SizeOf<TranscribeCppNative.BackendInitParams>());
        Assert.Equal(48, Marshal.SizeOf<TranscribeCppNative.Token>());
        Assert.Equal(16, Marshal.OffsetOf<TranscribeCppNative.Token>(nameof(TranscribeCppNative.Token.StartMilliseconds)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<TranscribeCppNative.Token>(nameof(TranscribeCppNative.Token.Text)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.KeepSpecialTags)).ToInt32());
        Assert.Equal(96, Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.Prefix)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<TranscribeCppNative.DeviceInfo>(nameof(TranscribeCppNative.DeviceInfo.DeviceType)).ToInt32());
    }

    [Fact]
    public async Task RuntimeDownloadKeepsOnlyLibrariesContractAndLicensesAndIsReusedOnceVerified()
    {
        var archive = Archive();
        var requests = 0;
        using var http = Http(() => { requests++; return new ByteArrayContent(archive); });
        var store = new TranscribeCppAssetStore(http, RuntimeAsset(archive));
        var directory = Path.Join(_root, "runtime");
        Assert.False(store.IsReady(directory));

        await store.DownloadAsync(directory, null, default);

        Assert.True(store.IsReady(directory));
        Assert.True(File.Exists(Path.Join(directory, "transcribe.dll")));
        Assert.True(File.Exists(Path.Join(directory, "contract.json")));
        Assert.True(File.Exists(Path.Join(directory, "licenses", "LICENSE")));
        Assert.False(File.Exists(Path.Join(directory, "transcribe-cli.exe")));
        Assert.False(Directory.Exists(Path.Join(directory, "include")));
        await store.DownloadAsync(directory, null, default);
        Assert.Equal(1, requests);

        File.WriteAllText(Path.Join(directory, "ggml.dll"), "");
        Assert.False(store.IsReady(directory));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("traversal")]
    [InlineData("link")]
    [InlineData("truncated")]
    [InlineData("missing-library")]
    public async Task InvalidRuntimeDownloadsNeverBecomeReady(string failure)
    {
        var archive = Archive(failure);
        var asset = RuntimeAsset(archive);
        if (failure == "checksum") asset = asset with { Sha256 = new string('0', 64) };
        using var http = Http(() => new ByteArrayContent(failure == "truncated" ? archive[..^1] : archive));
        var store = new TranscribeCppAssetStore(http, asset);
        var directory = Path.Join(_root, "runtime");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync(directory, null, default));

        Assert.False(store.IsReady(directory));
        Assert.False(Directory.Exists(directory));
        Assert.Empty(Directory.GetDirectories(_root, "runtime.download-*"));
    }

    [Fact]
    public void ContractOfAnotherReleaseIsRejectedBeforeTheLibraryLoads()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Join(_root, "contract.json"), """{"version":"0.3.0","header_hash":"0000000000000000"}""");
        var error = Assert.Throws<InvalidDataException>(() => TranscribeCppRuntime.ValidateContract(_root));
        Assert.Contains("0.3.1", error.Message);
        File.WriteAllText(Path.Join(_root, "contract.json"), Contract);
        TranscribeCppRuntime.ValidateContract(_root);
    }

    [Fact]
    public void ModelsGoToTheFirstDedicatedVulkanGpuAndNameIt()
    {
        var cpu = new TranscribeCppDevice(2, "CPU", "Ryzen", "cpu", TranscribeCppNative.DeviceTypeCpu, 0);
        var integrated = new TranscribeCppDevice(0, "Vulkan0", "AMD Radeon(TM) Graphics", "vulkan", TranscribeCppNative.DeviceTypeIntegratedGpu, 0);
        var dedicated = new TranscribeCppDevice(1, "Vulkan1", "Radeon RX 7800 XT", "vulkan", TranscribeCppNative.DeviceTypeGpu, 0);

        Assert.Equal(dedicated, TranscribeCppRuntime.PreferredVulkanDevice([integrated, dedicated, cpu]));
        Assert.Equal(integrated, TranscribeCppRuntime.PreferredVulkanDevice([integrated, cpu]));
        Assert.Null(TranscribeCppRuntime.PreferredVulkanDevice([cpu]));

        var status = TranscribeCppRecognizer.DescribeDevice(dedicated);
        Assert.Equal(TranscriptionAccelerationBackend.AmdVulkan, status.ActiveBackend);
        Assert.Equal("Radeon RX 7800 XT", status.DisplayText);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, TranscribeCppRecognizer.DescribeDevice(cpu).ActiveBackend);
    }

    [Fact]
    public void TokenTimesBecomeValidRescoringIntervals()
    {
        // Transducers can emit several tokens in one frame; a zero-length token borrows the time up to the next one.
        var timings = SherpaOnnxPlugin.TokenTimings(
            [new(" Ku", .32, .48), new("ber", .48, .48), new("netes", .64, .96), new(".", .96, 1.2)], 1.0);

        Assert.Equal([" Ku", "ber", "netes", "."], timings.Select(t => t.Text));
        Assert.Equal(.48, timings[1].StartSeconds, 6);
        Assert.Equal(.64, timings[1].EndSeconds, 6);
        Assert.Equal(1.0, timings[3].EndSeconds, 6);
        Assert.All(timings, t => Assert.True(t.EndSeconds > t.StartSeconds));
        Assert.Empty(SherpaOnnxPlugin.TokenTimings([new("a", .5, .6), new("b", .2, .3)], 1.0));
    }

    [Fact]
    public void ACudaChoiceFromEarlierVersionsReadsAsAutomatic()
    {
        using var plugin = new SherpaOnnxPlugin();

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        Assert.Equal(TranscriptionAccelerationPreference.Auto, plugin.AccelerationPreference);
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.AmdRocm);
        Assert.Equal(TranscriptionAccelerationPreference.Auto, plugin.AccelerationPreference);
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.AmdVulkan);

        Assert.Equal(SherpaOnnxPlugin.GpuPlatform ? TranscriptionAccelerationPreference.AmdVulkan : TranscriptionAccelerationPreference.Auto,
            plugin.AccelerationPreference);
        Assert.Equal(SherpaOnnxPlugin.GpuPlatform
            ? [TranscriptionAccelerationBackend.Cpu, TranscriptionAccelerationBackend.AmdVulkan]
            : [TranscriptionAccelerationBackend.Cpu], plugin.SupportedAccelerationBackends);
    }

    [Fact]
    public async Task ActivationDeletesTheRetiredCudaRuntime()
    {
        var cuda = Path.Join(_root, "Runtimes", "sherpa-onnx-cuda", "v1.13.0", "native");
        Directory.CreateDirectory(cuda);
        File.WriteAllText(Path.Join(cuda, "cudnn64_9.dll"), "retired");
        using var plugin = new SherpaOnnxPlugin();

        await plugin.ActivateAsync(new TestHost(_root));

        Assert.False(Directory.Exists(Path.Join(_root, "Runtimes", "sherpa-onnx-cuda")));
        Assert.True(Directory.Exists(Path.Join(_root, "Runtimes")));
    }

    [GpuPlatformFact]
    public async Task ChoosingTheGpuDownloadsOnlyItsFilesAndLoadsTheModelThere()
    {
        var gpus = new List<FakeGpu>();
        using var http = Server(out var requests);
        using var plugin = CreatePlugin(http, path => { var gpu = new FakeGpu(path); gpus.Add(gpu); return gpu; },
            cpu: (_, _) => throw new InvalidOperationException("The CPU must not load."));
        await plugin.ActivateAsync(new TestHost(_root));
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.AmdVulkan);
        Assert.False(plugin.IsModelDownloaded(ModelId));

        var reports = new List<double>();
        await plugin.DownloadModelAsync(ModelId, new SyncProgress(reports.Add), default);

        Assert.True(plugin.IsModelDownloaded(ModelId));
        Assert.Equal(["/runtime.tar.gz", "/model.gguf"], requests);
        Assert.Equal(1, reports[^1], 3);
        Assert.Equal(reports.Order(), reports);
        Assert.False(File.Exists(Path.Join(_root, "Models", ModelId, "encoder.int8.onnx")));

        await plugin.LoadModelAsync(ModelId, default);
        var result = await plugin.TranscribePcmAsync(new float[16000], null, false, default);

        Assert.Equal(Path.Join(_root, "Models", ModelId, "gpu", "model.gguf"), Assert.Single(gpus).Path);
        Assert.Equal(TranscriptionAccelerationBackend.AmdVulkan, plugin.AccelerationStatus.ActiveBackend);
        Assert.Equal("Test GPU", plugin.AccelerationStatus.DisplayText);
        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal(["Hal", "lo", " Welt"], result.TokenTimings.Select(t => t.Text));

        // Only the CPU files count once the CPU is chosen again.
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        Assert.False(plugin.IsModelDownloaded(ModelId));
        await plugin.RemoveModelAsync(ModelId, default);
        Assert.True(gpus[0].Disposed);
        Assert.False(Directory.Exists(Path.Join(_root, "Models", ModelId)));
    }

    [GpuPlatformFact]
    public async Task SwitchingALoadedModelToTheGpuDownloadsWhatIsMissing()
    {
        using var http = Server(out var requests);
        var cpuLoads = 0;
        using var plugin = CreatePlugin(http, path => new FakeGpu(path), cpu: (_, _) => { cpuLoads++; return null!; });
        await plugin.ActivateAsync(new TestHost(_root));
        await plugin.DownloadModelAsync(ModelId, null, default);
        await plugin.LoadModelAsync(ModelId, default);
        Assert.Equal(1, cpuLoads);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.AmdVulkan);
        await plugin.LoadModelAsync(ModelId, default);

        Assert.Equal(["/encoder.int8.onnx", "/tokens.txt", "/runtime.tar.gz", "/model.gguf"], requests);
        Assert.Equal(TranscriptionAccelerationBackend.AmdVulkan, plugin.AccelerationStatus.ActiveBackend);
        Assert.Equal(1, cpuLoads);
    }

    [GpuPlatformFact]
    public async Task AutomaticUsesTheGpuOnlyOnceItsFilesArePresentAndFallsBackToTheCpu()
    {
        using var http = Server(out var requests);
        var cpuLoads = 0;
        var gpuFails = false;
        using var plugin = CreatePlugin(http, path => gpuFails ? throw new InvalidOperationException("Device lost") : new FakeGpu(path),
            cpu: (_, _) => { cpuLoads++; return null!; });
        await plugin.ActivateAsync(new TestHost(_root));
        await plugin.DownloadModelAsync(ModelId, null, default);

        await plugin.LoadModelAsync(ModelId, default);
        Assert.Equal(1, cpuLoads);
        Assert.DoesNotContain("/model.gguf", requests);

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.AmdVulkan);
        await plugin.DownloadModelAsync(ModelId, null, default);
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Auto);
        await plugin.LoadModelAsync(ModelId, default);
        Assert.Equal(TranscriptionAccelerationBackend.AmdVulkan, plugin.AccelerationStatus.ActiveBackend);
        Assert.Equal(1, cpuLoads);

        gpuFails = true;
        await plugin.LoadModelAsync(ModelId, default);
        Assert.Equal(2, cpuLoads);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.Contains("Device lost", plugin.AccelerationStatus.Detail);

        // A chosen GPU reports its failure instead of quietly using the CPU.
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.AmdVulkan);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync(ModelId, default));
        Assert.Equal("Device lost", error.Message);
        Assert.Equal("GPU unavailable", plugin.AccelerationStatus.DisplayText);
        Assert.Equal(2, cpuLoads);
    }

    [GpuPlatformFact]
    public async Task ModelsWithoutAGpuVersionStayOnTheCpu()
    {
        using var http = Server(out var requests);
        var cpuLoads = 0;
        using var plugin = CreatePlugin(http, _ => throw new InvalidOperationException("No GPU version."),
            cpu: (_, _) => { cpuLoads++; return null!; }, gguf: false);
        await plugin.ActivateAsync(new TestHost(_root));
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.AmdVulkan);

        await plugin.DownloadModelAsync(ModelId, null, default);
        await plugin.LoadModelAsync(ModelId, default);

        Assert.Equal(["/encoder.int8.onnx", "/tokens.txt"], requests);
        Assert.Equal(1, cpuLoads);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.Equal("Test Model runs on the CPU.", plugin.AccelerationStatus.Detail);
    }

    [GpuPlatformFact]
    public async Task LongRecordingsRunOnTheGpuInTheSameChunksAsOnTheCpu()
    {
        FakeGpu? gpu = null;
        using var http = Server(out _);
        using var plugin = CreatePlugin(http, path => gpu = new FakeGpu(path));
        await plugin.ActivateAsync(new TestHost(_root));
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.AmdVulkan);
        await plugin.LoadModelAsync(ModelId, default);
        var samples = new float[(SherpaOnnxPlugin.ParakeetMaximumChunkSeconds + 60) * SherpaOnnxPlugin.SampleRate];

        var result = await plugin.TranscribePcmAsync(samples, null, false, default);

        Assert.Equal(SherpaOnnxPlugin.CreateParakeetChunks(samples.Length).Select(chunk => chunk.Count), gpu!.Lengths);
        Assert.Empty(result.TokenTimings);
        Assert.Equal(SherpaOnnxPlugin.ParakeetMaximumChunkSeconds + 60, result.DurationSeconds, 3);
    }

    /// <summary>
    /// Loads the real runtime and model through the plugin's GPU path and transcribes on the machine's graphics card.
    /// Opt-in because it needs about 760 MB of files and a Vulkan driver.
    /// </summary>
    [LocalTranscribeCppFact]
    public async Task RealRuntimeTranscribesSpeechOnTheGraphicsCard()
    {
        var archive = Environment.GetEnvironmentVariable("TYPEWHISPER_TEST_TRANSCRIBECPP_ARCHIVE")!;
        var model = Environment.GetEnvironmentVariable("TYPEWHISPER_TEST_TRANSCRIBECPP_MODEL")!;
        var wav = Environment.GetEnvironmentVariable("TYPEWHISPER_TEST_TRANSCRIBECPP_WAV")!;
        using var http = Http(request => new StreamContent(File.OpenRead(request.RequestUri!.AbsolutePath.EndsWith(".gguf") ? model : archive)));
        var gguf = new FileInfo(model);
        using var plugin = new SherpaOnnxPlugin(http, [TestModel(new("Real GGUF", "https://example.invalid/model.gguf", Sha256(File.ReadAllBytes(model)), gguf.Length, gguf.Name))],
            gpuRuntime: TranscribeCppAsset.Runtime with { Sha256 = Sha256(File.ReadAllBytes(archive)), Size = new FileInfo(archive).Length });
        await plugin.ActivateAsync(new TestHost(_root));
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.AmdVulkan);

        await plugin.LoadModelAsync(ModelId, default);
        foreach (var (which, size) in TranscribeCppNative.ManagedStructSizes)
            Assert.Equal(size, (int)TranscribeCppNative.transcribe_abi_struct_size(which));
        var result = await plugin.TranscribeAsync(File.ReadAllBytes(wav), null, false, null, default);

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.NotEmpty(result.TokenTimings);
        Assert.All(result.TokenTimings, t => Assert.Contains(t.Text.Trim(), result.Text, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(TranscriptionAccelerationBackend.AmdVulkan, plugin.AccelerationStatus.ActiveBackend);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.TranscribeAsync(File.ReadAllBytes(wav), null, false, null, cancelled.Token));
    }

    private const string Contract = """{"version":"0.3.1","header_hash":"57b1af43650f195d","backends":["vulkan","cpu"],"lane":"cpu-vulkan"}""";

    private static SherpaOnnxPlugin CreatePlugin(HttpClient http, Func<string, IGpuRecognizer> gpu,
        Func<string, string, SherpaOnnx.OfflineRecognizer>? cpu = null, bool gguf = true) =>
        new(http, [TestModel(gguf ? new("Test GGUF", "https://example.invalid/model.gguf", Sha256(Gguf), Gguf.Length, "model.gguf") : null)],
            cpu ?? ((_, _) => null!), gpu, RuntimeAsset(RuntimeArchive));

    private static SherpaOnnxPlugin.ModelDefinition TestModel(TranscribeCppAsset? gguf) => new(
        ModelId, "Test Model", "Test", "~1 MB", 1, 1, false, false,
        [
            new("encoder.int8.onnx", "https://example.invalid/encoder.int8.onnx", 1, Sha256(Encoder)),
            new("tokens.txt", "https://example.invalid/tokens.txt", 1, Sha256(Tokens))
        ], gguf);

    // Serves the CPU files, the runtime archive and the GGUF, and records the requested paths.
    private static HttpClient Server(out List<string> requests)
    {
        var log = requests = [];
        var archive = Archive();
        return Http(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            log.Add(path);
            return new ByteArrayContent(path switch
            {
                "/model.gguf" => Gguf,
                "/runtime.tar.gz" => RuntimeArchive,
                "/encoder.int8.onnx" => Encoder,
                _ => Tokens
            });
        });
    }

    private static byte[] Archive(string? failure = null)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            void Add(string name, string content)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };
                tar.WriteEntry(entry);
            }
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, Root + "/"));
            Add(Root + "/contract.json", Contract);
            if (failure != "missing-library") Add(Root + "/transcribe.dll", "native library");
            Add(Root + "/ggml.dll", "ggml");
            Add(Root + "/licenses/LICENSE", "MIT");
            Add(Root + "/transcribe-cli.exe", "not needed");
            Add(Root + "/include/transcribe.h", "not needed");
            if (failure == "traversal") Add(Root + "/../evil.dll", "outside");
            if (failure == "link") tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, Root + "/link.dll") { LinkName = "C:/Windows/evil.dll" });
        }
        return buffer.ToArray();
    }

    private static TranscribeCppAsset RuntimeAsset(byte[] archive) =>
        new("Test runtime", "https://example.invalid/runtime.tar.gz", Sha256(archive), archive.Length, "transcribe.dll", Root);

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static HttpClient Http(Func<HttpContent> content) => Http(_ => content());
    private static HttpClient Http(Func<HttpRequestMessage, HttpContent> content) => new(new Handler(content));

    private sealed class Handler(Func<HttpRequestMessage, HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content(request) }); }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class FakeGpu(string path) : IGpuRecognizer
    {
        internal string Path { get; } = path;
        internal List<int> Lengths { get; } = [];
        internal bool Disposed { get; private set; }
        public TranscriptionAccelerationStatus Status { get; } = new(TranscriptionAccelerationBackend.AmdVulkan, "Test GPU");

        public TranscribeCppResult Transcribe(float[] samples, bool tokens, CancellationToken cancellationToken)
        {
            Lengths.Add(samples.Length);
            return new("Hallo Welt", tokens ? [new("Hal", .1, .2), new("lo", .2, .3), new(" Welt", .4, .6)] : []);
        }

        public void Dispose() => Disposed = true;
    }

    public void Dispose()
    {
        // The real-runtime test leaves transcribe.dll loaded; native libraries stay locked until the process ends.
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (UnauthorizedAccessException) { }
    }
}

// transcribe.cpp publishes its Vulkan runtime for Windows x64 only; elsewhere the plugin offers the CPU alone.
public sealed class GpuPlatformFactAttribute : FactAttribute
{
    public GpuPlatformFactAttribute()
    {
        if (!SherpaOnnxPlugin.GpuPlatform) Skip = "The graphics card path requires Windows x64.";
    }
}

public sealed class LocalTranscribeCppFactAttribute : FactAttribute
{
    public LocalTranscribeCppFactAttribute()
    {
        if (new[] { "ARCHIVE", "MODEL", "WAV" }.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPEWHISPER_TEST_TRANSCRIBECPP_" + name))))
            Skip = "Opt-in test: set TYPEWHISPER_TEST_TRANSCRIBECPP_ARCHIVE to transcribe-native-0.3.1-windows-x86_64-cpu-vulkan.tar.gz, " +
                "TYPEWHISPER_TEST_TRANSCRIBECPP_MODEL to parakeet-tdt-0.6b-v3-Q8_0.gguf and TYPEWHISPER_TEST_TRANSCRIBECPP_WAV to a 16 kHz mono speech WAV.";
    }
}
