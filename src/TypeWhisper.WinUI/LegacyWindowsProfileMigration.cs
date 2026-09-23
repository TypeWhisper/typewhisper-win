using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginHost;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Runs only against the importer's unpublished staging directory, before any live stores or plugins start.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class LegacyWindowsProfileMigration
{
    internal const string ReportName = "legacy-migration-report.json";
    private static readonly byte[] LegacyEntropy = "TypeWhisper.ApiKey.v1"u8.ToArray();

    internal static async Task PrepareAsync(string source, string stage, Version hostVersion,
        Action<string>? progress, CancellationToken ct,
        Func<string, string, CancellationToken, Task<bool>>? install = null)
    {
        var notes = new List<string>();
        var settingsPath = Path.Combine(source, "settings.json");
        AppSettings? settings = null;
        if (File.Exists(settingsPath))
        {
            settings = SettingsService.ParseForMigration(Encoding.UTF8.GetString(Read(settingsPath)));
            LegacyApplicationSettings.Write(stage, settings);
            // Match widget names rather than numeric values: Timer and Waveform changed enum positions.
            OverlayPreferencesStore.Save(Path.Combine(stage, "overlay.json"), new(
                settings.IndicatorStyle switch
                {
                    IndicatorStyle.CompactBadge => OverlayMode.Compact,
                    IndicatorStyle.EdgeDock => OverlayMode.Minimal,
                    _ => OverlayMode.Standard
                }, settings.LiveTranscriptionEnabled, false,
                settings.OverlayPosition == OverlayPosition.Top ? OverlayAnchor.TopCenter : OverlayAnchor.BottomCenter,
                Enum.Parse<OverlayWidget>(settings.OverlayLeftWidget.ToString()),
                Enum.Parse<OverlayWidget>(settings.OverlayRightWidget.ToString()),
                AppSettings.NormalizeLiveTranscriptionFontSize(settings.LiveTranscriptionFontSize),
                AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(settings.PreviewBubbleAutoHideMilliseconds)));
        }
        // The license format and user-scoped DPAPI entropy are unchanged. Activation IDs are preserved.
        foreach (var name in new[] { "licenses.dat", "license.json" })
        {
            var path = Path.Combine(source, "Data", name);
            if (File.Exists(path)) await File.WriteAllBytesAsync(Path.Combine(stage, name), Read(path), ct);
        }

        var ids = new HashSet<string>(settings?.PluginEnabledState.Keys.AsEnumerable() ?? [], StringComparer.Ordinal);
        var pluginData = Path.Combine(source, "PluginData");
        var legacyPackages = Path.Combine(source, "Plugins");
        foreach (var root in new[] { pluginData, legacyPackages })
        {
            RejectLinks(root);
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                RejectLinks(directory);
                var id = Path.GetFileName(directory);
                if (ValidId(id)) ids.Add(id);
            }
        }
        var selected = settings is null ? null : LegacyApplicationSettings.SelectedModel(settings);
        if (selected is { } selection) ids.Add(selection.PluginId);
        if (!string.IsNullOrEmpty(settings?.GroqApiKey)) ids.Add("com.typewhisper.groq");
        if (!string.IsNullOrEmpty(settings?.OpenAiApiKey)) ids.Add("com.typewhisper.openai");
        if (ids.Any(id => !ValidId(id))) throw new InvalidDataException("Invalid legacy plugin identity.");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var store = new PortablePluginStore(Path.Combine(stage, "PluginPackages"), hostVersion, http);
        IReadOnlyList<PortableCatalogEntry>? catalog = null;
        if (ids.Count > 0 && install is null)
        {
            progress?.Invoke("Finding compatible plugins…");
            catalog = await new PortablePluginCatalog(http).FetchAsync(ct);
            await store.InitializeAsync(ct: ct);
        }
        var installed = new List<string>();
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke("Migrating " + id + "…");
            var data = Path.Combine(stage, "PluginData", id);
            Directory.CreateDirectory(data);
            var oldSettings = Path.Combine(pluginData, id, "settings.json");
            var values = File.Exists(oldSettings) ? ParseSettings(Read(oldSettings)) : new Dictionary<string, JsonElement>();
            var secrets = new WindowsPluginSecretStore(data);
            foreach (var key in values.Keys.Where(key => key.StartsWith("secret:", StringComparison.Ordinal)).ToArray())
            {
                var encrypted = values[key].GetString();
                if (!string.IsNullOrEmpty(encrypted))
                    await secrets.StoreAsync(key[7..], Decrypt(encrypted));
                values.Remove(key);
            }
            var legacyKey = id == "com.typewhisper.groq" ? settings?.GroqApiKey : id == "com.typewhisper.openai" ? settings?.OpenAiApiKey : null;
            if (!string.IsNullOrEmpty(legacyKey) && await secrets.LoadAsync("api-key") is null)
                await secrets.StoreAsync("api-key", Decrypt(legacyKey));
            values["Enabled"] = JsonSerializer.SerializeToElement(settings?.PluginEnabledState.GetValueOrDefault(id, true) ?? true);
            if (selected is { } active && active.PluginId == id)
            {
                values["SelectedModelId"] = JsonSerializer.SerializeToElement(active.ModelId);
                values["Language"] = JsonSerializer.SerializeToElement(settings!.Language);
            }
            await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(values), ct);

            bool available;
            if (install is not null) available = await install(id, stage, ct);
            else
            {
                var entry = catalog!.SingleOrDefault(item => item.Id == id && item.Supports(hostVersion, PortablePluginCatalog.Architecture));
                available = entry is not null;
                if (entry is not null) await store.InstallAsync(entry, ct: ct);
            }
            if (!available)
            {
                notes.Add(id + ": no compatible plugin is available. Its saved configuration was preserved; install a replacement in Integrations.");
                continue;
            }
            installed.Add(id);
            // Copy model assets, never legacy plugin binaries or shared writable directories.
            var assets = string.IsNullOrWhiteSpace(settings?.LocalModelStoragePath) ? Path.Combine(pluginData, id)
                : Path.Combine(settings.LocalModelStoragePath, "PluginData", id);
            var models = Path.Combine(assets, "Models");
            if (Directory.Exists(models)) await CopyModelsAsync(models, Path.Combine(data, "Models"), ct);
            if (id == "com.typewhisper.file-memory")
            {
                var memories = Path.Combine(pluginData, id, "memories.json");
                if (File.Exists(memories)) await File.WriteAllBytesAsync(Path.Combine(data, "memories.json"), Read(memories), ct);
            }
        }
        if (settings is not null && selected is null && settings.SelectedModelId is not null)
            notes.Add("The previous model selection could not be mapped. Select a model in Dictation.");
        notes.Add("History was imported as text. Archived audio, recordings, recovery audio and account sign-ins remain in the previous profile.");
        await File.WriteAllTextAsync(Path.Combine(stage, ReportName), JsonSerializer.Serialize(new
        { Version = 1, InstalledPlugins = installed, Notes = notes }), ct);
    }

    private static bool ValidId(string id) => Regex.IsMatch(id, "^[a-z0-9]+(?:[.-][a-z0-9]+)+$");
    private static Dictionary<string, JsonElement> ParseSettings(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject()
            .GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException("Invalid legacy plugin settings.");
        return document.RootElement.Deserialize<Dictionary<string, JsonElement>>()!;
    }
    internal static string Decrypt(string encrypted)
    {
        byte[] ciphertext;
        try { ciphertext = Convert.FromBase64String(encrypted); }
        catch (FormatException) { return encrypted; } // Very early versions stored plain API keys.
        // A DPAPI failure is not a plaintext key. Stop the import rather than storing ciphertext as a credential.
        var plaintext = ProtectedData.Unprotect(ciphertext, LegacyEntropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    private static byte[] Read(string path)
    {
        RejectLinks(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Legacy configuration exceeds the import limit.");
        var result = new byte[checked((int)stream.Length)]; stream.ReadExactly(result); return result;
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Legacy migration does not follow linked paths.");
    }
    private static async Task CopyModelsAsync(string source, string destination, CancellationToken ct)
    {
        RejectLinks(source);
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested(); RejectLinks(path);
            var target = Path.Combine(destination, Path.GetFileName(path));
            if (Directory.Exists(path)) await CopyModelsAsync(path, target, ct);
            else if (Path.GetExtension(path).ToLowerInvariant() is not (".exe" or ".dll" or ".ps1" or ".bat" or ".cmd" or ".py"))
            {
                await using var input = File.OpenRead(path);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, ct);
            }
        }
    }
}
