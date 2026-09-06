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
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<string, Receipt> _installed = new(StringComparer.Ordinal);
    private bool _initialized;
    private sealed record Receipt(string Directory, string Version, string? PendingDirectory = null, string? PendingVersion = null);
    public string Root { get; }
    public string InventoryRoot => Path.Combine(Root, "installed");
    public bool Initialized => _initialized;
    public PortablePluginStore(string root, Version host, HttpClient http, Func<string, IPluginHostServices>? services = null)
    { Root = Path.GetFullPath(root); _host = host; _http = http; _services = services; }

    public async Task InitializeAsync(string? bundledRoot = null, CancellationToken ct = default)
    {
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
                foreach (var (id, receipt) in next.ToArray())
                {
                    PortableCatalogEntry.ValidateId(id);
                    PackagePath(receipt.Directory);
                    if (receipt.PendingDirectory is not { } pending) continue;
                    ValidatePackage(PackagePath(pending), id, receipt.PendingVersion!);
                    next[id] = new(pending, receipt.PendingVersion!);
                }
            }
            else if (bundledRoot is not null && Directory.Exists(bundledRoot))
            {
                foreach (var package in PortablePluginInventory.Scan(bundledRoot, _host))
                {
                    if (package.Error is not null) throw new InvalidDataException(package.Error);
                    var manifest = package.Manifest!;
                    // Old dev outputs may still contain this once-separate dependency.
                    if (manifest.Id == "com.typewhisper.parakeet-ctc") continue;
                    var token = Guid.NewGuid().ToString("N");
                    var target = PackagePath(token);
                    CopyTree(package.Directory, target);
                    if (manifest.Id == "com.typewhisper.sherpa-onnx" && !Directory.Exists(Path.Combine(target, "Dependencies", "com.typewhisper.parakeet-ctc")))
                    {
                        var ctc = Path.Combine(bundledRoot, "com.typewhisper.parakeet-ctc");
                        if (Directory.Exists(ctc)) CopyTree(ctc, Path.Combine(target, "Dependencies", "com.typewhisper.parakeet-ctc"));
                    }
                    ValidatePackage(target, manifest.Id, manifest.Version);
                    if (_services is not null)
                        await PortablePluginPackage.RunInstallationHookAsync(target, _services(manifest.Id), _host, null, uninstall: false, ct);
                    next.Add(manifest.Id, new(token, manifest.Version));
                }
            }
            Commit(next);
            _initialized = true;
            var keep = next.Values.Select(r => r.Directory).ToHashSet(StringComparer.Ordinal);
            foreach (var dir in Directory.GetDirectories(Path.Combine(Root, "packages")))
                if (!keep.Contains(Path.GetFileName(dir))) TryDelete(dir);
        }
        finally { _operation.Release(); }
    }

    public bool IsInstalled(string id) { lock (_sync) return _installed.ContainsKey(id); }
    public string? InstalledVersion(string id) { lock (_sync) return _installed.GetValueOrDefault(id)?.Version; }
    public bool PendingRestart(string id) { lock (_sync) return _installed.GetValueOrDefault(id)?.PendingDirectory is not null; }
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
        await _operation.WaitAsync(ct);
        string? package = null;
        var archive = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            EnsureInitialized();
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
            Dictionary<string, Receipt> next;
            lock (_sync) next = new(_installed, StringComparer.Ordinal);
            var update = next.TryGetValue(entry.Id, out var previous);
            progress?.Report(new("Preparing " + entry.Name + "…"));
            if (_services is not null)
                await PortablePluginPackage.RunInstallationHookAsync(package, _services(entry.Id), _host, previous?.Version, uninstall: false, ct, progress);
            ct.ThrowIfCancellationRequested();
            next[entry.Id] = update ? previous! with { PendingDirectory = token, PendingVersion = entry.Version } : new(token, entry.Version);
            Commit(next);
            package = null;
            return update;
        }
        finally
        {
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
            if (next.TryGetValue(id, out var previous) && _services is not null)
                await PortablePluginPackage.RunInstallationHookAsync(PackagePath(previous.Directory), _services(id), _host, previous.Version, uninstall: true, ct, progress);
            ct.ThrowIfCancellationRequested();
            if (!next.Remove(id)) throw new InvalidOperationException("This plugin is not installed.");
            Commit(next);
        }
        finally { _operation.Release(); }
    }

    private void ValidatePackage(string directory, string id, string version)
    {
        var package = PortablePluginInventory.Inspect(directory, _host);
        if (package.Error is not null) throw new InvalidDataException(package.Error);
        if (package.Manifest?.Id != id || package.Manifest.Version != version) throw new InvalidDataException("Package identity or version does not match the catalog.");
        var dependencies = Path.Combine(directory, "Dependencies");
        if (Directory.Exists(dependencies))
            foreach (var dependency in PortablePluginInventory.Scan(dependencies, _host))
                if (dependency.Error is not null) throw new InvalidDataException(dependency.Error);
        if (id == "com.typewhisper.sherpa-onnx" &&
            PortablePluginInventory.Inspect(Path.Combine(dependencies, "com.typewhisper.parakeet-ctc"), _host) is var ctc &&
            (ctc.Error is not null || ctc.Manifest?.Id != "com.typewhisper.parakeet-ctc"))
            throw new InvalidDataException("The NVIDIA package is missing its dictionary boosting dependency.");
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
