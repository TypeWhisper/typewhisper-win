using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.PortableFixture;

// Starts real worker processes. The fixture engine crashes, hangs or fails when asked through the language.
public sealed class TranscriptionWorkerTests : IAsyncLifetime
{
    private const string PluginId = "test.typewhisper.worker";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "transcription-worker-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _notices = [];
    private readonly List<string> _logs = [];
    private string PackageDirectory => Path.Combine(_root, "package");
    private string DataDirectory => Path.Combine(_root, "data");
    private LoggingHost _host = null!;
    private WorkerProbePlugin _inner = null!;
    private IsolatedTranscriptionEngine _engine = null!;

    private static string WorkerPath => Path.Combine(AppContext.BaseDirectory,
        "TypeWhisper.TranscriptionWorker.TestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));

    public async Task InitializeAsync()
    {
        WritePackage(PackageDirectory, typeof(WorkerProbePlugin));
        _host = new(DataDirectory, _logs);
        _inner = new WorkerProbePlugin();
        await _inner.ActivateAsync(_host);
        _inner.SelectModel("small");
        _engine = Isolation().TryIsolate(_inner, PackageDirectory, _host)
            ?? throw new InvalidOperationException("The fixture engine was not isolated.");
        _engine.Notice += message => { lock (_notices) _notices.Add(message); };
    }

    public async Task DisposeAsync()
    {
        if (_engine is not null) await _engine.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static TranscriptionIsolation Isolation() =>
        new(WorkerPath, [TranscriptionWorkerServer.Argument], new HashSet<string> { PluginId }, new Version(1, 1, 5));

    // Short enough for tests, long enough that a loaded machine does not miss ten beats in a row.
    private static readonly TimeSpan InactivityWindow = TimeSpan.FromSeconds(2);

    private IsolatedTranscriptionEngine WatchedEngine()
    {
        var isolation = new TranscriptionIsolation(WorkerPath, [TranscriptionWorkerServer.Argument], new HashSet<string> { PluginId }, new Version(1, 1, 5))
            { HeartbeatInterval = TimeSpan.FromMilliseconds(200), RequestInactivityTimeout = InactivityWindow };
        var engine = isolation.TryIsolate(_inner, PackageDirectory, _host)!;
        engine.Notice += message => { lock (_notices) _notices.Add(message); };
        return engine;
    }

    private static void WritePackage(string folder, Type type)
    {
        Directory.CreateDirectory(folder);
        File.Copy(type.Assembly.Location, Path.Combine(folder, "fixture.dll"), overwrite: true);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
        {
            Id = PluginId, Name = "Worker fixture", Version = "1.0.0", IsLocal = true, AssemblyName = "fixture.dll", PluginClass = type.FullName!
        }));
    }

