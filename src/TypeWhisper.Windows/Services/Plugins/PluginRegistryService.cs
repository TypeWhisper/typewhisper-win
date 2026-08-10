using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// Fetches the plugin registry from GitHub, manages installation, uninstallation,.
/// and update checking for marketplace plugins.
/// </summary>
public sealed class PluginRegistryService
{
    private const string RegistryUrl = "https://typewhisper.github.io/typewhisper-win/plugins.json";
    private const string CommunityRegistryUrl = "https://typewhisper.github.io/typewhisper-win/plugins-community-v1.json";
    private const string PendingUpdatesDirectoryName = ".pending-updates";
    private const string PendingUninstallsDirectoryName = ".pending-uninstalls";
    private const string StagingDirectoryName = ".staging";
    private const string InstallMetadataDirectoryName = ".install-metadata";
    private const int InstallReceiptSchemaVersion = 1;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan DefaultDownloadInactivityTimeout = TimeSpan.FromSeconds(30);

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
    private readonly string _installMetadataPath;
    private readonly string _pluginDataPath;
    private readonly Func<string, string, CancellationToken, Task> _replaceActiveDirectoryAsync;
    private readonly Func<string, CancellationToken, Task> _deleteActiveDirectoryAsync;
    private readonly AppDistributionKind _distributionKind;
    private readonly TimeSpan _downloadInactivityTimeout;
    private readonly bool _usesCustomDirectoryReplacement;
    private readonly RegistryArtifactTrustValidator _artifactTrustValidator;
    private readonly bool _requireArtifactAttestation;
    private readonly string _officialRegistryUrl;
    private readonly string _communityRegistryUrl;
    private readonly Architecture _processArchitecture;
    private readonly Version _hostVersion;
    private readonly SemaphoreSlim _registryFetchLock = new(1, 1);

    private readonly RegistryFeedCache _officialFeedCache = new();
    private readonly RegistryFeedCache _communityFeedCache = new();
    private DateTime _lastUpdateCheck;

