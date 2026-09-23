using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;

namespace TypeWhisper.PluginSDK.Portable.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class LegacyWindowsProfileMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "windows-upgrade-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "legacy");
    private string Destination => Path.Combine(_root, "new");
    private const string Plugin = "com.typewhisper.groq";
    private void Seed(string? secret = null)
    {
        Directory.CreateDirectory(Path.Combine(Source, "Data"));
        File.WriteAllText(Path.Combine(Source, "settings.json"), JsonSerializer.Serialize(new AppSettings
        { SelectedModelId = "plugin:" + Plugin + ":whisper-large-v3", PluginEnabledState = new() { [Plugin] = true }, HasCompletedOnboarding = true },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var plugin = Path.Combine(Source, "PluginData", Plugin);
        Directory.CreateDirectory(plugin);
        var encrypted = secret ?? Convert.ToBase64String(ProtectedData.Protect("test-key"u8.ToArray(), "TypeWhisper.ApiKey.v1"u8.ToArray(), DataProtectionScope.CurrentUser));
        File.WriteAllText(Path.Combine(plugin, "settings.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        { ["secret:api-key"] = encrypted, ["selectedModel"] = "whisper-large-v3" }));
        File.WriteAllBytes(Path.Combine(Source, "Data", "licenses.dat"), "existing-activation"u8.ToArray());
    }
    private Task<bool> Import(Func<string, string, CancellationToken, Task<bool>>? installer = null,
        CancellationToken cancellation = default) =>
        LegacyDailyProfileMigration.ImportAsync(Source, Destination, cancellationToken: cancellation, prepareProfile: (source, stage, ct) =>
            LegacyWindowsProfileMigration.PrepareAsync(source, stage, new(1, 1, 5), null, ct,
                installer ?? ((_, _, _) => Task.FromResult(true))));
    // Uses the real catalog and package store against a stubbed network.
    private Task<bool> ImportFromCatalog(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        LegacyDailyProfileMigration.ImportAsync(Source, Destination, prepareProfile: (source, stage, ct) =>
            LegacyWindowsProfileMigration.PrepareAsync(source, stage, new(1, 1, 5), null, ct, handler: new StubHandler(respond)));
    private string Report => File.ReadAllText(Path.Combine(Destination, LegacyWindowsProfileMigration.ReportName));
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { await Task.Yield(); var response = respond(request); response.RequestMessage = request; return response; }
    }
    [Fact]
    public async Task SettingsOnlyProfileMigratesSecretsAndLicenseWithoutChangingSource()
    {
        Seed();
        var before = Directory.GetFiles(Source, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        Assert.True(await Import());
        var data = Path.Combine(Destination, "PluginData", Plugin);
        Assert.Equal("test-key", await new WindowsPluginSecretStore(data).LoadAsync("api-key"));
        var settings = File.ReadAllText(Path.Combine(data, "settings.json"));
        Assert.DoesNotContain("secret:", settings); Assert.DoesNotContain("test-key", settings);
        Assert.Equal("existing-activation", File.ReadAllText(Path.Combine(Destination, "licenses.dat")));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(await Import((_, _, _) => throw new Exception("Existing profiles must not install plugins.")));
        var provider = new TypeWhisper.Plugin.Groq.GroqPlugin();
        await provider.ActivateAsync(new TypeWhisper.PluginHost.VocabularyHostServices(data, secrets: new WindowsPluginSecretStore(data)));
        try
        {
            Assert.True(provider.IsConfigured);
            Assert.Equal("whisper-large-v3", provider.SelectedModelId);
        }
        finally { await provider.DeactivateAsync(); }
    }
    [Fact]
    public async Task LicenseOnlyProfileRetainsActivation()
    {
        Directory.CreateDirectory(Path.Combine(Source, "Data"));
        File.WriteAllText(Path.Combine(Source, "Data", "licenses.dat"), "existing-activation");
        Assert.True(await Import((_, _, _) => throw new Exception("A license-only profile must not install plugins.")));
        Assert.Equal("existing-activation", File.ReadAllText(Path.Combine(Destination, "licenses.dat")));
    }
    [Fact]
    public async Task OverlayMigrationPreservesDisabledLiveTextAndWidgetMeaning()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "settings.json"), JsonSerializer.Serialize(new AppSettings
        {
            LiveTranscriptionEnabled = false, LiveTranscriptionFontSize = 16,
            PreviewBubbleAutoHideMilliseconds = 3000, OverlayPosition = OverlayPosition.Top,
            IndicatorStyle = IndicatorStyle.CompactBadge,
            OverlayLeftWidget = TypeWhisper.Core.Models.OverlayWidget.Timer,
            OverlayRightWidget = TypeWhisper.Core.Models.OverlayWidget.Waveform
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.True(await Import());
        var overlay = OverlayPreferencesStore.Read(Path.Combine(Destination, "overlay.json"));
        Assert.False(overlay.LiveText);
        Assert.Equal(16, overlay.LiveTranscriptionFontSize);
        Assert.Equal(3000, overlay.PreviewBubbleAutoHideMilliseconds);
        Assert.Equal(OverlayAnchor.TopCenter, overlay.Anchor);
        Assert.Equal(OverlayMode.Compact, overlay.Mode);
        Assert.Equal(TypeWhisper.WinUI.OverlayWidget.Timer, overlay.Left);
        Assert.Equal(TypeWhisper.WinUI.OverlayWidget.Waveform, overlay.Right);
    }
    [Fact]
    public async Task FailedPluginDownloadStillPublishesProfileWithReportNote()
    {
        Seed();
        var models = Path.Combine(Source, "PluginData", Plugin, "Models");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "weights.onnx"), "model-data");
        Assert.True(await Import((_, _, _) => throw new IOException("offline")));
        var data = Path.Combine(Destination, "PluginData", Plugin);
        Assert.Equal("test-key", await new WindowsPluginSecretStore(data).LoadAsync("api-key"));
        Assert.Equal("model-data", File.ReadAllText(Path.Combine(data, "Models", "weights.onnx")));
        Assert.Contains("could not be downloaded or verified: " + Plugin, Report);
        Assert.Contains(Plugin, JsonDocument.Parse(Report).RootElement.GetProperty("UnavailablePlugins").EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain("test-key", Report);
        Assert.DoesNotContain("offline", Report);
    }
    [Fact]
    public async Task CancellationStillLeavesNoVisibleProfileAndCanRetry()
    {
        Seed();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Import((_, _, _) =>
        { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }, cancellation.Token));
        Assert.False(Directory.Exists(Destination));
        Assert.Empty(Directory.GetDirectories(_root, ".typewhisper-import-*"));
        Assert.True(await Import());
    }
    [Fact]
    public async Task OfflineCatalogStillPublishesProfileWithReportNote()
    {
        Seed();
        Assert.True(await ImportFromCatalog(_ => throw new HttpRequestException("No such host is known.")));
        Assert.Equal("test-key", await new WindowsPluginSecretStore(Path.Combine(Destination, "PluginData", Plugin)).LoadAsync("api-key"));
        Assert.Equal("existing-activation", File.ReadAllText(Path.Combine(Destination, "licenses.dat")));
        Assert.Contains("plugin catalog could not be reached", Report);
        Assert.Empty(JsonDocument.Parse(Report).RootElement.GetProperty("InstalledPlugins").EnumerateArray());
        Assert.True(LegacyDailyProfileMigration.WasImported(Destination));
    }
    [Fact]
    public async Task FailedPackageFromCatalogIsDeferred()
    {
        Seed();
        var catalog = JsonSerializer.Serialize(new
        {
            plugins = new[] { new { id = Plugin, name = "Groq", version = "1.0.0", minHostVersion = "1.1.0", downloadUrl = "https://example.invalid/groq.zip",
                sha256 = new string('0', 64), size = 10, platforms = new[] { "windows" }, supportedArchitectures = new[] { TypeWhisper.PluginHost.PortablePluginCatalog.Architecture } } }
        });
        Assert.True(await ImportFromCatalog(request => request.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal)
            ? new(System.Net.HttpStatusCode.NotFound) : new(System.Net.HttpStatusCode.OK) { Content = new StringContent(catalog) }));
        Assert.Contains("could not be downloaded or verified: " + Plugin, Report);
        Assert.Empty(Directory.GetFiles(Path.Combine(Destination, "PluginPackages"), "*.zip"));
    }
    [Fact]
    public async Task UndecryptableKeyIsReportedAndNeverBecomesAnApiKey()
    {
        var ciphertext = Convert.ToBase64String("not-a-dpapi-blob"u8.ToArray());
        Seed(ciphertext);
        Assert.True(await Import());
        var data = Path.Combine(Destination, "PluginData", Plugin);
        Assert.Null(await new WindowsPluginSecretStore(data).LoadAsync("api-key"));
        Assert.DoesNotContain("secret:", File.ReadAllText(Path.Combine(data, "settings.json")));
        Assert.Contains("Saved API keys for " + Plugin + " could not be decrypted", Report);
        Assert.DoesNotContain(ciphertext, Report);
        Assert.Throws<CryptographicException>(() => LegacyWindowsProfileMigration.Decrypt(ciphertext));
    }
    [Fact]
    public async Task MalformedPluginSettingsAreReportedAndReset()
    {
        Seed();
        File.WriteAllText(Path.Combine(Source, "PluginData", Plugin, "settings.json"), "{\"secret:api-key\":");
        Assert.True(await Import());
        Assert.Contains("Saved settings for " + Plugin + " could not be read", Report);
        Assert.Contains("\"Enabled\":true", File.ReadAllText(Path.Combine(Destination, "PluginData", Plugin, "settings.json")));
    }
    [Fact]
    public async Task InvalidPluginIdentityInSettingsIsSkipped()
    {
        Seed();
        File.WriteAllText(Path.Combine(Source, "settings.json"), JsonSerializer.Serialize(new AppSettings
        { PluginEnabledState = new() { [Plugin] = true, ["..\\escape"] = true } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.True(await Import());
        Assert.Contains("unsupported identifiers were skipped", Report);
        Assert.False(Directory.Exists(Path.Combine(Destination, "escape")));
    }
    [Fact]
    public async Task LinkedLegacyRootIsFollowed()
    {
        Seed();
        var real = Path.Combine(_root, "other-volume");
        Directory.Move(Source, real);
        Link(Source, real);
        Assert.True(await Import());
        Assert.Equal("test-key", await new WindowsPluginSecretStore(Path.Combine(Destination, "PluginData", Plugin)).LoadAsync("api-key"));
    }
    [Fact]
    public async Task LinkedPluginFolderInsideSourceIsRejected()
    {
        Seed();
        var outside = Path.Combine(_root, "outside");
        Directory.Move(Path.Combine(Source, "PluginData", Plugin), outside);
        Link(Path.Combine(Source, "PluginData", Plugin), outside);
        await Assert.ThrowsAsync<LegacyImportLinkException>(() => Import());
        Assert.False(Directory.Exists(Destination));
    }
    private static void Link(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Junctions need no symlink privilege.
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
    }
    [Fact]
    public async Task MissingPluginIsReportedAndSelectionIsNotReplaced()
    {
        Seed();
        Assert.True(await Import((_, _, _) => Task.FromResult(false)));
        Assert.Contains(Plugin, File.ReadAllText(Path.Combine(Destination, "Dictation/settings.json")));
        Assert.Contains("no compatible plugin", File.ReadAllText(Path.Combine(Destination, LegacyWindowsProfileMigration.ReportName)));
    }
    [Fact]
    public async Task ModelsAreCopiedWithoutExecutingOrCopyingOldRuntimeBinaries()
    {
        Seed();
        var models = Path.Combine(Source, "PluginData", Plugin, "Models", "model");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "weights.onnx"), "model-data");
        File.WriteAllText(Path.Combine(models, "legacy.dll"), "old-runtime");
        Assert.True(await Import());
        Assert.Equal("model-data", File.ReadAllText(Path.Combine(Destination, "PluginData", Plugin, "Models", "model", "weights.onnx")));
        Assert.False(File.Exists(Path.Combine(Destination, "PluginData", Plugin, "Models", "model", "legacy.dll")));
        Assert.True(File.Exists(Path.Combine(models, "legacy.dll")));
    }
    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        // Unlink first: recursive deletion reports access denied for junctions.
        foreach (var link in Directory.GetDirectories(_root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0).ToArray()) Directory.Delete(link);
        Directory.Delete(_root, true);
    }
}
