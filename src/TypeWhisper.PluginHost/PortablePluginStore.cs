using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginHost;

// Immutable package directories avoid replacing native DLLs loaded by Windows.
// One atomic index is authoritative, including an intentionally empty installation.
// Updates take effect at the next host startup; removed binaries are collected then.
public sealed class PortablePluginStore
{
    public const long MaximumPackageBytes = 1024L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Version _host;
    private readonly Func<string, IPluginHostServices>? _services;
    private readonly Func<TypeWhisper.Core.Models.ManagedProviderPolicy>? _policy;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<string, Receipt> _installed = new(StringComparer.Ordinal);
    private bool _initialized;
    private sealed record Receipt(string Directory, string Version, string? PendingDirectory = null, string? PendingVersion = null, string? UpdateWarning = null);
    // The lifecycle operation in progress and why the policy would refuse it, so a policy pass can cancel it;
    // guarded by _sync.
    private CancellationTokenSource? _activeOperation;
    private Func<string?>? _activeRefusal;
    public string Root { get; }
    public string InventoryRoot => Path.Combine(Root, "installed");
    public bool Initialized => _initialized;
    public PortablePluginStore(string root, Version host, HttpClient http, Func<string, IPluginHostServices>? services = null,
        Func<TypeWhisper.Core.Models.ManagedProviderPolicy>? policy = null)
    { Root = Path.GetFullPath(root); _host = host; _http = http; _services = services; _policy = policy; }

