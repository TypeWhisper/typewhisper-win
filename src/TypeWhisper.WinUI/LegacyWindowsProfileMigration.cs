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
        Func<string, string, CancellationToken, Task<bool>>? install = null, HttpMessageHandler? handler = null)
    {
        var notes = new List<string>();
        var settingsPath = Path.Combine(source, "settings.json");
        AppSettings? settings = null;
        if (File.Exists(settingsPath))
        {
            // Core settings stay strict: a partial conversion would silently change dictation behavior.
            settings = SettingsService.ParseForMigration(Encoding.UTF8.GetString(Read(settingsPath, source)));
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
            if (File.Exists(path)) await File.WriteAllBytesAsync(Path.Combine(stage, name), Read(path, source), ct);
        }

        var ids = new HashSet<string>(settings?.PluginEnabledState.Keys.AsEnumerable() ?? [], StringComparer.Ordinal);
        var pluginData = Path.Combine(source, "PluginData");
        var legacyPackages = Path.Combine(source, "Plugins");
        foreach (var root in new[] { pluginData, legacyPackages })
        {
            RejectLinks(root, source);
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                RejectLinks(directory, source);
                var id = Path.GetFileName(directory);
                if (ValidId(id)) ids.Add(id);
            }
        }
        var selected = settings is null ? null : LegacyApplicationSettings.SelectedModel(settings);
        if (selected is { } selection) ids.Add(selection.PluginId);
        if (!string.IsNullOrEmpty(settings?.GroqApiKey)) ids.Add("com.typewhisper.groq");
        if (!string.IsNullOrEmpty(settings?.OpenAiApiKey)) ids.Add("com.typewhisper.openai");
        // Identities become directory names; anything else is skipped rather than blocking the upgrade.
        if (ids.RemoveWhere(id => !ValidId(id)) > 0) notes.Add("Plugin entries with unsupported identifiers were skipped.");
        // A configured external model location is followed like the legacy root; links below it are not.
        var external = string.IsNullOrWhiteSpace(settings?.LocalModelStoragePath) ? null
            : LegacyDailyProfileMigration.ResolveLinkedPath(settings.LocalModelStoragePath);

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(10);
        var store = new PortablePluginStore(Path.Combine(stage, "PluginPackages"), hostVersion, http);
        IReadOnlyList<PortableCatalogEntry>? catalog = null;
        var offline = false;
        if (ids.Count > 0 && install is null)
        {
            progress?.Invoke("Finding compatible plugins…");
            // Downloads are best-effort: plugins can be installed later in Integrations. Only cancellation aborts.
            try
            {
                catalog = await new PortablePluginCatalog(http).FetchAsync(ct);
                await store.InitializeAsync(ct: ct);
            }
            catch (Exception ex) when (Recoverable(ex, ct)) { offline = true; }
        }
        List<string> installed = [], unavailable = [], deferred = [];
        SortedSet<string> keys = new(StringComparer.Ordinal), unreadable = new(StringComparer.Ordinal);
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke("Migrating " + id + "…");
            var data = Path.Combine(stage, "PluginData", id);
            Directory.CreateDirectory(data);
            var oldSettings = Path.Combine(pluginData, id, "settings.json");
            var values = new Dictionary<string, JsonElement>();
            // Links still stop the import (IOException); damaged content only resets this plugin.
            if (File.Exists(oldSettings))
                try { values = ParseSettings(Read(oldSettings, source)); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { unreadable.Add(id); }
            var secrets = new WindowsPluginSecretStore(data);
            foreach (var key in values.Keys.Where(key => key.StartsWith("secret:", StringComparison.Ordinal)).ToArray())
            {
                var value = values[key];
                values.Remove(key);
                if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String && value.GetString() is "") continue;
                if (value.ValueKind == JsonValueKind.String && TryDecrypt(value.GetString()!) is { } plaintext)
                    await secrets.StoreAsync(key[7..], plaintext);
                else keys.Add(id);
            }
            var legacyKey = id == "com.typewhisper.groq" ? settings?.GroqApiKey : id == "com.typewhisper.openai" ? settings?.OpenAiApiKey : null;
            if (!string.IsNullOrEmpty(legacyKey) && await secrets.LoadAsync("api-key") is null)
            {
                if (TryDecrypt(legacyKey) is { } plaintext) await secrets.StoreAsync("api-key", plaintext);
                else keys.Add(id);
            }
            values["Enabled"] = JsonSerializer.SerializeToElement(settings?.PluginEnabledState.GetValueOrDefault(id, true) ?? true);
            if (selected is { } active && active.PluginId == id)
            {
                values["SelectedModelId"] = JsonSerializer.SerializeToElement(active.ModelId);
                values["Language"] = JsonSerializer.SerializeToElement(settings!.Language);
            }
            await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(values), ct);

            bool? available = null; // null: catalog, download or package validation failed; retry later.
            if (!offline)
            {
                try
                {
                    if (install is not null) available = await install(id, stage, ct);
                    else if (catalog!.SingleOrDefault(item => item.Id == id && item.Supports(hostVersion, PortablePluginCatalog.Architecture)) is { } entry)
                    { await store.InstallAsync(entry, ct: ct); available = true; }
                    else available = false;
                }
                catch (Exception ex) when (Recoverable(ex, ct)) { }
            }
            if (available == false)
            {
                unavailable.Add(id);
                notes.Add(id + ": no compatible plugin is available. Its saved configuration was preserved; install a replacement in Integrations.");
                continue;
            }
            (available == true ? installed : deferred).Add(id);
            // Deferred plugins keep their models so a later install from Integrations finds them.
            // Copy model assets, never legacy plugin binaries or shared writable directories.
            var assets = external is null ? Path.Combine(pluginData, id) : Path.Combine(external, "PluginData", id);
            var models = Path.Combine(assets, "Models");
            if (Directory.Exists(models))
            {
                RejectLinks(models, external ?? source);
                await CopyModelsAsync(models, Path.Combine(data, "Models"), ct);
            }
            if (id == "com.typewhisper.file-memory")
            {
                var memories = Path.Combine(pluginData, id, "memories.json");
                if (File.Exists(memories)) await File.WriteAllBytesAsync(Path.Combine(data, "memories.json"), Read(memories, source), ct);
            }
        }
        if (deferred.Count > 0)
            notes.Add((offline ? "The plugin catalog could not be reached, so these plugins were not installed: "
                : "These plugins could not be downloaded or verified: ") + string.Join(", ", deferred) +
                ". Their settings and models were preserved; install them in Integrations.");
        if (keys.Count > 0)
            notes.Add("Saved API keys for " + string.Join(", ", keys) + " could not be decrypted for this Windows user. Enter them again in Integrations.");
        if (unreadable.Count > 0)
            notes.Add("Saved settings for " + string.Join(", ", unreadable) + " could not be read and were reset.");
        if (settings is not null && selected is null && settings.SelectedModelId is not null)
            notes.Add("The previous model selection could not be mapped. Select a model in Dictation.");
        notes.Add("History was imported as text. Archived audio, recordings, recovery audio and account sign-ins remain in the previous profile.");
        // Plugin identifiers and fixed notes only: never keys, exception text or transcript content.
        await File.WriteAllTextAsync(Path.Combine(stage, ReportName), JsonSerializer.Serialize(new
        {
            Version = 1, InstalledPlugins = installed,
            UnavailablePlugins = unavailable.Concat(deferred).Order(StringComparer.Ordinal), Notes = notes
        }), ct);
    }

    private static bool Recoverable(Exception error, CancellationToken ct) =>
        !ct.IsCancellationRequested && error is not (OutOfMemoryException or LegacyImportLinkException);
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
        // A DPAPI failure is not a plaintext key; never store ciphertext as a credential.
        var plaintext = ProtectedData.Unprotect(ciphertext, LegacyEntropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    // Keys encrypted for another Windows user or machine are reported and must be entered again.
    private static string? TryDecrypt(string encrypted)
    {
        try { return Decrypt(encrypted); }
        catch (CryptographicException) { return null; }
    }
    private static byte[] Read(string path, string root)
    {
        RejectLinks(path, root);
        using var stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Legacy configuration exceeds the import limit.");
        var result = new byte[checked((int)stream.Length)]; stream.ReadExactly(result); return result;
    }
    // The importer passes a resolved root; only links inside it are rejected.
    private static void RejectLinks(string path, string root) => LegacyDailyProfileMigration.RejectLinks(path, root);
    private static async Task CopyModelsAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested(); RejectLinks(path, source);
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
