using System.IO;
using System.Text.Json;
using TypeWhisper.Plugin.GraniteSpeech;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class GraniteSpeechPluginTests
{
    [Fact]
    public void Manifest_TargetsTypeWhisper084OrNewer()
    {
        var manifest = ReadManifest();

        Assert.NotNull(manifest);
        Assert.Equal("com.typewhisper.granite-speech", manifest.Id);
        Assert.Equal("1.1.2", manifest.MinHostVersion);
        Assert.True(manifest.IsLocal);
        Assert.Contains("transcription", manifest.Categories!);
    }

    [Fact]
    public void ModelCard_ListsSupportedLanguages()
    {
        using var sut = new GraniteSpeechPlugin();
        var model = Assert.Single(sut.TranscriptionModels);
        Assert.Equal(sut.SupportedLanguages, model.LanguageCodes);
        Assert.Equal(model.LanguageCodes.Count, model.LanguageCount);
        Assert.Equal("Local (Granite Speech)", sut.ProviderDisplayName);
    }

    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifest = ReadManifest();
        var sut = new GraniteSpeechPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public async Task RemoveModelAsync_DeletesTheManagedAssetDirectory()
    {
        var assetDirectory = Path.Join(Path.GetTempPath(), $"tw-granite-remove-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Join(assetDirectory, "managed-runtime", "hf-cache"));
            await File.WriteAllTextAsync(Path.Join(assetDirectory, "settings.json"), "preserve");
            await File.WriteAllTextAsync(Path.Join(assetDirectory, "managed-runtime", ".setup-complete"), "ready");
            await File.WriteAllTextAsync(Path.Join(assetDirectory, "managed-runtime", "hf-cache", "weights.bin"), "model");
            using var sut = new GraniteSpeechPlugin();
            await sut.ActivateAsync(new FakePluginHostServices(assetDirectory));

            await sut.RemoveModelAsync("granite-4.0-1b-speech", CancellationToken.None);

            Assert.True(sut.SupportsModelRemoval);
            Assert.False(Directory.Exists(Path.Join(assetDirectory, "managed-runtime")));
            Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Join(assetDirectory, "settings.json")));
            Assert.False(sut.IsModelDownloaded("granite-4.0-1b-speech"));
        }
        finally
        {
            if (Directory.Exists(assetDirectory))
                Directory.Delete(assetDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RemoveModelAsync_RequiresActivatedHostBeforeRecursiveDelete()
    {
        using var sut = new GraniteSpeechPlugin();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RemoveModelAsync("granite-4.0-1b-speech", CancellationToken.None));

        Assert.Equal("Plugin is not activated.", error.Message);
    }

    [Fact]
    public async Task ProcessingDevicePersistsAndInvalidValuesDoNotChangeIt()
    {
        var host = new FakePluginHostServices(Path.GetTempPath());
        using var sut = new GraniteSpeechPlugin();
        await sut.ActivateAsync(host);
        Assert.False(sut.IsConfigured);
        Assert.True(sut.SupportsLocalLivePreview);
        Assert.IsAssignableFrom<IPcmTranscriptionEnginePlugin>(sut);
        await sut.SaveTextSettingAsync("device", "NvidiaCuda", default);
        await sut.DeactivateAsync();
        await sut.ActivateAsync(host);
        Assert.Equal("NvidiaCuda", Assert.Single(sut.TextSettings).Value);
        await Assert.ThrowsAsync<ArgumentException>(() => sut.SaveTextSettingAsync("device", "unknown", default));
        Assert.Equal("NvidiaCuda", Assert.Single(sut.TextSettings).Value);
        Assert.False(sut.IsModelDownloaded("unknown"));
    }

    [Fact]
    public async Task InvalidPcmAndCanceledCallsNeverStartALocalProcess()
    {
        using var sut = new GraniteSpeechPlugin();
        await Assert.ThrowsAsync<ArgumentException>(() => sut.TranscribePcmAsync(new float[] { float.NaN }, "de", false, default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.TranscribePcmAsync(new float[160], "de", false, canceled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.TranscribeAsync([], "xx", false, null, default));
        Assert.Equal("Model not loaded", sut.AccelerationStatus.DisplayText);
    }

    [Fact]
    public async Task RuntimeReadinessRequiresPatchedPackagesAndSupportsDeviceChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "granite-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "managed-runtime"));
            var marker = Path.Combine(root, "managed-runtime", ".setup-complete");
            var cachedModel = Path.Combine(root, "managed-runtime", "cached-model");
            await File.WriteAllTextAsync(cachedModel, "keep");
            await File.WriteAllTextAsync(marker, "old-runtime");
            using var sut = new GraniteSpeechPlugin();
            await sut.ActivateAsync(new FakePluginHostServices(root));
            Assert.False(sut.IsConfigured);
            await sut.SaveTextSettingAsync("device", "Cpu", default);
            await File.WriteAllTextAsync(marker, GraniteSpeechPlugin.RuntimeRevision + "|cpu");
            Assert.True(sut.IsConfigured);
            await sut.SaveTextSettingAsync("device", "NvidiaCuda", default);
            Assert.False(sut.IsConfigured);
            Assert.Equal("keep", await File.ReadAllTextAsync(cachedModel));
            await File.WriteAllTextAsync(marker, GraniteSpeechPlugin.RuntimeRevision + "|cuda");
            Assert.True(sut.IsConfigured);
            await sut.SaveTextSettingAsync("device", "Cpu", default);
            Assert.True(sut.IsConfigured);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SettingsActionCanRemoveTheOnlySelectedModelAndPreservesSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "granite-action-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "managed-runtime"));
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "preserve");
            using var sut = new GraniteSpeechPlugin();
            await sut.ActivateAsync(new FakePluginHostServices(root));
            Assert.NotNull(sut.SelectedModelId);
            await sut.ExecuteSettingsActionAsync("remove-assets", default);
            Assert.Null(sut.SelectedModelId);
            Assert.False(Directory.Exists(Path.Combine(root, "managed-runtime")));
            Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(root, "settings.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PythonPathsPreserveDriveAndUncRootsWithExtendedLengthSupport()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("/tmp/python", GraniteSpeechPlugin.ExtendedPythonPath("/tmp/python"));
            return;
        }
        Assert.Equal(@"\\?\C:\data\python", GraniteSpeechPlugin.ExtendedPythonPath(@"C:\data\python"));
        Assert.Equal(@"\\?\UNC\server\share\python", GraniteSpeechPlugin.ExtendedPythonPath(@"\\server\share\python"));
        Assert.Equal(@"\\?\C:\data\python", GraniteSpeechPlugin.ExtendedPythonPath(@"\\?\C:\data\python"));
    }

    [Fact]
    public void RuntimeSelectionPinsTheActualWheelFlavor()
    {
        Assert.Equal("torch==2.13.0+cpu torchaudio==2.11.0+cpu", GraniteSpeechPlugin.RuntimeWheels("Cpu"));
        Assert.Equal("torch==2.13.0+cu130 torchaudio==2.11.0+cu130", GraniteSpeechPlugin.RuntimeWheels("NvidiaCuda"));
        Assert.Equal(GraniteSpeechPlugin.RuntimeWheels("NvidiaCuda"), GraniteSpeechPlugin.RuntimeWheels("Auto"));
    }

    [Fact]
    public async Task CancelingAnInFlightCommandTerminatesTheSidecar()
    {
        // Native Windows process behavior; the portable validation suite also runs on Linux.
        if (!OperatingSystem.IsWindows()) return;
        var start = new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "[Console]::ReadLine() | Out-Null; Start-Sleep -Seconds 30" }
        };
        using var child = System.Diagnostics.Process.Start(start)!;
        using var observer = System.Diagnostics.Process.GetProcessById(child.Id);
        using var sut = new GraniteSpeechPlugin();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(GraniteSpeechPlugin).GetField(name, flags)!.SetValue(sut, value);
        Set("_sidecar", child); Set("_sidecarIn", child.StandardInput); Set("_sidecarOut", child.StandardOutput);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            var pending = (Task<JsonElement>)typeof(GraniteSpeechPlugin).GetMethod("SendCommandAsync", flags)!.Invoke(sut, [new { cmd = "transcribe" }, cancellation.Token])!;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            Assert.True(observer.HasExited);
            Assert.Null(typeof(GraniteSpeechPlugin).GetField("_sidecar", flags)!.GetValue(sut));
        }
        finally { if (!observer.HasExited) observer.Kill(entireProcessTree: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeDownloadsRequireTheExpectedHashBeforeReplacingFiles(bool valid)
    {
        var root = Path.Combine(Path.GetTempPath(), "granite-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "artifact.zip");
        byte[] payload = [1, 2, 3];
        using var http = new System.Net.Http.HttpClient(new PayloadHandler(payload));
        try
        {
            await File.WriteAllTextAsync(destination, "previous");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(valid ? payload : [4, 5, 6]));
            var download = GraniteSpeechPlugin.DownloadFileAsync(http, "https://fixture.invalid/runtime", destination, hash, default);
            if (valid)
            {
                await download;
                Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => download);
                Assert.Equal("previous", await File.ReadAllTextAsync(destination));
            }
            Assert.False(File.Exists(destination + ".tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class PayloadHandler(byte[] payload) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(payload) });
    }

    private static PluginManifest? ReadManifest() =>
        JsonSerializer.Deserialize<PluginManifest>(
            TestFile.ReadProjectFile("plugins-v2", "TypeWhisper.Plugin.GraniteSpeech", "manifest.json"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    private sealed class FakePluginHostServices(string pluginDataDirectory) : IPluginHostServices
    {
        public string PluginDataDirectory { get; } = pluginDataDirectory;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new NoOpPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new NoOpPluginLocalization();

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
        private readonly Dictionary<string, object?> _settings = [];
        public T? GetSetting<T>(string key) => _settings.TryGetValue(key, out var value) ? (T?)value : default;
        public void SetSetting<T>(string key, T value) => _settings[key] = value;
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
    }

    private sealed class NoOpPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent => new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class NoOpPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }
}