    /// <summary>
    /// Gets the user-writable plugin directory used for manual and registry installs.
    /// </summary>
    public string PluginsPath => _pluginsPath;

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
        AppDistributionKind? distributionKind = null,
        TimeSpan? downloadInactivityTimeout = null,
        string? pluginDataPath = null,
        RegistryArtifactTrustValidator? artifactTrustValidator = null,
        bool requireArtifactAttestation = false,
        string officialRegistryUrl = RegistryUrl,
        string communityRegistryUrl = CommunityRegistryUrl,
        Architecture? processArchitecture = null,
        Version? hostVersion = null)
    {
        _pluginManager = pluginManager;
        _pluginLoader = pluginLoader;
        _settings = settings;
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _distributionKind = distributionKind ?? AppDistribution.Current;
        _downloadInactivityTimeout = downloadInactivityTimeout ?? DefaultDownloadInactivityTimeout;
        _pluginsPath = Path.GetFullPath(pluginsPath ?? TypeWhisperEnvironment.PluginsPath);
        _bundledPluginsPath = Path.GetFullPath(bundledPluginsPath ?? Path.Join(AppContext.BaseDirectory, "Plugins"));
        _pluginDataPath = Path.GetFullPath(pluginDataPath ?? pluginManager.PluginDataRoot);
        _pendingUpdatesPath = GetValidatedChildDirectory(_pluginsPath, PendingUpdatesDirectoryName, "pending updates directory");
        _pendingUninstallsPath = GetValidatedChildDirectory(_pluginsPath, PendingUninstallsDirectoryName, "pending uninstalls directory");
        _installMetadataPath = GetValidatedChildDirectory(_pluginsPath, InstallMetadataDirectoryName, "plugin install metadata directory");
        _usesCustomDirectoryReplacement = replaceActiveDirectoryAsync is not null;
        _replaceActiveDirectoryAsync = replaceActiveDirectoryAsync ?? ReplaceActiveDirectoryAsync;
        _deleteActiveDirectoryAsync = deleteActiveDirectoryAsync ?? DeleteActiveDirectoryAsync;
        _artifactTrustValidator = artifactTrustValidator ?? RegistryArtifactTrustValidator.Empty;
        _requireArtifactAttestation = requireArtifactAttestation;
        _officialRegistryUrl = officialRegistryUrl;
        _communityRegistryUrl = communityRegistryUrl;
        _processArchitecture = processArchitecture ?? RuntimeInformation.ProcessArchitecture;
        _hostVersion = hostVersion ?? PluginHostVersion.Current;
    }

    /// <summary>
    /// Fetches official and community plugin feeds. Results and failures are cached for 5 minutes.
    /// Official entries take precedence over community entries with the same plugin ID.
    /// </summary>
    public async Task<IReadOnlyList<RegistryPlugin>> FetchRegistryAsync(CancellationToken ct = default)
    {
        await _registryFetchLock.WaitAsync(ct);

        try
        {
            var now = DateTime.UtcNow;
            var officialTask = FetchRegistryFeedAsync(
                _officialRegistryUrl,
                RegistryArtifactSource.Official,
                _officialFeedCache,
                now,
                ct);
            var communityTask = FetchRegistryFeedAsync(
                _communityRegistryUrl,
                RegistryArtifactSource.Community,
                _communityFeedCache,
                now,
                ct);
            await Task.WhenAll(officialTask, communityTask);
            var official = await officialTask;
            var community = await communityTask;
            var merged = MergeRegistryFeeds(official, community);
            Debug.WriteLine(
                $"[PluginRegistry] Resolved {merged.Count} compatible plugin(s) " +
                $"({official.Count} official, {community.Count} community)");
            return merged;
        }
        finally
        {
            _registryFetchLock.Release();
        }
    }

    private async Task<IReadOnlyList<RegistryPlugin>> FetchRegistryFeedAsync(
        string registryUrl,
        RegistryArtifactSource source,
        RegistryFeedCache cache,
        DateTime now,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(registryUrl))
            return [];
        if (cache.HasAttempted && now - cache.LastAttempt < CacheDuration)
            return cache.Plugins;

        try
        {
            var json = await _httpClient.GetStringAsync(registryUrl, ct);
            var sourceName = source == RegistryArtifactSource.Official ? "official" : "community";
            cache.Plugins = DeserializeRegistryFeed(json)
                .Select(plugin => plugin with { Source = sourceName })
                .Where(plugin => IsValidPluginId(plugin.Id))
                .Where(plugin => IsCompatible(plugin, _hostVersion, _processArchitecture))
                .ToList();
            cache.HasAttempted = true;
            cache.LastAttempt = now;
            return cache.Plugins;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            cache.HasAttempted = true;
            cache.LastAttempt = now;
            Debug.WriteLine($"[PluginRegistry] Failed to fetch {source} registry: {ex.Message}");
            return cache.Plugins;
        }
    }

    private static IReadOnlyList<RegistryPlugin> DeserializeRegistryFeed(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => JsonSerializer.Deserialize<List<RegistryPlugin>>(json, JsonOptions) ?? [],
            JsonValueKind.Object =>
                JsonSerializer.Deserialize<RegistryFeedEnvelope>(json, JsonOptions)?.Plugins ?? [],
            _ => throw new JsonException("Plugin registry feed must be an array or an object containing plugins.")
        };
    }

    private static IReadOnlyList<RegistryPlugin> MergeRegistryFeeds(
        IReadOnlyList<RegistryPlugin> official,
        IReadOnlyList<RegistryPlugin> community)
    {
        var merged = new List<RegistryPlugin>(official.Count + community.Count);
        var pluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in official.Concat(community))
        {
            if (pluginIds.Add(plugin.Id))
                merged.Add(plugin);
        }

        return merged;
    }

    /// <summary>
    /// Determines the install state of a registry plugin.
    /// </summary>
    public PluginInstallState GetInstallState(RegistryPlugin registryPlugin) =>
        GetInstallDiagnosis(registryPlugin, verifyFileHashes: false).State;

    /// <summary>
    /// Diagnoses a registry plugin using pending operations, disk contents, the registry install receipt,
    /// and the runtime load state. The checks are ordered so the same disk state always returns the same result.
    /// </summary>
    public PluginInstallDiagnosis GetInstallDiagnosis(RegistryPlugin registryPlugin) =>
        GetInstallDiagnosis(registryPlugin, verifyFileHashes: true);

    private PluginInstallDiagnosis GetInstallDiagnosis(
        RegistryPlugin registryPlugin,
        bool verifyFileHashes)
    {
        ValidatePluginId(registryPlugin.Id);
        var pendingUninstallDir = GetValidatedPendingUninstallDirectory(registryPlugin.Id);
        var pendingDir = GetValidatedPendingDirectory(registryPlugin.Id);
        var pluginDir = GetValidatedPluginDirectory(registryPlugin.Id);

        try
        {
            var receiptResult = ReadInstallReceipt(registryPlugin.Id);
            var isRegistryManaged = receiptResult.Receipt is not null;

            if (receiptResult.Exists && receiptResult.Receipt is null)
            {
                return Broken(
                    PluginDiagnosticCode.InterruptedInstallation,
                    isRegistryManaged: false,
                    "The registry install receipt is invalid.");
            }

            if (Directory.Exists(pendingUninstallDir))
                return Healthy(PluginInstallState.PendingRestart, isRegistryManaged);

            if (Directory.Exists(pendingDir))
            {
                var pendingManifestResult = ReadManifestForDiagnosis(pendingDir);
                if (pendingManifestResult.Manifest is not null &&
                    ManifestMatchesRegistry(pendingManifestResult.Manifest, registryPlugin))
                {
                    return Healthy(PluginInstallState.PendingRestart, isRegistryManaged);
                }

                return Broken(
                    PluginDiagnosticCode.InterruptedInstallation,
                    isRegistryManaged,
                    "The pending plugin update is incomplete or does not match the registry version.");
            }

            if (HasInterruptedInstallArtifacts(registryPlugin.Id, isRegistryManaged))
            {
                return Broken(
                    PluginDiagnosticCode.InterruptedInstallation,
                    isRegistryManaged,
                    "A previous plugin installation left staging or replacement files behind.");
            }

            var local = _pluginManager.GetPlugin(registryPlugin.Id);
            if (!Directory.Exists(pluginDir))
            {
                if (receiptResult.Receipt is not null)
                {
                    return Broken(
                        PluginDiagnosticCode.MissingFiles,
                        isRegistryManaged: true,
                        "The managed plugin directory is missing.");
                }

                if (local is not null && IsWithinDirectory(local.PluginDirectory, _bundledPluginsPath))
                    return Healthy(PluginInstallState.Bundled, isRegistryManaged: false);

                return Healthy(PluginInstallState.NotInstalled, isRegistryManaged: false);
            }

            EnsureDirectoryReadable(pluginDir);
            var diskManifestResult = ReadManifestForDiagnosis(pluginDir);
            if (!diskManifestResult.Exists)
            {
                return Broken(
                    PluginDiagnosticCode.MissingFiles,
                    isRegistryManaged,
                    "manifest.json is missing from the plugin directory.");
            }

            if (diskManifestResult.Manifest is null)
            {
                return Broken(
                    PluginDiagnosticCode.InvalidManifest,
                    isRegistryManaged,
                    "manifest.json could not be parsed.");
            }

            var diskManifest = diskManifestResult.Manifest;
            if (!ManifestIdMatches(diskManifest, registryPlugin.Id) ||
                string.IsNullOrWhiteSpace(diskManifest.Version) ||
                string.IsNullOrWhiteSpace(diskManifest.AssemblyName) ||
                string.IsNullOrWhiteSpace(diskManifest.PluginClass))
            {
                return Broken(
                    PluginDiagnosticCode.InvalidManifest,
                    isRegistryManaged,
                    "The plugin manifest identity or required fields are invalid.");
            }

            string assemblyPath;
            try
            {
                assemblyPath = GetValidatedPluginFilePath(pluginDir, diskManifest.AssemblyName);
            }
            catch (InvalidOperationException ex)
            {
                return Broken(PluginDiagnosticCode.InvalidManifest, isRegistryManaged, ex.Message);
            }

            if (!File.Exists(assemblyPath))
            {
                return Broken(
                    PluginDiagnosticCode.MissingFiles,
                    isRegistryManaged,
                    $"The plugin assembly '{diskManifest.AssemblyName}' is missing.");
            }

            if (verifyFileHashes && receiptResult.Receipt is not null)
            {
                var integrityFailure = VerifyInstalledFiles(pluginDir, diskManifest, receiptResult.Receipt);
                if (integrityFailure is not null)
                    return integrityFailure;
            }

            if (_pluginManager.IsInitialized)
            {
                if (local is null)
                {
                    return Broken(
                        PluginDiagnosticCode.LoadFailure,
                        isRegistryManaged,
                        "The plugin files are present but the host could not load the plugin.");
                }

                var shouldBeEnabled = !_settings.Current.PluginEnabledState.TryGetValue(registryPlugin.Id, out var enabled) || enabled;
                if (shouldBeEnabled && !_pluginManager.IsEnabled(registryPlugin.Id))
                {
                    return Broken(
                        PluginDiagnosticCode.LoadFailure,
                        isRegistryManaged,
                        "The plugin was loaded but could not be activated.");
                }
            }

            var localVersion = SelectNewestVersion(local?.Manifest.Version, diskManifest.Version);
            if (Version.TryParse(registryPlugin.Version, out var remoteVer) &&
                Version.TryParse(localVersion, out var localVer) &&
                remoteVer > localVer)
            {
                return Healthy(PluginInstallState.UpdateAvailable, isRegistryManaged);
            }

            return Healthy(PluginInstallState.Installed, isRegistryManaged);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Broken(PluginDiagnosticCode.PermissionDenied, HasValidInstallReceipt(registryPlugin.Id), ex.Message);
        }
        catch (IOException ex) when (IsAccessDenied(ex))
        {
            return Broken(PluginDiagnosticCode.PermissionDenied, HasValidInstallReceipt(registryPlugin.Id), ex.Message);
        }
        catch (IOException ex)
        {
            return Broken(PluginDiagnosticCode.MissingFiles, HasValidInstallReceipt(registryPlugin.Id), ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Broken(PluginDiagnosticCode.InvalidManifest, HasValidInstallReceipt(registryPlugin.Id), ex.Message);
        }
    }

    /// <summary>
    /// Validates the source and build attestation claimed by a registry entry.
    /// </summary>
    public RegistryArtifactValidationResult GetArtifactTrustStatus(RegistryPlugin registryPlugin) =>
        _artifactTrustValidator.Validate(registryPlugin);

    /// <summary>
    /// Gets whether a loaded plugin directory is part of the application-bundled plugin root.
    /// </summary>
    public bool IsBundledPluginPath(string pluginDirectory) =>
        !string.IsNullOrWhiteSpace(pluginDirectory) && IsWithinDirectory(pluginDirectory, _bundledPluginsPath);

    /// <summary>
    /// Validates the build provenance persisted with an installed registry plugin.
    /// A matching current registry entry alone never upgrades an installation's trust.
    /// </summary>
    public RegistryArtifactValidationResult GetInstalledArtifactTrustStatus(RegistryPlugin registryPlugin)
    {
        ValidatePluginId(registryPlugin.Id);
        var receipt = ReadInstallReceipt(registryPlugin.Id).Receipt;
        if (receipt?.ArtifactTrust is null)
        {
            return new RegistryArtifactValidationResult(
                RegistryArtifactValidationCode.MissingMetadata,
                RegistryArtifactTrustValidator.ClassifySource(receipt?.RegistrySource),
                RegistryArtifactTrustLevel.Unverified);
        }

        var artifactTrust = receipt.ArtifactTrust;
        return _artifactTrustValidator.Validate(new RegistryPlugin
        {
            Id = receipt.PluginId,
            Version = receipt.Version,
            DownloadUrl = receipt.DownloadUrl,
            Size = artifactTrust.PackageSize,
            Sha256 = receipt.PackageSha256,
            Source = artifactTrust.Source,
            Trust = artifactTrust.Trust,
            SourceRepository = artifactTrust.SourceRepository,
            Attestation = artifactTrust.Attestation
        });
    }

    /// <summary>
    /// Downloads and installs a plugin from the registry.
    /// </summary>
    public async Task<PluginInstallResult> InstallPluginAsync(
        RegistryPlugin registryPlugin,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        await InstallPluginCoreAsync(registryPlugin, progress, requirePackageHash: false, ct);

    /// <summary>
    /// Repairs a broken registry-managed plugin by reinstalling it from the current registry artifact.
    /// Unknown or manual installs require an explicit adoption decision from the caller.
    /// </summary>
    public async Task<PluginInstallResult> RepairPluginAsync(
        RegistryPlugin registryPlugin,
        bool allowSourceAdoption,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var diagnosis = GetInstallDiagnosis(registryPlugin);
        if (diagnosis.State != PluginInstallState.Broken)
            throw new InvalidOperationException(Loc.Instance["Plugins.RepairNotRequired"]);

        if (!diagnosis.IsRegistryManaged && !allowSourceAdoption)
            throw new InvalidOperationException(Loc.Instance["Plugins.RepairSourceUnknown"]);

        return await InstallPluginCoreAsync(registryPlugin, progress, requirePackageHash: true, ct);
    }

    private async Task<PluginInstallResult> InstallPluginCoreAsync(
        RegistryPlugin registryPlugin,
        IProgress<double>? progress,
        bool requirePackageHash,
        CancellationToken ct)
    {
        ValidatePluginId(registryPlugin.Id);
        var artifactTrust = ValidateArtifactTrustForInstall(registryPlugin);
        requirePackageHash |= artifactTrust.IsVerified;

        Directory.CreateDirectory(_pluginsPath);
        Directory.CreateDirectory(_pendingUpdatesPath);
        Directory.CreateDirectory(_pendingUninstallsPath);
        Directory.CreateDirectory(_installMetadataPath);

        var pluginDir = GetValidatedPluginDirectory(registryPlugin.Id);
        var stagingRoot = GetValidatedChildDirectory(_pluginsPath, StagingDirectoryName, "plugin staging directory");
        var stagingDir = GetValidatedChildDirectory(stagingRoot, $"{registryPlugin.Id}-{Guid.NewGuid():N}", "plugin staging instance directory");
        var tempZip = Path.GetTempFileName();

        try
        {
            Directory.CreateDirectory(stagingRoot);

            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            downloadCts.CancelAfter(_downloadInactivityTimeout);
            try
            {
                using var response = await _httpClient.GetAsync(
                    registryPlugin.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    downloadCts.Token);
                response.EnsureSuccessStatusCode();

                var contentLength = response.Content.Headers.ContentLength;
                if (artifactTrust.IsVerified && contentLength is not null && contentLength != registryPlugin.Size)
                    throw CreateArtifactTrustException(RegistryArtifactValidationCode.InvalidPackageSize);

                var totalBytes = contentLength ?? registryPlugin.Size;
                await using var contentStream = await response.Content.ReadAsStreamAsync(downloadCts.Token);
                await using var fileStream = File.Create(tempZip);

                var buffer = new byte[8192];
                long bytesRead = 0;
                int read;
                while (true)
                {
                    downloadCts.CancelAfter(_downloadInactivityTimeout);
                    read = await contentStream.ReadAsync(buffer, downloadCts.Token);
                    if (read == 0)
                        break;

                    if (artifactTrust.IsVerified && bytesRead + read > registryPlugin.Size)
                        throw CreateArtifactTrustException(RegistryArtifactValidationCode.InvalidPackageSize);

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    bytesRead += read;
                    progress?.Report(totalBytes > 0 ? (double)bytesRead / totalBytes : 0);
                }

                if (artifactTrust.IsVerified && bytesRead != registryPlugin.Size)
                    throw CreateArtifactTrustException(RegistryArtifactValidationCode.InvalidPackageSize);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && downloadCts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Plugin download timed out because no data was received. Check your internet connection and try again.",
                    ex);
            }

            VerifyDownloadedPackage(registryPlugin, tempZip, requirePackageHash);
            ZipFile.ExtractToDirectory(tempZip, stagingDir, overwriteFiles: true);

            // Unblock downloaded files
            PluginLoader.UnblockDirectory(stagingDir);
            ValidateStagedPlugin(registryPlugin, stagingDir);
            var installReceipt = CreateInstallReceipt(registryPlugin, stagingDir, tempZip, artifactTrust);
            WriteEmbeddedInstallReceipt(stagingDir, installReceipt);

            var shouldEnable = !_settings.Current.PluginEnabledState.TryGetValue(registryPlugin.Id, out var enabled) || enabled;
            var settingsSnapshot = CapturePluginSettings(registryPlugin.Id);
            var previousPluginDirectoryExisted = Directory.Exists(pluginDir);

            // Unload existing version if present
            if (_pluginManager.GetPlugin(registryPlugin.Id) is not null)
            {
                await _pluginManager.UnloadPluginAsync(registryPlugin.Id);
                CollectUnloadedPluginContexts();
            }

            await ClearPendingUninstallAsync(registryPlugin.Id, ct);

            string? backupDirectory = null;
            try
            {
                backupDirectory = await ReplacePluginDirectoryForInstallAsync(stagingDir, pluginDir, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await QueuePendingUpdateAsync(registryPlugin.Id, stagingDir, ct);
                await TryReloadPreviousPluginAsync(pluginDir, shouldEnable);
                Debug.WriteLine($"[PluginRegistry] Queued plugin update pending restart: {registryPlugin.Id} ({ex.Message})");
                return PluginInstallResult.PendingRestart;
            }

            DeleteDirectoryIfExists(GetValidatedPendingDirectory(registryPlugin.Id));

            try
            {
                await _pluginManager.LoadPluginFromDirectoryAsync(pluginDir, activate: shouldEnable);
                if (_pluginManager.GetPlugin(registryPlugin.Id) is null ||
                    (shouldEnable && !_pluginManager.IsEnabled(registryPlugin.Id)))
                {
                    throw new InvalidOperationException(Loc.Instance["Plugins.RepairLoadValidationFailed"]);
                }

                WriteInstallReceipt(registryPlugin.Id, installReceipt);
                DeleteEmbeddedInstallReceipt(pluginDir);
                DeleteDirectoryIfExists(backupDirectory);
                CleanupInterruptedInstallArtifacts(registryPlugin.Id);
            }
            catch (Exception ex)
            {
                await RollBackPluginInstallAsync(
                    registryPlugin.Id,
                    pluginDir,
                    backupDirectory,
                    previousPluginDirectoryExisted,
                    shouldEnable,
                    settingsSnapshot);
                throw new InvalidOperationException(
                    Loc.Instance.GetString("Plugins.RepairRolledBackFormat", ex.Message),
                    ex);
            }

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
            DeleteInstallReceipt(pluginId);
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
                PromoteEmbeddedInstallReceipt(pluginId, pluginDir);
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
                DeleteInstallReceipt(pluginId);

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

    private void VerifyDownloadedPackage(
        RegistryPlugin registryPlugin,
        string packagePath,
        bool requirePackageHash)
    {
        if (string.IsNullOrWhiteSpace(registryPlugin.Sha256))
        {
            if (_distributionKind == AppDistributionKind.Store || requirePackageHash)
                throw new InvalidOperationException(Loc.Instance["Plugins.PackageHashMissing"]);

            return;
        }

        var expectedHash = NormalizeSha256(registryPlugin.Sha256);
        var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.Instance["Plugins.PackageHashMismatch"]);
    }

    private static string NormalizeSha256(string sha256)
    {
        var normalized = sha256.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException(Loc.Instance["Plugins.PackageHashInvalid"]);

        return normalized;
    }

    private static bool IsCompatible(
        RegistryPlugin plugin,
        Version hostVersion,
        Architecture processArchitecture)
    {
        if (!string.IsNullOrWhiteSpace(plugin.MinHostVersion) &&
            (!Version.TryParse(plugin.MinHostVersion, out var minVersion) || hostVersion < minVersion))
        {
            return false;
        }

        if (plugin.Platforms is not null &&
            (plugin.Platforms.Count == 0 ||
             !plugin.Platforms.Any(platform => IsCurrentWindowsPlatform(platform, processArchitecture))))
        {
            return false;
        }

        if (plugin.SupportedArchitectures is not null &&
            (plugin.SupportedArchitectures.Count == 0 ||
             !plugin.SupportedArchitectures.Any(architecture =>
                 IsCurrentArchitecture(architecture, processArchitecture))))
        {
            return false;
        }

        return true;
    }

    private static bool IsCurrentWindowsPlatform(string platform, Architecture processArchitecture) =>
        platform.Trim().ToLowerInvariant() switch
        {
            "windows" or "win" or "win32" => true,
            "windows-x64" or "win-x64" => processArchitecture == Architecture.X64,
            "windows-arm64" or "win-arm64" => processArchitecture == Architecture.Arm64,
            "windows-x86" or "win-x86" => processArchitecture == Architecture.X86,
            _ => false
        };

    private static bool IsCurrentArchitecture(string architecture, Architecture processArchitecture) =>
        architecture.Trim().ToLowerInvariant() switch
        {
            "x64" or "amd64" or "win-x64" => processArchitecture == Architecture.X64,
            "arm64" or "aarch64" or "win-arm64" => processArchitecture == Architecture.Arm64,
            "x86" or "i386" or "win-x86" => processArchitecture == Architecture.X86,
            _ => false
        };

    private static void ValidateStagedPlugin(RegistryPlugin registryPlugin, string stagingDir)
    {
        var manifest = ReadManifest(stagingDir)
            ?? throw new InvalidOperationException(Loc.Instance["Plugins.PackageManifestInvalid"]);

        if (!string.Equals(manifest.Id, registryPlugin.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.Instance["Plugins.PackageIdMismatch"]);

        if (!string.Equals(manifest.Version, registryPlugin.Version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.Instance["Plugins.PackageVersionMismatch"]);

        if (string.IsNullOrWhiteSpace(manifest.AssemblyName) ||
            string.IsNullOrWhiteSpace(manifest.PluginClass) ||
            !File.Exists(GetValidatedPluginFilePath(stagingDir, manifest.AssemblyName)))
        {
            throw new InvalidOperationException(Loc.Instance["Plugins.PackageManifestInvalid"]);
        }
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
            throw new IOException(Loc.Instance.GetString("Plugins.PendingUninstallCleanupFailedFormat", pluginId));
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

    private RegistryArtifactValidationResult ValidateArtifactTrustForInstall(RegistryPlugin registryPlugin)
    {
        var result = _artifactTrustValidator.Validate(registryPlugin);
        if (result.IsVerified)
            return result;

        if (!_requireArtifactAttestation && !RegistryArtifactTrustValidator.HasAnyTrustMetadata(registryPlugin))
            return result;

        throw CreateArtifactTrustException(result.Code);
    }

    private static RegistryArtifactTrustException CreateArtifactTrustException(
        RegistryArtifactValidationCode validationCode) =>
        new(
            validationCode,
            Loc.Instance.GetString("Plugins.ArtifactTrustValidationFailedFormat", validationCode));

    private async Task<string?> ReplacePluginDirectoryForInstallAsync(
        string sourceDirectory,
        string targetDirectory,
        CancellationToken ct)
    {
        if (_usesCustomDirectoryReplacement)
        {
            var customBackupDirectory = Directory.Exists(targetDirectory)
                ? targetDirectory + ".repair-backup-" + Guid.NewGuid().ToString("N")
                : null;
            if (customBackupDirectory is not null)
                CopyPluginDirectory(targetDirectory, customBackupDirectory);

            try
            {
                await _replaceActiveDirectoryAsync(sourceDirectory, targetDirectory, ct);
                return customBackupDirectory;
            }
            catch
            {
                if (Directory.Exists(targetDirectory))
                    Directory.Delete(targetDirectory, recursive: true);

                if (customBackupDirectory is not null)
                    Directory.Move(customBackupDirectory, targetDirectory);

                throw;
            }
        }

        var backupDirectory = Directory.Exists(targetDirectory)
            ? targetDirectory + ".repair-backup-" + Guid.NewGuid().ToString("N")
            : null;

        if (backupDirectory is not null)
            Directory.Move(targetDirectory, backupDirectory);

        try
        {
            await _replaceActiveDirectoryAsync(sourceDirectory, targetDirectory, ct);
            return backupDirectory;
        }
        catch
        {
            if (Directory.Exists(targetDirectory))
                Directory.Delete(targetDirectory, recursive: true);

            if (backupDirectory is not null && Directory.Exists(backupDirectory))
                Directory.Move(backupDirectory, targetDirectory);

            throw;
        }
    }

    private static void CopyPluginDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var sourcePath in EnumeratePluginFiles(sourceDirectory))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var targetPath = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath);
        }
    }

    private PluginInstallReceipt CreateInstallReceipt(
        RegistryPlugin registryPlugin,
        string pluginDirectory,
        string packagePath,
        RegistryArtifactValidationResult artifactTrust)
    {
        var files = EnumeratePluginFiles(pluginDirectory)
            .Select(path => new PluginInstallFile(
                Path.GetRelativePath(pluginDirectory, path).Replace(Path.DirectorySeparatorChar, '/'),
                new FileInfo(path).Length,
                ComputeFileSha256(path)))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

        var artifactTrustReceipt = artifactTrust.IsVerified
            ? new PluginArtifactTrustReceipt(
                registryPlugin.Source!,
                registryPlugin.Trust!,
                registryPlugin.SourceRepository!,
                registryPlugin.Size,
                registryPlugin.Attestation!)
            : null;
        var registrySource = RegistryArtifactTrustValidator.ClassifySource(registryPlugin.Source) switch
        {
            RegistryArtifactSource.Official => "official",
            RegistryArtifactSource.Community => "community",
            _ => null
        };

        return new PluginInstallReceipt(
            InstallReceiptSchemaVersion,
            registryPlugin.Id,
            registryPlugin.Version,
            registryPlugin.DownloadUrl,
            ComputeFileSha256(packagePath),
            files,
            registrySource,
            artifactTrustReceipt);
    }

    private static IEnumerable<string> EnumeratePluginFiles(string pluginDirectory)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(pluginDirectory);

        while (pendingDirectories.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pendingDirectories.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(Loc.Instance["Plugins.PackageManifestInvalid"]);

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                }
                else if (!string.Equals(Path.GetFileName(entry), EmbeddedInstallReceiptFileName, StringComparison.Ordinal))
                {
                    yield return entry;
                }
            }
        }
    }

    private PluginInstallDiagnosis? VerifyInstalledFiles(
        string pluginDirectory,
        PluginManifest manifest,
        PluginInstallReceipt receipt)
    {
        if (!string.Equals(receipt.PluginId, manifest.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(receipt.Version, manifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            return Broken(
                PluginDiagnosticCode.IntegrityMismatch,
                isRegistryManaged: true,
                "The installed manifest does not match the registry install receipt.");
        }

        IReadOnlyList<string> actualFiles;
        try
        {
            actualFiles = EnumeratePluginFiles(pluginDirectory).ToArray();
        }
        catch (InvalidOperationException ex)
        {
            return Broken(PluginDiagnosticCode.IntegrityMismatch, isRegistryManaged: true, ex.Message);
        }

        if (actualFiles.Count != receipt.Files.Count)
        {
            return Broken(
                PluginDiagnosticCode.IntegrityMismatch,
                isRegistryManaged: true,
                "The installed plugin file set does not match the registry install receipt.");
        }

        foreach (var expectedFile in receipt.Files)
        {
            string filePath;
            try
            {
                filePath = GetValidatedPluginFilePath(pluginDirectory, expectedFile.RelativePath);
            }
            catch (InvalidOperationException ex)
            {
                return Broken(PluginDiagnosticCode.IntegrityMismatch, isRegistryManaged: true, ex.Message);
            }

            if (!File.Exists(filePath))
            {
                return Broken(
                    PluginDiagnosticCode.MissingFiles,
                    isRegistryManaged: true,
                    $"The recorded plugin file '{expectedFile.RelativePath}' is missing.");
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length != expectedFile.Length ||
                !string.Equals(ComputeFileSha256(filePath), expectedFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Broken(
                    PluginDiagnosticCode.IntegrityMismatch,
                    isRegistryManaged: true,
                    $"The installed plugin file '{expectedFile.RelativePath}' failed integrity validation.");
            }
        }

        return null;
    }

    private InstallReceiptReadResult ReadInstallReceipt(string pluginId)
    {
        var receiptPath = GetValidatedInstallReceiptPath(pluginId);
        if (!File.Exists(receiptPath))
            return new InstallReceiptReadResult(false, null);

        try
        {
            var receipt = JsonSerializer.Deserialize<PluginInstallReceipt>(File.ReadAllText(receiptPath), JsonOptions);
            return IsValidInstallReceipt(pluginId, receipt)
                ? new InstallReceiptReadResult(true, receipt)
                : new InstallReceiptReadResult(true, null);
        }
        catch (JsonException)
        {
            return new InstallReceiptReadResult(true, null);
        }
        catch (NotSupportedException)
        {
            return new InstallReceiptReadResult(true, null);
        }
    }

    private static bool IsValidInstallReceipt(string pluginId, PluginInstallReceipt? receipt)
    {
        if (receipt is null ||
            receipt.SchemaVersion != InstallReceiptSchemaVersion ||
            !string.Equals(receipt.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(receipt.Version) ||
            string.IsNullOrWhiteSpace(receipt.DownloadUrl) ||
            receipt.Files is null ||
            receipt.Files.Count == 0)
        {
            return false;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(receipt.PackageSha256))
                return false;

            _ = NormalizeSha256(receipt.PackageSha256);
            if (receipt.Files.Any(file => file is null))
                return false;

            return receipt.Files
                .Select(file => file.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == receipt.Files.Count &&
                receipt.Files.All(file =>
                    file is not null &&
                    !string.IsNullOrWhiteSpace(file.RelativePath) &&
                    file.Length >= 0 &&
                    !string.IsNullOrWhiteSpace(file.Sha256) &&
                    string.Equals(NormalizeSha256(file.Sha256), file.Sha256, StringComparison.OrdinalIgnoreCase)) &&
                IsValidRegistrySource(receipt.RegistrySource) &&
                IsValidArtifactTrustReceiptStructure(receipt.ArtifactTrust);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsValidRegistrySource(string? registrySource) =>
        registrySource is null ||
        RegistryArtifactTrustValidator.ClassifySource(registrySource) != RegistryArtifactSource.Unknown;

    private static bool IsValidArtifactTrustReceiptStructure(PluginArtifactTrustReceipt? artifactTrust)
    {
        if (artifactTrust is null)
            return true;

        return !string.IsNullOrWhiteSpace(artifactTrust.Source) &&
               !string.IsNullOrWhiteSpace(artifactTrust.Trust) &&
               !string.IsNullOrWhiteSpace(artifactTrust.SourceRepository) &&
               artifactTrust.PackageSize > 0 &&
               artifactTrust.Attestation is
               {
                   Algorithm: not null,
                   KeyId: not null,
                   SourceCommit: not null,
                   Signature: not null
               };
    }

    private bool HasValidInstallReceipt(string pluginId)
    {
        try
        {
            return ReadInstallReceipt(pluginId).Receipt is not null;
        }
        catch
        {
            return false;
        }
    }

    private void WriteInstallReceipt(string pluginId, PluginInstallReceipt receipt)
    {
        Directory.CreateDirectory(_installMetadataPath);
        WriteJsonAtomically(GetValidatedInstallReceiptPath(pluginId), receipt);
    }

    private static void WriteEmbeddedInstallReceipt(string pluginDirectory, PluginInstallReceipt receipt) =>
        WriteJsonAtomically(Path.Combine(pluginDirectory, EmbeddedInstallReceiptFileName), receipt);

    private static void WriteJsonAtomically<T>(string targetPath, T value)
    {
        var parentDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException(Loc.Instance["Plugins.InvalidPath"]);
        Directory.CreateDirectory(parentDirectory);
        var tempPath = Path.Combine(parentDirectory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            DeleteFileIfExists(tempPath);
        }
    }

    private void PromoteEmbeddedInstallReceipt(string pluginId, string pluginDirectory)
    {
        var embeddedPath = Path.Combine(pluginDirectory, EmbeddedInstallReceiptFileName);
        if (!File.Exists(embeddedPath))
            return;

        PluginInstallReceipt? receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<PluginInstallReceipt>(File.ReadAllText(embeddedPath), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            Debug.WriteLine($"[PluginRegistry] Invalid embedded install receipt for {pluginId}: {ex.Message}");
            return;
        }

        if (!IsValidInstallReceipt(pluginId, receipt))
        {
            Debug.WriteLine($"[PluginRegistry] Invalid embedded install receipt for {pluginId}");
            return;
        }

        WriteInstallReceipt(pluginId, receipt!);
        DeleteEmbeddedInstallReceipt(pluginDirectory);
    }

    private static void DeleteEmbeddedInstallReceipt(string pluginDirectory) =>
        DeleteFileIfExists(Path.Combine(pluginDirectory, EmbeddedInstallReceiptFileName));

    private void DeleteInstallReceipt(string pluginId)
    {
        var receiptPath = GetValidatedInstallReceiptPath(pluginId);
        if (File.Exists(receiptPath))
            File.Delete(receiptPath);
    }

    private async Task RollBackPluginInstallAsync(
        string pluginId,
        string pluginDirectory,
        string? backupDirectory,
        bool previousPluginDirectoryExisted,
        bool shouldEnable,
        PluginSettingsSnapshot settingsSnapshot)
    {
        if (_pluginManager.GetPlugin(pluginId) is not null)
        {
            await _pluginManager.UnloadPluginAsync(pluginId);
            CollectUnloadedPluginContexts();
        }

        RestorePluginSettings(pluginId, settingsSnapshot);

        if (Directory.Exists(pluginDirectory))
            await DeletePluginDirectoryForRollbackAsync(pluginDirectory);

        if (backupDirectory is not null && Directory.Exists(backupDirectory))
        {
            Directory.Move(backupDirectory, pluginDirectory);
        }
        else if (previousPluginDirectoryExisted)
        {
            throw new IOException("The previous plugin directory could not be restored.");
        }

        await TryReloadPreviousPluginAsync(pluginDirectory, shouldEnable);
    }

    private static async Task DeletePluginDirectoryForRollbackAsync(string pluginDirectory)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                Directory.Delete(pluginDirectory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                CollectUnloadedPluginContexts();
                await Task.Delay(50);
            }
        }

        throw new IOException("The failed plugin candidate could not be removed during rollback.", lastError);
    }

    private async Task TryReloadPreviousPluginAsync(string pluginDirectory, bool shouldEnable)
    {
        if (!Directory.Exists(pluginDirectory) || ReadManifest(pluginDirectory) is null)
            return;

        try
        {
            await _pluginManager.LoadPluginFromDirectoryAsync(pluginDirectory, activate: shouldEnable);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginRegistry] Failed to reload previous plugin at {pluginDirectory}: {ex.Message}");
        }
    }

    private PluginSettingsSnapshot CapturePluginSettings(string pluginId)
    {
        var settingsPath = GetValidatedPluginSettingsPath(pluginId);
        return File.Exists(settingsPath)
            ? new PluginSettingsSnapshot(true, File.ReadAllBytes(settingsPath))
            : new PluginSettingsSnapshot(false, null);
    }

    private void RestorePluginSettings(string pluginId, PluginSettingsSnapshot snapshot)
    {
        var settingsPath = GetValidatedPluginSettingsPath(pluginId);
        if (!snapshot.Exists)
        {
            DeleteFileIfExists(settingsPath);
            return;
        }

        var pluginDataDirectory = Path.GetDirectoryName(settingsPath)
            ?? throw new InvalidOperationException(Loc.Instance["Plugins.InvalidPath"]);
        Directory.CreateDirectory(pluginDataDirectory);
        var tempPath = Path.Combine(pluginDataDirectory, $".settings.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(tempPath, snapshot.Contents ?? []);
            File.Move(tempPath, settingsPath, overwrite: true);
        }
        finally
        {
            DeleteFileIfExists(tempPath);
        }
    }

    private bool HasInterruptedInstallArtifacts(string pluginId, bool hasValidInstallReceipt)
    {
        var pluginDirectory = GetValidatedPluginDirectory(pluginId);
        if (!hasValidInstallReceipt &&
            File.Exists(Path.Combine(pluginDirectory, EmbeddedInstallReceiptFileName)))
        {
            return true;
        }

        var stagingRoot = GetValidatedChildDirectory(_pluginsPath, StagingDirectoryName, "plugin staging directory");
        if (Directory.Exists(stagingRoot) && Directory.EnumerateDirectories(stagingRoot)
            .Any(path => Path.GetFileName(path).StartsWith(pluginId + "-", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return Directory.Exists(_pluginsPath) && Directory.EnumerateDirectories(_pluginsPath)
            .Select(Path.GetFileName)
            .Any(name => name is not null &&
                (name.StartsWith(pluginId + ".replacing-", StringComparison.OrdinalIgnoreCase) ||
                 (!hasValidInstallReceipt &&
                  name.StartsWith(pluginId + ".repair-backup-", StringComparison.OrdinalIgnoreCase))));
    }

    private void CleanupInterruptedInstallArtifacts(string pluginId)
    {
        var stagingRoot = GetValidatedChildDirectory(_pluginsPath, StagingDirectoryName, "plugin staging directory");
        if (Directory.Exists(stagingRoot))
        {
            foreach (var path in Directory.EnumerateDirectories(stagingRoot)
                .Where(path => Path.GetFileName(path).StartsWith(pluginId + "-", StringComparison.OrdinalIgnoreCase)))
            {
                DeleteDirectoryIfExists(path);
            }
        }

        if (!Directory.Exists(_pluginsPath))
            return;

        foreach (var path in Directory.EnumerateDirectories(_pluginsPath).Where(path =>
            Path.GetFileName(path).StartsWith(pluginId + ".replacing-", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith(pluginId + ".repair-backup-", StringComparison.OrdinalIgnoreCase)))
        {
            DeleteDirectoryIfExists(path);
        }
    }

    private static ManifestReadResult ReadManifestForDiagnosis(string pluginDirectory)
    {
        var manifestPath = Path.Combine(pluginDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
            return new ManifestReadResult(false, null);

        try
        {
            return new ManifestReadResult(
                true,
                JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), JsonOptions));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new ManifestReadResult(true, null);
        }
    }

    private static void EnsureDirectoryReadable(string pluginDirectory)
    {
        _ = EnumeratePluginFiles(pluginDirectory).ToArray();
    }

    private static string GetValidatedPluginFilePath(string pluginDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':') ||
            relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment == ".."))
        {
            throw new InvalidOperationException(Loc.Instance["Plugins.PathOutsideRoot"]);
        }

        var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(pluginDirectory));
        var fullPath = Path.GetFullPath(Path.Combine(pluginDirectory, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.Instance["Plugins.PathOutsideRoot"]);

        return fullPath;
    }

    private string GetValidatedInstallReceiptPath(string pluginId)
    {
        ValidatePluginId(pluginId);
        return GetValidatedChildDirectory(_installMetadataPath, pluginId + ".json", "plugin install receipt");
    }

    private string GetValidatedPluginSettingsPath(string pluginId)
    {
        ValidatePluginId(pluginId);
        var pluginDataDirectory = GetValidatedChildDirectory(_pluginDataPath, pluginId, "plugin data directory");
        return Path.Combine(pluginDataDirectory, "settings.json");
    }

    private static bool IsWithinDirectory(string path, string rootDirectory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(rootDirectory);
        return PathsEqual(fullPath, fullRoot) ||
               fullPath.StartsWith(EnsureTrailingSeparator(fullRoot), StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsAccessDenied(IOException exception)
    {
        var nativeError = exception.HResult & 0xFFFF;
        return nativeError is 5 or 32 or 33;
    }

    private static PluginInstallDiagnosis Healthy(PluginInstallState state, bool isRegistryManaged) =>
        new(state, PluginDiagnosticCode.None, isRegistryManaged);

    private static PluginInstallDiagnosis Broken(
        PluginDiagnosticCode code,
        bool isRegistryManaged,
        string? details = null) =>
        new(PluginInstallState.Broken, code, isRegistryManaged, details);

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
            throw new InvalidOperationException(Loc.Instance["Plugins.InvalidPath"]);
        }

        var fullRoot = Path.GetFullPath(rootDirectory);
        var fullRootWithSeparator = EnsureTrailingSeparator(fullRoot);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, childName));
        if (!fullPath.StartsWith(fullRootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.Instance["Plugins.PathOutsideRoot"]);

        return fullPath;
    }

    private static void ValidatePluginId(string pluginId)
    {
        if (!IsValidPluginId(pluginId))
            throw new InvalidOperationException(Loc.Instance["Plugins.InvalidId"]);
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

    private static void DeleteDirectoryIfExists(string? path)
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

    private const string EmbeddedInstallReceiptFileName = ".typewhisper-install.json";

    private sealed class RegistryFeedCache
    {
        public bool HasAttempted { get; set; }
        public DateTime LastAttempt { get; set; }
        public IReadOnlyList<RegistryPlugin> Plugins { get; set; } = [];
    }

    private sealed record RegistryFeedEnvelope(IReadOnlyList<RegistryPlugin>? Plugins);

    private sealed record PluginInstallReceipt(
        int SchemaVersion,
        string PluginId,
        string Version,
        string DownloadUrl,
        string PackageSha256,
        IReadOnlyList<PluginInstallFile> Files,
        string? RegistrySource = null,
        PluginArtifactTrustReceipt? ArtifactTrust = null);

    private sealed record PluginArtifactTrustReceipt(
        string Source,
        string Trust,
        string SourceRepository,
        long PackageSize,
        RegistryArtifactAttestation Attestation);

    private sealed record PluginInstallFile(string RelativePath, long Length, string Sha256);

    private sealed record InstallReceiptReadResult(bool Exists, PluginInstallReceipt? Receipt);

    private sealed record ManifestReadResult(bool Exists, PluginManifest? Manifest);

    private sealed record PluginSettingsSnapshot(bool Exists, byte[]? Contents);
}
