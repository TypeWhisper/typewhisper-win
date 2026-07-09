using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// Fetches the plugin registry from GitHub, manages installation, uninstallation,.
/// and update checking for marketplace plugins.
/// </summary>
public sealed class PluginRegistryService
{
    private const string RegistryUrl = "https://typewhisper.github.io/typewhisper-win/plugins.json";
    private const string PendingUpdatesDirectoryName = ".pending-updates";
    private const string PendingUninstallsDirectoryName = ".pending-uninstalls";
    private const string StagingDirectoryName = ".staging";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly PluginManager _pluginManager;
    private readonly PluginLoader _pluginLoader;
    private readonly ISettingsService _settings;
    private readonly HttpClient _httpClient;
    private readonly string _pluginsPath;
    private readonly string _bundledPluginsPath;
    private readonly string _pendingUpdatesPath;
    private readonly string _pendingUninstallsPath;
    private readonly Func<string, string, CancellationToken, Task> _replaceActiveDirectoryAsync;
    private readonly Func<string, CancellationToken, Task> _deleteActiveDirectoryAsync;
    private readonly AppDistributionKind _distributionKind;

    private List<RegistryPlugin>? _cachedRegistry;
    private DateTime _cacheTimestamp;
    private DateTime _lastUpdateCheck;

    /// <summary>
    /// Initializes a new instance of the PluginRegistryService class.
    /// </summary>
    public PluginRegistryService(
        PluginManager pluginManager,
        PluginLoader pluginLoader,
        ISettingsService settings,
        HttpClient? httpClient = null,
        string? pluginsPath = null,
        Func<string, string, CancellationToken, Task>? replaceActiveDirectoryAsync = null,
        Func<string, CancellationToken, Task>? deleteActiveDirectoryAsync = null,
        string? bundledPluginsPath = null,
        AppDistributionKind? distributionKind = null)
    {
        _pluginManager = pluginManager;
        _pluginLoader = pluginLoader;
        _settings = settings;
        _httpClient = httpClient ?? new HttpClient();
        _distributionKind = distributionKind ?? AppDistribution.Current;
        _pluginsPath = Path.GetFullPath(pluginsPath ?? TypeWhisperEnvironment.PluginsPath);
        _bundledPluginsPath = Path.GetFullPath(bundledPluginsPath ?? Path.Join(AppContext.BaseDirectory, "Plugins"));
        _pendingUpdatesPath = GetValidatedChildDirectory(_pluginsPath, PendingUpdatesDirectoryName, "pending updates directory");
        _pendingUninstallsPath = GetValidatedChildDirectory(_pluginsPath, PendingUninstallsDirectoryName, "pending uninstalls directory");
        _replaceActiveDirectoryAsync = replaceActiveDirectoryAsync ?? ReplaceActiveDirectoryAsync;
        _deleteActiveDirectoryAsync = deleteActiveDirectoryAsync ?? DeleteActiveDirectoryAsync;
    }