    /// <summary>Cancels an installation or uninstall hook in progress that the policy refuses by now.</summary>
    public void ReapplyPolicy()
    {
        CancellationTokenSource? cancel;
        lock (_sync) cancel = _activeRefusal?.Invoke() is not null ? _activeOperation : null;
        try { cancel?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Why machine policy refuses installing or updating a package, or null.</summary>
    public string? InstallRefusal(string pluginId)
    {
        var policy = _policy?.Invoke() ?? TypeWhisper.Core.Models.ManagedProviderPolicy.None;
        return policy.PluginDownloadRefusal() ?? policy.PluginRefusal(pluginId);
    }

    /// <summary>Why a catalog entry may not be installed, or null: downloads are off, the package is outside the allowlist, or it is a remote package whose every declared capability is refused.</summary>
    public string? InstallRefusal(PortableCatalogEntry entry)
    {
        var policy = _policy?.Invoke() ?? TypeWhisper.Core.Models.ManagedProviderPolicy.None;
        return policy.PluginDownloadRefusal()
            ?? policy.PackageRefusal(entry.Id, PluginLocalityMap.ToContentLocality(entry.ResolvedLocality), entry.Categories);
    }

    /// <summary>Initializes the authoritative installation index. Optional bootstrap IDs select bundle directories only when no index exists.</summary>
    public async Task InitializeAsync(string? bundledRoot = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? bootstrapPluginIds = null)
    {
        var bootstrap = bootstrapPluginIds?.Distinct(StringComparer.Ordinal).ToArray();
        if (bootstrap is not null) foreach (var id in bootstrap) PortableCatalogEntry.ValidateId(id);
        await _operation.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.Combine(Root, "packages"));
            var index = Path.Combine(Root, "installed.json");
            var next = new Dictionary<string, Receipt>(StringComparer.Ordinal);
            if (File.Exists(index))
            {
                next = JsonSerializer.Deserialize<Dictionary<string, Receipt>>(await File.ReadAllTextAsync(index, ct), PortablePluginCatalog.Json)
                    ?? throw new InvalidDataException("The installed plugin index is invalid.");
                var promoted = new List<(string Id, Receipt Staged)>();
                foreach (var (id, receipt) in next.ToArray())
                {
                    PortableCatalogEntry.ValidateId(id);
                    PackagePath(receipt.Directory);
                    if (receipt.PendingDirectory is not { } pending) continue;
                    try
                    {
                        // An update the policy refuses by now stays staged, judged by the staged package's own
                        // manifest: the previous version keeps running until the policy allows the update.
                        if (UpdateRefused(id) || RefusedForExecution(id, PackagePath(pending))) continue;
                        ValidatePackage(PackagePath(pending), id, receipt.PendingVersion!);
                        next[id] = new(pending, receipt.PendingVersion!);
                        promoted.Add((id, receipt));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                    {
                        // Fall back only to a valid previous package. Never hide damage to both versions.
                        ValidatePackage(PackagePath(receipt.Directory), id, receipt.Version);
                        next[id] = new(receipt.Directory, receipt.Version, UpdateWarning:
                            "The staged update was invalid. The previous version was kept; retry the update.");
                    }
                }
                // Judged once more right before the commit: a policy that turned restrictive while the index was
                // scanned keeps the update staged and the previous version running.
                foreach (var (id, staged) in promoted)
                    if (UpdateRefused(id) || RefusedForExecution(id, PackagePath(next[id].Directory))) next[id] = staged;
            }
            else if (bundledRoot is not null && Directory.Exists(bundledRoot))
            {
                foreach (var package in bootstrap is null ? PortablePluginInventory.Scan(bundledRoot, _host)
                    : bootstrap.Where(id => Directory.Exists(Path.Combine(bundledRoot, id)))
                        .Select(id => PortablePluginInventory.Inspect(Path.Combine(bundledRoot, id), _host)).ToArray())
                {
                    if (package.Error is not null) throw new InvalidDataException(package.Error);
                    var manifest = package.Manifest!;
                    if (bootstrap is not null && manifest.Id != Path.GetFileName(package.Directory))
                        throw new InvalidDataException("A selected bundle identity does not match its directory.");
                    if (manifest.IsInternalDependency) continue;
                    // A bundle the policy refuses is not bootstrapped: no copy, no hook, no receipt. It can be
                    // installed from the catalog once the policy allows it.
                    if (ExecutionRefusal(manifest.Id, package.Directory) is not null) continue;
                    var token = Guid.NewGuid().ToString("N");
                    var target = PackagePath(token);
                    CopyTree(package.Directory, target);
                    ValidatePackage(target, manifest.Id, manifest.Version);
                    if (_services is not null)
                    {
                        // Registered like an installation, so a policy pass cancels the hook once the policy refuses
                        // the bundle; a cancelled bundle is left out and its copy removed.
                        using var policyLinked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        lock (_sync) { _activeOperation = policyLinked; _activeRefusal = () => ExecutionRefusal(manifest.Id, target); }
                        try { await PortablePluginPackage.RunInstallationHookAsync(target, _services(manifest.Id), _host, null, uninstall: false, policyLinked.Token); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { TryDelete(target); continue; }
                        finally { lock (_sync) { _activeOperation = null; _activeRefusal = null; } }
                    }
                    next.Add(manifest.Id, new(token, manifest.Version));
                }
                // Judged once more before the index is written: a policy that changed during the hooks keeps a
                // refused bundle out; its directory is removed with the other orphans below.
                foreach (var (id, receipt) in next.ToArray())
                    if (RefusedForExecution(id, PackagePath(receipt.Directory))) next.Remove(id);
            }
            Commit(next);
            _initialized = true;
            var keep = next.Values.SelectMany(r => new[] { r.Directory, r.PendingDirectory }).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var dir in Directory.GetDirectories(Path.Combine(Root, "packages")))
                if (!keep.Contains(Path.GetFileName(dir))) TryDelete(dir);
        }
        finally { _operation.Release(); }
    }

    public bool IsInstalled(string id) { lock (_sync) return _installed.ContainsKey(id); }
    public string? InstalledVersion(string id) { lock (_sync) return _installed.GetValueOrDefault(id)?.Version; }
    /// <summary>Persistent, non-fatal feedback after rejecting a damaged staged update.</summary>
    public string? UpdateWarning(string id) { lock (_sync) return _installed.GetValueOrDefault(id)?.UpdateWarning; }
    /// <summary>Whether a staged update waits for a restart. One the policy refuses is not promoted by a restart, so none is advertised.</summary>
    public bool PendingRestart(string id)
    {
        Receipt? receipt;
        lock (_sync) receipt = _installed.GetValueOrDefault(id);
        if (receipt?.PendingDirectory is not { } pending) return false;
        return _policy is null || !(UpdateRefused(id) || RefusedForExecution(id, PackagePath(pending)));
    }
    public string Resolve(string id)
    {
        lock (_sync) return _installed.TryGetValue(id, out var receipt) ? PackagePath(receipt.Directory)
            : throw new InvalidOperationException("This plugin is not installed.");
    }
    public IReadOnlyList<InstalledPluginPackage> Inventory()
    {
        lock (_sync) return _installed.Select(pair => PortablePluginInventory.Inspect(PackagePath(pair.Value.Directory), _host)
            with { Directory = Path.Combine(InventoryRoot, pair.Key) }).ToArray();
    }

    public async Task<bool> InstallAsync(PortableCatalogEntry entry, IProgress<PluginInstallationProgress>? progress = null, CancellationToken ct = default)
    {
        entry.Validate();
        if (!entry.Supports(_host, PortablePluginCatalog.Architecture)) throw new InvalidDataException("This package does not support this host or architecture.");
        // Policy is checked before any byte is downloaded and before installation hooks run.
        if (InstallRefusal(entry) is { } refused) throw new TypeWhisper.Core.Models.ManagedPolicyDeniedException("plugin-download", refused);
        await _operation.WaitAsync(ct);
        // A policy pass cancels this installation through ReapplyPolicy once the policy refuses it, so a download or a
        // slow hook does not run on after the restriction took effect.
        var caller = ct;
        using var policyLinked = CancellationTokenSource.CreateLinkedTokenSource(caller);
        ct = policyLinked.Token;
        lock (_sync) { _activeOperation = policyLinked; _activeRefusal = () => InstallRefusal(entry); }
        string? package = null;
        var archive = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            EnsureInitialized();
            // Rechecked after the wait: a policy that changed meanwhile wins before any byte is downloaded.
            if (InstallRefusal(entry) is { } refusedNow) throw new TypeWhisper.Core.Models.ManagedPolicyDeniedException("plugin-download", refusedNow);
            lock (_sync)
                if (_installed.TryGetValue(entry.Id, out var current) && (current.PendingDirectory is not null || Version.Parse(current.Version) >= Version.Parse(entry.Version)))
                    throw new InvalidOperationException(current.PendingDirectory is not null ? "Restart TypeWhisper to finish the update." : "This version or a newer version is already installed.");
            progress?.Report(new("Downloading " + entry.Name + "…", 0));
            using var response = await _http.GetAsync(entry.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidDataException("The package redirected outside HTTPS.");
            if (response.Content.Headers.ContentLength is { } length && length != entry.Size) throw new InvalidDataException("Package size does not match the catalog.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(archive))
            {
                var buffer = new byte[81920]; long total = 0;
                while (true)
                {
                    using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    inactivity.CancelAfter(TimeSpan.FromSeconds(30));
                    var count = await input.ReadAsync(buffer, inactivity.Token);
                    if (count == 0) break;
                    total += count;
                    if (total > entry.Size) throw new InvalidDataException("Package exceeds the catalog size.");
                    // A policy that turned restrictive during the download stops it instead of letting it finish.
                    if (InstallRefusal(entry) is { } refusedDuring) throw new TypeWhisper.Core.Models.ManagedPolicyDeniedException("plugin-download", refusedDuring);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    progress?.Report(new("Downloading " + entry.Name + "…", (double)total / entry.Size));
                }
                if (total != entry.Size) throw new InvalidDataException("Incomplete plugin download.");
            }
            progress?.Report(new("Verifying package…"));
            await using (var stream = File.OpenRead(archive))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Package checksum does not match the catalog.");
            var token = Guid.NewGuid().ToString("N");
            package = PackagePath(token);
            progress?.Report(new("Extracting package…"));
            await Task.Run(() => Extract(archive, package, ct), ct);
            ValidatePackage(package, entry.Id, entry.Version);
            ct.ThrowIfCancellationRequested();
            // Judged once more after the download, now also by the extracted manifest rather than the catalog entry
            // alone: a policy that changed meanwhile, or a package that declares more than its entry, is refused
            // before the installation hook runs any package code and before the index records the package.
            if ((InstallRefusal(entry) ?? ExecutionRefusal(entry.Id, package)) is { } refusedLate)
                throw new TypeWhisper.Core.Models.ManagedPolicyDeniedException("plugin-download", refusedLate);
            Dictionary<string, Receipt> next;
            lock (_sync) next = new(_installed, StringComparer.Ordinal);
            var update = next.TryGetValue(entry.Id, out var previous);
            progress?.Report(new("Preparing " + entry.Name + "…"));
            if (_services is not null)
                await PortablePluginPackage.RunInstallationHookAsync(package, _services(entry.Id), _host, previous?.Version, uninstall: false, ct, progress);
            ct.ThrowIfCancellationRequested();
            // Judged once more after the hook, by the entry and by the extracted manifest: a policy revoked meanwhile
            // keeps the package out of the index.
            if ((InstallRefusal(entry) ?? ExecutionRefusal(entry.Id, package)) is { } refusedAfterHook)
                throw new TypeWhisper.Core.Models.ManagedPolicyDeniedException("plugin-download", refusedAfterHook);
            next[entry.Id] = update ? previous! with { PendingDirectory = token, PendingVersion = entry.Version, UpdateWarning = null } : new(token, entry.Version);
            Commit(next);
            package = null;
            return update;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested && InstallRefusal(entry) is { } refusedMeanwhile)
        {
            // Cancelled by the policy pass, not by the caller: the reason is the refusal.
            throw new TypeWhisper.Core.Models.ManagedPolicyDeniedException("plugin-download", refusedMeanwhile);
        }
        finally
        {
            lock (_sync) { _activeOperation = null; _activeRefusal = null; }
            if (package is not null) TryDelete(package);
            try { File.Delete(archive); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _operation.Release();
        }
    }

    // Caller must drain and disable the runtime before removing its registration.
    // Keep credentials, preferences and model files for an eventual reinstall.
    public async Task UninstallAsync(string id, IProgress<PluginInstallationProgress>? progress = null, CancellationToken ct = default)
    {
        PortableCatalogEntry.ValidateId(id);
        await _operation.WaitAsync(ct);
        try
        {
            EnsureInitialized();
            Dictionary<string, Receipt> next;
            lock (_sync) next = new(_installed, StringComparer.Ordinal);
            progress?.Report(new("Running plugin cleanup…"));
            // A package the policy refuses is removed without running its code: the hook would load the assembly.
            // A hook already running is cancelled by the policy pass once the policy refuses the package.
            if (next.TryGetValue(id, out var previous) && _services is not null && !RefusedForExecution(id, PackagePath(previous.Directory)))
            {
                var directory = PackagePath(previous.Directory);
                using var policyLinked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_sync) { _activeOperation = policyLinked; _activeRefusal = () => ExecutionRefusal(id, directory); }
                try
                {
                    // Judged again once the operation is registered: a policy pass in between could not cancel it.
                    if (ExecutionRefusal(id, directory) is null)
                        await PortablePluginPackage.RunInstallationHookAsync(directory, _services(id), _host, previous.Version, uninstall: true, policyLinked.Token, progress);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                finally { lock (_sync) { _activeOperation = null; _activeRefusal = null; } }
            }
            ct.ThrowIfCancellationRequested();
            if (!next.Remove(id)) throw new InvalidOperationException("This plugin is not installed.");
            Commit(next);
        }
        finally { _operation.Release(); }
    }

    // Why policy refuses running this package's code, judged by its manifest on disk, or null. A package whose
    // manifest cannot be read is judged by the allowlist alone.
    private string? ExecutionRefusal(string pluginId, string directory)
    {
        var policy = _policy?.Invoke() ?? TypeWhisper.Core.Models.ManagedProviderPolicy.None;
        var manifest = PortablePluginInventory.Inspect(directory, _host).Manifest;
        return manifest is null ? policy.PluginRefusal(pluginId)
            : policy.PackageRefusal(pluginId, PluginLocalityMap.ToContentLocality(manifest.ResolvedLocality), manifest.ResolvedCategories);
    }

    private bool RefusedForExecution(string pluginId, string directory) => ExecutionRefusal(pluginId, directory) is not null;

    // Whether policy refuses installing or updating this package right now.
    private bool UpdateRefused(string pluginId) => _policy?.Invoke() is { } policy && (policy.PluginDownloadRefusal() ?? policy.PluginRefusal(pluginId)) is not null;

    private void ValidatePackage(string directory, string id, string version)
    {
        var package = PortablePluginInventory.Inspect(directory, _host);
        if (package.Error is not null) throw new InvalidDataException(package.Error);
        if (package.Manifest?.Id != id || package.Manifest.Version != version) throw new InvalidDataException("Package identity or version does not match the catalog.");
        if (package.Manifest.IsInternalDependency)
            throw new InvalidDataException("Internal dependencies must be installed with their parent package.");
        var dependencies = Path.Combine(directory, "Dependencies");
        if (Directory.Exists(dependencies))
            foreach (var dependency in PortablePluginInventory.Scan(dependencies, _host))
                if (dependency.Error is not null) throw new InvalidDataException(dependency.Error);
        var required = package.Manifest.BundledDependencies;
        if (required is null || required.Distinct(StringComparer.Ordinal).Count() != required.Count)
            throw new InvalidDataException("Invalid bundled dependency declarations.");
        foreach (var dependencyId in required)
        {
            PortableCatalogEntry.ValidateId(dependencyId);
            if (dependencyId == id) throw new InvalidDataException("A package cannot depend on itself.");
            var dependency = PortablePluginInventory.Inspect(Path.Combine(dependencies, dependencyId), _host);
            if (dependency.Error is not null || dependency.Manifest?.Id != dependencyId)
                throw new InvalidDataException("A required bundled dependency is missing or invalid: " + dependencyId);
        }
    }

    private void EnsureInitialized() { if (!_initialized) throw new InvalidOperationException("Plugin storage has not initialized."); }
    private string PackagePath(string token)
    {
        if (!Guid.TryParseExact(token, "N", out _)) throw new InvalidDataException("Invalid installed package path.");
        return Path.Combine(Root, "packages", token);
    }
    private void Commit(Dictionary<string, Receipt> next)
    {
        var path = Path.Combine(Root, "installed.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(next, PortablePluginCatalog.Json));
        File.Move(path + ".tmp", path, true);
        lock (_sync) _installed = next;
    }
    private static void CopyTree(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked plugin folders are not supported.");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked plugin files are not supported.");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.GetDirectories(source)) CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
    private static void Extract(string archive, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        using var zip = ZipFile.OpenRead(archive);
        long expanded = 0;
        if (zip.Entries.Count > 20000) throw new InvalidDataException("Too many files in plugin package.");
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            expanded += entry.Length;
            var segments = entry.FullName.Replace('\\', '/').TrimEnd('/').Split('/');
            if (expanded > 4 * MaximumPackageBytes || segments.Any(s => s is "" or "." or ".." || s.Contains(':') || s.EndsWith('.') || s.EndsWith(' ')) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unsafe plugin archive entry.");
            var path = Path.GetFullPath(Path.Combine(target, Path.Combine(segments)));
            if (!path.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe plugin archive path.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: false);
            }
        }
    }
    private void TryDelete(string path)
    {
        // Only immutable GUID package folders inside this store may be collected.
        if (!Guid.TryParseExact(Path.GetFileName(path), "N", out _) || !string.Equals(Path.GetFullPath(path), PackagePath(Path.GetFileName(path)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return;
        try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