    private static Dictionary<string, string> Fields(PluginTranscriptionResult result) => result.Text.Split(';')
        .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1]);

    [Fact]
    public async Task TranscribesInAWorkerProcessWithTheHostSelectionAndTimings()
    {
        var result = await _engine.TranscribePcmAsync(new float[] { 0, 0.5f, 0.25f }, "en", false, default);
        var fields = Fields(result);
        Assert.NotEqual(Environment.ProcessId.ToString(), fields["pid"]);
        Assert.Equal(_engine.WorkerProcessId.ToString(), fields["pid"]);
        Assert.Equal("small", fields["model"]);
        Assert.Equal("small", fields["selected"]);
        Assert.Equal("3", fields["length"]);
        Assert.Equal("0.25", fields["detail"]);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(new VocabularyTokenTiming("probe", 0.25, 0.5), Assert.Single(result.TokenTimings));
        Assert.Equal(new PluginTranscriptionSegment("probe", 0, 1), Assert.Single(result.Segments));

        var wav = await _engine.TranscribeWithLanguageHintsAsync([1, 2, 3, 4], ["de", "en"], false, "terms", default);
        Assert.Equal("4", Fields(wav)["length"]);
        Assert.Equal("terms", Fields(wav)["detail"]);
        Assert.Contains(_logs, line => line == "activated in " + fields["pid"]);
        Assert.Contains(_logs, line => line == "loaded small in " + fields["pid"]);
    }

    [Fact]
    public async Task ModelChangesInTheHostReachTheRunningWorker()
    {
        await _engine.TranscribePcmAsync(new float[] { 0 }, null, false, default);
        var worker = _engine.WorkerProcessId;
        _engine.SelectModel("large");
        var fields = Fields(await _engine.TranscribePcmAsync(new float[] { 0 }, null, false, default));
        Assert.Equal(worker.ToString(), fields["pid"]);
        Assert.Equal("large", fields["model"]);
        Assert.Equal("large", _inner.SelectedModelId);
    }

    [Fact]
    public async Task ACrashIsRetriedOnceInANewWorkerWithoutANotice()
    {
        var result = await _engine.TranscribePcmAsync(new float[] { 0 }, "crash-once", false, default);
        Assert.Equal(_engine.WorkerProcessId.ToString(), Fields(result)["pid"]);
        Assert.False(_engine.UsesCpuFallback);
        Assert.Empty(_notices);
        Assert.Contains(_logs, line => line.Contains("worker crashed (1 in a row", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(DataDirectory, "transcription-worker.log")));
    }

    [Fact]
    public async Task RepeatedCrashesOnTheGraphicsCardSwitchToTheCpu()
    {
        _engine.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        var result = await _engine.TranscribePcmAsync(new float[] { 0 }, "crash-accelerated", false, default);
        Assert.Equal("Cpu", Fields(result)["accel"]);
        Assert.True(_engine.UsesCpuFallback);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, _engine.AccelerationStatus.ActiveBackend);
        Assert.Contains("switched it to the CPU", Assert.Single(_notices));
        // The fallback stays until the user chooses an acceleration again.
        Assert.Equal("Cpu", Fields(await _engine.TranscribePcmAsync(new float[] { 0 }, "crash-accelerated", false, default))["accel"]);
        _engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Auto);
        Assert.False(_engine.UsesCpuFallback);
        Assert.Equal("Auto", Fields(await _engine.TranscribePcmAsync(new float[] { 0 }, null, false, default))["accel"]);
    }

    [Fact]
    public async Task CrashesOnTheCpuUnderAutoDoNotClaimAGraphicsCardFallback()
    {
        _host.SetSetting("NoGpu", true);
        await _engine.LoadModelAsync("small", default);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, _engine.AccelerationStatus.ActiveBackend);
        await Assert.ThrowsAsync<TranscriptionWorkerFaultedException>(() => _engine.TranscribePcmAsync(new float[] { 0 }, "crash", false, default));
        Assert.False(_engine.UsesCpuFallback);
        Assert.Contains("paused", Assert.Single(_notices));
    }

    [Fact]
    public async Task RepeatedCrashesPauseTheEngineUntilAModelIsLoadedAgain()
    {
        _engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        await Assert.ThrowsAsync<TranscriptionWorkerFaultedException>(() => _engine.TranscribePcmAsync(new float[] { 0 }, "crash", false, default));
        Assert.Contains("paused", Assert.Single(_notices));
        Assert.Null(_engine.WorkerProcessId);
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TranscriptionWorkerFaultedException>(() => _engine.TranscribePcmAsync(new float[] { 0 }, null, false, default));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), "A paused engine must not start a worker.");
        Assert.Null(_engine.WorkerProcessId);
        await _engine.LoadModelAsync("small", default);
        Assert.Equal("small", Fields(await _engine.TranscribePcmAsync(new float[] { 0 }, null, false, default))["model"]);
    }

    [Fact]
    public async Task EngineErrorsKeepTheirKindAndTheWorker()
    {
        await _engine.TranscribePcmAsync(new float[] { 0 }, null, false, default);
        var worker = _engine.WorkerProcessId;
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => _engine.TranscribePcmAsync(new float[] { 0 }, "throw", false, default));
        Assert.Equal("Fixture engine rejected the request.", error.Message);
        Assert.Equal(worker, _engine.WorkerProcessId);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task CancellationReachesADecodeThatHonorsIt()
    {
        await _engine.TranscribePcmAsync(new float[] { 0 }, null, false, default);
        var worker = _engine.WorkerProcessId;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.TranscribePcmAsync(new float[] { 0 }, "hang", false, cancellation.Token));
        Assert.Equal(worker, _engine.WorkerProcessId);
    }

    [Fact]
    public async Task CancellationEndsADecodeThatIgnoresItWithoutCountingACrash()
    {
        await _engine.TranscribePcmAsync(new float[] { 0 }, null, false, default);
        var worker = Process.GetProcessById(_engine.WorkerProcessId!.Value);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.TranscribePcmAsync(new float[] { 0 }, "hang-hard", false, cancellation.Token));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(await _engine.TranscribePcmAsync(new float[] { 0 }, null, false, default));
        Assert.Empty(_notices);
        Assert.DoesNotContain(_logs, line => line.Contains("worker crashed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWorkerThatStopsBeatingIsEndedAfterTheInactivityWindowAndTheRequestRetried()
    {
        await using var engine = WatchedEngine();
        await engine.TranscribePcmAsync(new float[] { 0 }, null, false, default);
        var frozen = Process.GetProcessById(engine.WorkerProcessId!.Value);
        var timer = Stopwatch.StartNew();
        var result = await engine.TranscribePcmAsync(new float[] { 0 }, "freeze-once", false, default);
        Assert.True(timer.Elapsed >= InactivityWindow - TimeSpan.FromMilliseconds(50), "The worker was ended before the window had passed.");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), "Ending the frozen worker took too long.");
        Assert.Equal(engine.WorkerProcessId.ToString(), Fields(result)["pid"]);
        Assert.NotEqual(frozen.Id, engine.WorkerProcessId);
        await frozen.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(_logs, line => line.Contains("did not answer the transcribe request", StringComparison.Ordinal));
        Assert.Contains(_logs, line => line.Contains("worker stopped answering (1 in a row", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs, line => line.Contains("worker crashed", StringComparison.Ordinal));
        Assert.False(engine.UsesCpuFallback);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task ASlowRequestThatKeepsBeatingIsNotEnded()
    {
        await using var engine = WatchedEngine();
        await engine.TranscribePcmAsync(new float[] { 0 }, null, false, default);
        var worker = engine.WorkerProcessId;
        Assert.True(WorkerProbePlugin.SlowDecode > InactivityWindow, "The slow decode must outlast the window to prove anything.");
        var result = await engine.TranscribePcmAsync(new float[] { 0 }, "slow", false, default);
        Assert.Equal(worker.ToString(), Fields(result)["pid"]);
        Assert.DoesNotContain(_logs, line => line.Contains("did not answer", StringComparison.Ordinal) || line.Contains("stopped answering", StringComparison.Ordinal));
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task DisposingDuringAHungRequestEndsTheWorkerWithinTheCancellationGrace()
    {
        await _engine.TranscribePcmAsync(new float[] { 0 }, null, false, default);
        var worker = Process.GetProcessById(_engine.WorkerProcessId!.Value);
        // The request ignores cancellation and keeps beating, so only the stop can end it.
        var hung = _engine.TranscribePcmAsync(new float[] { 0 }, "hang-hard", false, default);
        await Task.Delay(300);
        var timer = Stopwatch.StartNew();
        await _engine.DisposeAsync();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "Disposing waited behind the hung request.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => hung);
        Assert.Contains("was stopped", error.Message);
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(_engine.WorkerProcessId);
        Assert.DoesNotContain(_logs, line => line.Contains("worker crashed", StringComparison.Ordinal) || line.Contains("stopped answering", StringComparison.Ordinal));
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task ACrashWhileLoadingIsReportedWithoutEndingTheHost()
    {
        _host.SetSetting("CrashOnLoad", true);
        _engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        await Assert.ThrowsAsync<TranscriptionWorkerFaultedException>(() => _engine.LoadModelAsync("small", default));
        _host.SetSetting("CrashOnLoad", false);
        await _engine.LoadModelAsync("small", default);
    }

    [Fact]
    public async Task UnloadAndDisposeEndTheWorker()
    {
        await _engine.LoadModelAsync("large", default);
        var first = Process.GetProcessById(_engine.WorkerProcessId!.Value);
        await _engine.UnloadModelAsync();
        await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(_engine.WorkerProcessId);
        await _engine.TranscribePcmAsync(new float[] { 0 }, null, false, default);
        var second = Process.GetProcessById(_engine.WorkerProcessId!.Value);
        await _engine.DisposeAsync();
        await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AnIdleWorkerEndsAndTheNextRequestLoadsTheModelAgain()
    {
        var time = new ManualTimeProvider();
        var isolation = new TranscriptionIsolation(WorkerPath, [TranscriptionWorkerServer.Argument], new HashSet<string> { PluginId }, new Version(1, 1, 5))
            { IdleUnloadPolicy = new(60, time) };
        await using var engine = isolation.TryIsolate(_inner, PackageDirectory, _host)!;
        var first = Fields(await engine.TranscribePcmAsync(new float[] { 0 }, null, false, default))["pid"];
        var worker = Process.GetProcessById(engine.WorkerProcessId!.Value);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.NotNull(engine.WorkerProcessId);
        time.Advance(TimeSpan.FromSeconds(1));
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await ManualTimeProvider.WaitUntilAsync(() => engine.WorkerProcessId is null);
        var fields = Fields(await engine.TranscribePcmAsync(new float[] { 0 }, null, false, default));
        Assert.NotEqual(first, fields["pid"]);
        Assert.Equal("small", fields["model"]);
        Assert.Contains(_logs, line => line == "loaded small in " + fields["pid"]);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task PreparingLoadsTheSelectedModelOnlyWhenNoWorkerRuns()
    {
        await _engine.PrepareAsync();
        var pid = _engine.WorkerProcessId!.Value;
        Assert.Contains(_logs, line => line == "loaded small in " + pid);
        await _engine.PrepareAsync();
        var fields = Fields(await _engine.TranscribePcmAsync(new float[] { 0 }, null, false, default));
        Assert.Equal(pid.ToString(), fields["pid"]);
        Assert.Single(_logs, line => line.StartsWith("loaded small in ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PluginActivationErrorsAreNotTreatedAsCrashes()
    {
        var broken = Path.Combine(_root, "broken");
        WritePackage(broken, typeof(WorkerProbePlugin));
        File.Delete(Path.Combine(broken, "fixture.dll"));
        await using var engine = Isolation().TryIsolate(_inner, broken, _host)!;
        await Assert.ThrowsAnyAsync<Exception>(() => engine.TranscribePcmAsync(new float[] { 0 }, null, false, default));
        Assert.DoesNotContain(_logs, line => line.Contains("worker crashed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRegistryPresentsIsolatedEnginesAndEndsTheirWorkersOnRequestAndDisable()
    {
        var bundles = Path.Combine(_root, "bundles");
        WritePackage(Path.Combine(bundles, PluginId), typeof(WorkerProbePlugin));
        using var http = new HttpClient();
        var store = new PortablePluginStore(Path.Combine(_root, "store"), new Version(1, 1, 5), http);
        await store.InitializeAsync(bundles);
        var host = new LoggingHost(Path.Combine(_root, "registry-data"), _logs);
        await using var registry = new PortablePluginRuntimeRegistry(store, new Version(1, 1, 5), _ => host) { TranscriptionIsolation = Isolation() };
        Assert.Null(await registry.SetEnabledAsync(PluginId, true));
        var provider = Assert.Single(registry.TranscriptionProviders);
        Assert.True(provider.SupportsPcm);
        var model = (await registry.GetModelStatesAsync(provider.SelectionId)).Single(state => state.ModelId == "large");
        await registry.SelectModelAsync(model);
        Task<(int, PluginTranscriptionResult)> Transcribe() => registry.UseTranscriptionAsync(provider.SelectionId, async (engine, ct) =>
        {
            Assert.IsType<IsolatedTranscriptionEngine>(engine);
            var decoded = await LanguageHintTranscription.DecodeAsync(engine, new float[] { 0 }, () => [], null, [], false, ct);
            return (((IsolatedTranscriptionEngine)engine).WorkerProcessId!.Value, decoded);
        });
        var (pid, result) = await Transcribe();
        Assert.Equal("large", Fields(result)["model"]);
        var worker = Process.GetProcessById(pid);
        await registry.StopTranscriptionWorkersAsync(PluginId);
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        (pid, _) = await Transcribe();
        worker = Process.GetProcessById(pid);
        Assert.Null(await registry.SetEnabledAsync(PluginId, false));
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class LoggingHost(string directory, List<string> logs) : IPluginHostServices
    {
        private readonly VocabularyHostServices _inner = new(directory);
        public Task StoreSecretAsync(string key, string value) => _inner.StoreSecretAsync(key, value);
        public Task<string?> LoadSecretAsync(string key) => _inner.LoadSecretAsync(key);
        public Task DeleteSecretAsync(string key) => _inner.DeleteSecretAsync(key);
        public T? GetSetting<T>(string key) => _inner.GetSetting<T>(key);
        public void SetSetting<T>(string key, T value) => _inner.SetSetting(key, value);
        public string PluginDataDirectory => _inner.PluginDataDirectory;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus => _inner.EventBus;
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { lock (logs) logs.Add(message); }
        public void NotifyCapabilitiesChanged() { }
        public IPluginLocalization Localization => _inner.Localization;
    }
}
