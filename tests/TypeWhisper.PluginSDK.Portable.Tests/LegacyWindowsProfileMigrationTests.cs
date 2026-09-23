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
    private Task<bool> Import(Func<string, string, CancellationToken, Task<bool>>? installer = null) =>
        LegacyDailyProfileMigration.ImportAsync(Source, Destination, prepareProfile: (source, stage, ct) =>
            LegacyWindowsProfileMigration.PrepareAsync(source, stage, new(1, 1, 5), null, ct,
                installer ?? ((_, _, _) => Task.FromResult(true))));
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
    public async Task InterruptedPluginDownloadLeavesNoVisibleProfileAndCanRetry()
    {
        Seed();
        await Assert.ThrowsAsync<IOException>(() => Import((_, _, _) => throw new IOException("offline")));
        Assert.False(Directory.Exists(Destination));
        Assert.True(await Import());
    }
    [Fact]
    public async Task DamagedCiphertextNeverBecomesAnApiKey()
    {
        Seed(Convert.ToBase64String("not-a-dpapi-blob"u8.ToArray()));
        await Assert.ThrowsAsync<CryptographicException>(() => Import());
        Assert.False(Directory.Exists(Destination));
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
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
