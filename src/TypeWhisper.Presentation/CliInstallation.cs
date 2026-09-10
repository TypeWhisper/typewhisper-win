using System.Security.Cryptography;
using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>The current user-local CLI installation.</summary>
public sealed record CliInstallationState(bool Bundled, bool Installed, bool CanRemove, bool InPath, string InstallPath);

/// <summary>Installs the bundled CLI separately from the legacy Windows application.</summary>
public sealed class CliInstallation
{
    private const string ManifestName = ".typewhisper-cli.json";
    private const string SharedRuntimeName = ".typewhisper-shared-runtime.json";
    private readonly string _bundle;
    private readonly string _destination;
    private readonly Func<string> _readPath;
    private readonly Action<string> _writePath;
    private readonly string? _profileDirectory;
    private string ManifestPath => Path.Combine(_destination, ManifestName);

    /// <summary>Uses the current application's bundle and the current user's environment.</summary>
    public CliInstallation(string? profileDirectory = null) : this(Path.Combine(AppContext.BaseDirectory, "Cli"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeWhisper", "1.1", "Cli"),
        () => Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "",
        value => Environment.SetEnvironmentVariable("PATH", value, EnvironmentVariableTarget.User), profileDirectory) { }

    /// <summary>Uses explicit filesystem roots and environment delegates for isolated operation and testing.</summary>
    public CliInstallation(string bundleDirectory, string installDirectory, Func<string> readUserPath, Action<string> writeUserPath, string? profileDirectory = null)
    {
        _bundle = Path.GetFullPath(bundleDirectory);
        _destination = Path.GetFullPath(installDirectory);
        _readPath = readUserPath;
        _writePath = writeUserPath;
        _profileDirectory = profileDirectory;
    }

    /// <summary>Reads bundle, executable, and user PATH availability without changing them.</summary>
    public CliInstallationState GetState() => new(File.Exists(Path.Combine(_bundle, "typewhisper.exe")),
        File.Exists(Path.Combine(_destination, "typewhisper.exe")), File.Exists(ManifestPath),
        ContainsPath(_readPath()), Path.Combine(_destination, "typewhisper.exe"));

    /// <summary>Copies the full runtime bundle and registers its directory in user PATH.</summary>
    public void Install()
    {
        if (!GetState().Bundled) throw new IOException("The CLI is not included in this build.");
        var previous = ReadManifest();
        var sources = ReadBundleSources();
        var files = sources.ToDictionary(file => file.Key, file => file.Value.Hash, StringComparer.OrdinalIgnoreCase);
        var profileBytes = _profileDirectory is null ? null : System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { profile_directory = Path.GetFullPath(_profileDirectory) }));
        if (profileBytes is not null) files["cli-profile.json"] = Convert.ToHexString(SHA256.HashData(profileBytes));
        if (files.ContainsKey(ManifestName)) throw new IOException("The CLI bundle contains a reserved file.");
        foreach (var file in files)
        {
            var target = OwnedPath(file.Key);
            if (File.Exists(target) && !IsOwned(previous, file.Key, Hash(target)))
                throw new IOException($"The existing file '{file.Key}' is not owned by this installation.");
        }
        Directory.CreateDirectory(_destination);
        // Journal old and new hashes before atomically replacing each file. Both
        // versions remain recognizable if the process stops before the commit.
        Save(previous);
        foreach (var file in files)
        {
            var target = OwnedPath(file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".cli-payload-{Guid.NewGuid():N}.tmp");
            try
            {
                if (file.Key == "cli-profile.json" && profileBytes is not null) File.WriteAllBytes(temporary, profileBytes);
                else File.Copy(sources[file.Key].Source, temporary);
                if (Hash(temporary) != file.Value) throw new IOException("The CLI bundle changed during installation. Try again.");
                // A recovered pending version becomes the old owned version before
                // a newer bundle replaces it, including after multiple failed updates.
                if (File.Exists(target))
                {
                    var currentHash = Hash(target);
                    if (!IsOwned(previous, file.Key, currentHash))
                        throw new IOException($"The existing file '{file.Key}' changed during installation.");
                    previous.Files[file.Key] = currentHash;
                }
                previous.PendingFiles[file.Key] = file.Value;
                Save(previous);
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            previous.Files[file.Key] = file.Value;
            previous.PendingFiles.Remove(file.Key);
            Save(previous);
        }
        foreach (var stale in previous.Files.Keys.Union(previous.PendingFiles.Keys, StringComparer.OrdinalIgnoreCase)
            .Except(files.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            DeleteOwned(stale, previous);
            previous.Files.Remove(stale);
            previous.PendingFiles.Remove(stale);
        }
        var currentPath = _readPath();
        if (!ContainsPath(currentPath))
        {
            // Keep ownership recoverable even if the process stops after updating PATH.
            previous.AddedPath = true;
            Save(previous);
            _writePath(currentPath + (currentPath.Length == 0 || currentPath.EndsWith(';') ? "" : ";") + _destination);
        }
        Save(previous);
    }

    /// <summary>Removes unchanged owned files and the PATH entry added by this installation.</summary>
    public void Remove()
    {
        if (!File.Exists(ManifestPath)) return;
        var manifest = ReadManifest();
        foreach (var file in manifest.Files.Keys.Union(manifest.PendingFiles.Keys, StringComparer.OrdinalIgnoreCase))
            DeleteOwned(file, manifest);
        if (manifest.AddedPath)
            _writePath(string.Join(';', _readPath().Split(';').Where(entry => !MatchesPath(entry))));
        File.Delete(ManifestPath);
    }

    private bool ContainsPath(string value) => value.Split(';').Any(MatchesPath);
    private bool MatchesPath(string value) => string.Equals(
        Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).TrimEnd('\\', '/'),
        _destination.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private Dictionary<string, (string Source, string Hash)> ReadBundleSources()
    {
        var sources = Directory.EnumerateFiles(_bundle, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetRelativePath(_bundle, path), SharedRuntimeName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(_bundle, path), path => (Source: path, Hash: Hash(path)), StringComparer.OrdinalIgnoreCase);
        var manifest = Path.Combine(_bundle, SharedRuntimeName);
        if (!File.Exists(manifest)) return sources; // Existing full bundles remain installable.
        var shared = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest))
            ?? throw new IOException("The shared CLI runtime manifest is invalid.");
        var appRoot = Path.GetDirectoryName(_bundle)!;
        foreach (var (name, hash) in shared)
        {
            // Shared payloads are root-level runtime files, never arbitrary parent paths.
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', ':']) >= 0
                || name is "." or ".."
                || string.Equals(name, ManifestName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, SharedRuntimeName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "cli-profile.json", StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(hash ?? "", "\\A[0-9A-Fa-f]{64}\\z"))
                throw new IOException("The shared CLI runtime manifest contains an invalid entry.");
            var source = Path.Combine(appRoot, name);
            for (var path = source; path is not null; path = Path.GetDirectoryName(path))
                if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Shared CLI runtime files must not use symbolic links.");
            if (!File.Exists(source) || !string.Equals(Hash(source), hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"The shared CLI runtime file '{name}' is missing or changed. Reinstall the app and try again.");
            if (!sources.TryAdd(name, (source, hash!.ToUpperInvariant()))) throw new IOException("The CLI bundle contains duplicate runtime entries.");
        }
        return sources;
    }
    private static bool IsOwned(Manifest manifest, string relative, string hash) =>
        (manifest.Files.TryGetValue(relative, out var committed) && committed == hash)
        || (manifest.PendingFiles.TryGetValue(relative, out var pending) && pending == hash);
    private void DeleteOwned(string relative, Manifest manifest)
    {
        var path = OwnedPath(relative);
        if (File.Exists(path) && IsOwned(manifest, relative, Hash(path))) File.Delete(path);
    }
    private string OwnedPath(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_destination, relative));
        if (Path.IsPathRooted(relative) || !path.StartsWith(_destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) || relative.Contains(':') || string.Equals(relative, ManifestName, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The CLI manifest contains an invalid path.");
        // Never follow directory junctions or symlinks during installation or removal.
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The CLI directory must not contain symbolic links.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The CLI file must not be a symbolic link.");
        return path;
    }
    private Manifest ReadManifest()
    {
        EnsureManifestLocation();
        if (!File.Exists(ManifestPath)) return new();
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath))
            ?? throw new IOException("The CLI manifest could not be read.");
        if (manifest.Files is null || manifest.PendingFiles is null) throw new IOException("The CLI manifest could not be read.");
        foreach (var key in manifest.Files.Keys.Concat(manifest.PendingFiles.Keys)) _ = OwnedPath(key);
        manifest.Files = new(manifest.Files, StringComparer.OrdinalIgnoreCase);
        manifest.PendingFiles = new(manifest.PendingFiles, StringComparer.OrdinalIgnoreCase);
        return manifest;
    }
    private void Save(Manifest manifest)
    {
        EnsureManifestLocation();
        var temporary = Path.Combine(_destination, $".cli-manifest-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest));
            File.Move(temporary, ManifestPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void EnsureManifestLocation()
    {
        _ = OwnedPath("typewhisper.exe");
        if (File.Exists(ManifestPath) && (File.GetAttributes(ManifestPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The CLI manifest must not be a symbolic link.");
    }
    private sealed class Manifest
    {
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PendingFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AddedPath { get; set; }
    }
}