    /// <summary>
    /// Fetches the plugin registry from the remote URL. Results are cached for 5 minutes.
    /// Filters out plugins whose MinHostVersion exceeds the current host version.
    /// </summary>
    public async Task<IReadOnlyList<RegistryPlugin>> FetchRegistryAsync(CancellationToken ct = default)
    {
        if (_cachedRegistry is not null && DateTime.UtcNow - _cacheTimestamp < CacheDuration)
            return _cachedRegistry;

        try
        {
            var json = await _httpClient.GetStringAsync(RegistryUrl, ct);
            var allPlugins = JsonSerializer.Deserialize<List<RegistryPlugin>>(json, JsonOptions) ?? [];

            var hostVersion = GetHostVersion();
            _cachedRegistry = allPlugins
                .Where(p => IsCompatible(p.MinHostVersion, hostVersion))
                .ToList();
            _cacheTimestamp = DateTime.UtcNow;

            Debug.WriteLine($"[PluginRegistry] Fetched {_cachedRegistry.Count} compatible plugin(s) from registry");
            return _cachedRegistry;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginRegistry] Failed to fetch registry: {ex.Message}");
            return _cachedRegistry ?? [];
        }
    }

    /// <summary>
    /// Determines the install state of a registry plugin by comparing it with locally loaded plugins.
    /// </summary>
    public PluginInstallState GetInstallState(RegistryPlugin registryPlugin)
    {
        var pendingUninstallDir = GetValidatedPendingUninstallDirectory(registryPlugin.Id);
        var pendingDir = GetValidatedPendingDirectory(registryPlugin.Id);
        var pluginDir = GetValidatedPluginDirectory(registryPlugin.Id);

        if (Directory.Exists(pendingUninstallDir))
            return PluginInstallState.PendingRestart;

        var pendingManifest = ReadManifest(pendingDir);
        if (ManifestMatchesRegistry(pendingManifest, registryPlugin))
            return PluginInstallState.PendingRestart;

        var local = _pluginManager.GetPlugin(registryPlugin.Id);
        var localVersion = local?.Manifest.Version;
        var diskManifest = ReadManifest(pluginDir);
        if (ManifestIdMatches(diskManifest, registryPlugin.Id))
            localVersion = SelectNewestVersion(localVersion, diskManifest!.Version);

        if (localVersion is null)
            return PluginInstallState.NotInstalled;

        // Compare versions
        if (Version.TryParse(registryPlugin.Version, out var remoteVer) &&
            Version.TryParse(localVersion, out var localVer) &&
            remoteVer > localVer)
        {
            return PluginInstallState.UpdateAvailable;
        }

        return PluginInstallState.Installed;
    }

    /// <summary>
    /// Downloads and installs a plugin from the registry.
    /// </summary>
    public async Task<PluginInstallResult> InstallPluginAsync(
        RegistryPlugin registryPlugin,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_pluginsPath);
        Directory.CreateDirectory(_pendingUpdatesPath);
        Directory.CreateDirectory(_pendingUninstallsPath);

        ValidatePluginId(registryPlugin.Id);
        var pluginDir = GetValidatedPluginDirectory(registryPlugin.Id);
        var stagingRoot = GetValidatedChildDirectory(_pluginsPath, StagingDirectoryName, "plugin staging directory");
        var stagingDir = GetValidatedChildDirectory(stagingRoot, $"{registryPlugin.Id}-{Guid.NewGuid():N}", "plugin staging instance directory");
        var tempZip = Path.GetTempFileName();

        try
        {
            Directory.CreateDirectory(stagingRoot);

            using (var response = await _httpClient.GetAsync(
                       registryPlugin.DownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead,
                       ct))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? registryPlugin.Size;
                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = File.Create(tempZip);

                var buffer = new byte[8192];
                long bytesRead = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    bytesRead += read;
                    progress?.Report(totalBytes > 0 ? (double)bytesRead / totalBytes : 0);
                }
            }

            VerifyDownloadedPackage(registryPlugin, tempZip);
            ZipFile.ExtractToDirectory(tempZip, stagingDir, overwriteFiles: true);

            // Unblock downloaded files
            PluginLoader.UnblockDirectory(stagingDir);
            ValidateStagedPlugin(registryPlugin, stagingDir);

            // Unload existing version if present
            if (_pluginManager.GetPlugin(registryPlugin.Id) is not null)
            {
                await _pluginManager.UnloadPluginAsync(registryPlugin.Id);
                CollectUnloadedPluginContexts();
            }

            await ClearPendingUninstallAsync(registryPlugin.Id, ct);

            try
            {
                await _replaceActiveDirectoryAsync(stagingDir, pluginDir, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await QueuePendingUpdateAsync(registryPlugin.Id, stagingDir, ct);
                Debug.WriteLine($"[PluginRegistry] Queued plugin update pending restart: {registryPlugin.Id} ({ex.Message})");
                return PluginInstallResult.PendingRestart;
            }

            DeleteDirectoryIfExists(GetValidatedPendingDirectory(registryPlugin.Id));

            await _pluginManager.LoadPluginFromDirectoryAsync(pluginDir, activate: true);
            if (_pluginManager.GetPlugin(registryPlugin.Id) is null)
                return PluginInstallResult.PendingRestart;

            Debug.WriteLine($"[PluginRegistry] Installed plugin: {registryPlugin.Id} v{registryPlugin.Version}");
            return PluginInstallResult.Installed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginRegistry] Failed to install {registryPlugin.Id}: {ex.Message}");
            throw;
        }
        finally
        {
            DeleteFileIfExists(tempZip);
            DeleteDirectoryIfExists(stagingDir);
        }
    }

    /// <summary>
    /// Uninstalls a plugin by unloading it and deleting its directory.
    /// </summary>
    public async Task<PluginUninstallResult> UninstallPluginAsync(
        string pluginId,
        CancellationToken ct = default)
    {
        ValidatePluginId(pluginId);
        await _pluginManager.UnloadPluginAsync(pluginId);
        CollectUnloadedPluginContexts();

        try
        {
            await DeletePendingUpdateDirectoryAsync(pluginId, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            QueuePendingUninstall(pluginId);
            Debug.WriteLine($"[PluginRegistry] Queued plugin uninstall pending restart after pending update cleanup failed: {pluginId} ({ex.Message})");
            return PluginUninstallResult.PendingRestart;
        }

        try
        {
            await DeleteInstalledPluginDirectoriesAsync(pluginId, ct);
            Debug.WriteLine($"[PluginRegistry] Uninstalled plugin: {pluginId}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            QueuePendingUninstall(pluginId);
            Debug.WriteLine($"[PluginRegistry] Queued plugin uninstall pending restart: {pluginId} ({ex.Message})");
            return PluginUninstallResult.PendingRestart;
        }

        await ClearPendingUninstallAsync(pluginId, ct);
        return PluginUninstallResult.Uninstalled;
    }

    /// <summary>
    /// Applies plugin updates that were staged because the running app could not replace loaded files.
    /// </summary>
    public async Task ApplyPendingUpdatesAsync(CancellationToken ct = default)
    {
        await ApplyPendingUninstallsAsync(ct);

        if (!Directory.Exists(_pendingUpdatesPath))
            return;

        foreach (var pendingDir in Directory.GetDirectories(_pendingUpdatesPath))
        {
            var pluginId = Path.GetFileName(pendingDir);
            if (!IsValidPluginId(pluginId))
            {
                Debug.WriteLine($"[PluginRegistry] Skipping invalid pending update directory: {pendingDir}");
                continue;
            }

            if (Directory.Exists(GetValidatedPendingUninstallDirectory(pluginId)))
            {
                Debug.WriteLine($"[PluginRegistry] Skipping pending update with pending uninstall: {pluginId}");
                continue;
            }

            var manifest = ReadManifest(pendingDir);
            if (!ManifestIdMatches(manifest, pluginId))
            {
                Debug.WriteLine($"[PluginRegistry] Skipping invalid pending update directory: {pendingDir}");
                continue;
            }

            var pluginDir = GetValidatedPluginDirectory(pluginId);
            try
            {
                await _replaceActiveDirectoryAsync(pendingDir, pluginDir, ct);
                Debug.WriteLine($"[PluginRegistry] Applied pending plugin update: {pluginId} v{manifest!.Version}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[PluginRegistry] Failed to apply pending update for {pluginId}: {ex.Message}");
            }
        }
    }

    private async Task ApplyPendingUninstallsAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_pendingUninstallsPath))
            return;

        foreach (var pendingDir in Directory.GetDirectories(_pendingUninstallsPath))
        {
            var pluginId = Path.GetFileName(pendingDir);
            if (!IsValidPluginId(pluginId))
            {
                Debug.WriteLine($"[PluginRegistry] Skipping invalid pending uninstall directory: {pendingDir}");
                continue;
            }

            try
            {
                await DeletePendingUpdateDirectoryAsync(pluginId, ct);
                await DeleteInstalledPluginDirectoriesAsync(pluginId, ct);

                await ClearPendingUninstallAsync(pluginId, ct);
                Debug.WriteLine($"[PluginRegistry] Applied pending plugin uninstall: {pluginId}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[PluginRegistry] Failed to apply pending uninstall for {pluginId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Checks for available plugin updates. Respects a 24-hour interval.
    /// </summary>
    public async Task CheckForUpdatesAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastUpdateCheck < UpdateCheckInterval)
            return;

        _lastUpdateCheck = DateTime.UtcNow;

        var registry = await FetchRegistryAsync(ct);
        var updatesAvailable = registry
            .Where(p => GetInstallState(p) == PluginInstallState.UpdateAvailable)
            .ToList();

        if (updatesAvailable.Count > 0)
        {
            Debug.WriteLine($"[PluginRegistry] {updatesAvailable.Count} plugin update(s) available");
        }
    }

    /// <summary>
    /// Marks first-run plugin setup as complete without installing marketplace plugins by default.
    /// </summary>
    public Task FirstRunAutoInstallAsync(CancellationToken ct = default)
    {
        if (_settings.Current.PluginFirstRunCompleted)
            return Task.CompletedTask;

        if (_distributionKind == AppDistributionKind.Store)
        {
            Debug.WriteLine("[PluginRegistry] Store distribution skips first-run plugin auto-install.");
        }
        else
        {
            Debug.WriteLine("[PluginRegistry] First run detected; marketplace plugin auto-install is disabled.");
        }

        _settings.Save(_settings.Current with { PluginFirstRunCompleted = true });
        return Task.CompletedTask;
    }

    private void VerifyDownloadedPackage(RegistryPlugin registryPlugin, string packagePath)
    {
        if (_distributionKind != AppDistributionKind.Store)
            return;

        if (string.IsNullOrWhiteSpace(registryPlugin.Sha256))
            throw new InvalidOperationException("Store plugin packages must include a SHA-256 hash.");

        var expectedHash = NormalizeSha256(registryPlugin.Sha256);
        var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Store plugin package SHA-256 hash does not match the registry entry.");
    }

    private static string NormalizeSha256(string sha256)
    {
        var normalized = sha256.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Store plugin package SHA-256 hash is invalid.");

        return normalized;
    }

    private static Version GetHostVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        return asm?.GetName().Version ?? new Version(1, 0);
    }

    private static bool IsCompatible(string? minHostVersion, Version hostVersion)
    {
        if (string.IsNullOrEmpty(minHostVersion))
            return true;

        return !Version.TryParse(minHostVersion, out var minVer) || hostVersion >= minVer;
    }

    private static void ValidateStagedPlugin(RegistryPlugin registryPlugin, string stagingDir)
    {
        var manifest = ReadManifest(stagingDir)
            ?? throw new InvalidOperationException("The downloaded plugin package does not contain a valid manifest.json.");

        if (!string.Equals(manifest.Id, registryPlugin.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded plugin package id does not match the registry entry.");

        if (!string.Equals(manifest.Version, registryPlugin.Version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded plugin package version does not match the registry entry.");
    }

    private async Task QueuePendingUpdateAsync(string pluginId, string stagingDir, CancellationToken ct)
    {
        await ClearPendingUninstallAsync(pluginId, ct);

        var pendingDir = GetValidatedPendingDirectory(pluginId);
        DeleteDirectoryIfExists(pendingDir);
        Directory.CreateDirectory(_pendingUpdatesPath);
        Directory.Move(stagingDir, pendingDir);
    }

    private void QueuePendingUninstall(string pluginId)
    {
        var pendingDir = GetValidatedPendingUninstallDirectory(pluginId);
        Directory.CreateDirectory(_pendingUninstallsPath);
        Directory.CreateDirectory(pendingDir);
    }

    private async Task DeletePendingUpdateDirectoryAsync(string pluginId, CancellationToken ct)
    {
        var pendingDir = GetValidatedPendingDirectory(pluginId);
        if (!Directory.Exists(pendingDir))
            return;

        await _deleteActiveDirectoryAsync(pendingDir, ct);
    }

    private async Task DeleteInstalledPluginDirectoriesAsync(string pluginId, CancellationToken ct)
    {
        foreach (var pluginDir in GetInstalledPluginDirectories(pluginId))
        {
            if (Directory.Exists(pluginDir))
                await _deleteActiveDirectoryAsync(pluginDir, ct);
        }
    }

    private async Task ClearPendingUninstallAsync(string pluginId, CancellationToken ct)
    {
        var pendingDir = GetValidatedPendingUninstallDirectory(pluginId);
        if (!Directory.Exists(pendingDir))
            return;

        await _deleteActiveDirectoryAsync(pendingDir, ct);
        if (Directory.Exists(pendingDir))
            throw new IOException($"Failed to clear pending uninstall marker for {pluginId}.");
    }

    private static Task DeleteActiveDirectoryAsync(string targetDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(targetDirectory))
            Directory.Delete(targetDirectory, recursive: true);

        return Task.CompletedTask;
    }

    private static async Task ReplaceActiveDirectoryAsync(
        string sourceDirectory,
        string targetDirectory,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var backupDirectory = targetDirectory + ".replacing-" + Guid.NewGuid().ToString("N");

        try
        {
            if (Directory.Exists(targetDirectory))
                Directory.Move(targetDirectory, backupDirectory);

            Directory.Move(sourceDirectory, targetDirectory);
            DeleteDirectoryIfExists(backupDirectory);
        }
        catch
        {
            if (!Directory.Exists(targetDirectory) && Directory.Exists(backupDirectory))
                Directory.Move(backupDirectory, targetDirectory);

            throw;
        }

        await Task.CompletedTask;
    }

    private static PluginManifest? ReadManifest(string pluginDir)
    {
        var manifestPath = Path.Combine(pluginDir, "manifest.json");
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginRegistry] Failed to read manifest at {manifestPath}: {ex.Message}");
            return null;
        }
    }

    private static bool ManifestMatchesRegistry(PluginManifest? manifest, RegistryPlugin registryPlugin) =>
        ManifestIdMatches(manifest, registryPlugin.Id) &&
        string.Equals(manifest!.Version, registryPlugin.Version, StringComparison.OrdinalIgnoreCase);

    private static bool ManifestIdMatches(PluginManifest? manifest, string pluginId) =>
        manifest is not null &&
        string.Equals(manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase);

    private static string SelectNewestVersion(string? first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return second;

        if (Version.TryParse(first, out var firstVersion) &&
            Version.TryParse(second, out var secondVersion))
        {
            return secondVersion > firstVersion ? second : first;
        }

        return first;
    }

    private static void CollectUnloadedPluginContexts()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private string GetValidatedPluginDirectory(string pluginId)
    {
        ValidatePluginId(pluginId);
        return GetValidatedChildDirectory(_pluginsPath, pluginId, "plugin directory");
    }

    private IReadOnlyList<string> GetInstalledPluginDirectories(string pluginId)
    {
        var directories = new List<string>();
        AddDistinctDirectory(GetValidatedPluginDirectory(pluginId));
        AddDistinctDirectory(GetValidatedBundledPluginDirectory(pluginId));
        return directories;

        void AddDistinctDirectory(string path)
        {
            if (!directories.Any(existing => PathsEqual(existing, path)))
                directories.Add(path);
        }
    }

    private string GetValidatedBundledPluginDirectory(string pluginId)
    {
        ValidatePluginId(pluginId);
        return GetValidatedChildDirectory(_bundledPluginsPath, pluginId, "bundled plugin directory");
    }

    private string GetValidatedPendingDirectory(string pluginId)
    {
        ValidatePluginId(pluginId);
        return GetValidatedChildDirectory(_pendingUpdatesPath, pluginId, "pending plugin directory");
    }

    private string GetValidatedPendingUninstallDirectory(string pluginId)
    {
        ValidatePluginId(pluginId);
        return GetValidatedChildDirectory(_pendingUninstallsPath, pluginId, "pending uninstall directory");
    }

    private static string GetValidatedChildDirectory(string rootDirectory, string childName, string description)
    {
        if (string.IsNullOrWhiteSpace(childName) ||
            Path.IsPathRooted(childName) ||
            childName.Contains(Path.DirectorySeparatorChar) ||
            childName.Contains(Path.AltDirectorySeparatorChar) ||
            childName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"Invalid {description} name.");
        }

        var fullRoot = Path.GetFullPath(rootDirectory);
        var fullRootWithSeparator = EnsureTrailingSeparator(fullRoot);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, childName));
        if (!fullPath.StartsWith(fullRootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The {description} resolves outside the expected root.");

        return fullPath;
    }

    private static void ValidatePluginId(string pluginId)
    {
        if (!IsValidPluginId(pluginId))
            throw new InvalidOperationException("Invalid plugin id.");
    }

    private static bool IsValidPluginId(string? pluginId) =>
        !string.IsNullOrWhiteSpace(pluginId) &&
        !Path.IsPathRooted(pluginId) &&
        !pluginId.Contains("..", StringComparison.Ordinal) &&
        !pluginId.Contains(Path.DirectorySeparatorChar) &&
        !pluginId.Contains(Path.AltDirectorySeparatorChar) &&
        pluginId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
            return;

        try { Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (!File.Exists(path))
            return;

        try { File.Delete(path); }
        catch { /* best effort */ }
    }
}
