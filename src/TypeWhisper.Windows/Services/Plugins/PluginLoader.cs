using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// A loaded plugin with its manifest, instance, load context, and directory.
/// </summary>
public sealed record LoadedPlugin(
    PluginManifest Manifest,
    ITypeWhisperPlugin Instance,
    PluginAssemblyLoadContext LoadContext,
    string PluginDirectory);

/// <summary>
/// Isolated assembly load context for each plugin, enabling per-plugin dependency resolution.
/// Collectible so plugins can be unloaded.
/// </summary>
public sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }
}

/// <summary>
/// Discovers and loads plugins from one or more search directories.
/// Each plugin resides in a subdirectory containing a manifest.json file.
/// </summary>
public sealed partial class PluginLoader
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Scans the given directories for plugin subdirectories, loads their manifests,
    /// and instantiates plugin classes. Broken plugins are logged and skipped.
    /// </summary>
    public List<LoadedPlugin> DiscoverAndLoad(IEnumerable<string> searchDirectories)
    {
        var loaded = new List<LoadedPlugin>();

        foreach (var searchDir in searchDirectories)
        {
            if (!Directory.Exists(searchDir))
            {
                Debug.WriteLine($"[PluginLoader] Search directory does not exist: {searchDir}");
                continue;
            }

            foreach (var pluginDir in Directory.GetDirectories(searchDir))
            {
                try
                {
                    var plugin = LoadPlugin(pluginDir);
                    if (plugin is not null)
                    {
                        loaded.Add(plugin);
                        Debug.WriteLine($"[PluginLoader] Loaded plugin: {plugin.Manifest.Id} v{plugin.Manifest.Version} from {pluginDir}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PluginLoader] Failed to load plugin from {pluginDir}: {ex.Message}");
                }
            }
        }

        return loaded;
    }

    private LoadedPlugin? LoadPlugin(string pluginDir)
    {
        var manifestPath = Path.Combine(pluginDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Debug.WriteLine($"[PluginLoader] No manifest.json in {pluginDir}, skipping");
            return null;
        }

        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson, ManifestJsonOptions);
        if (manifest is null)
        {
            Debug.WriteLine($"[PluginLoader] Failed to deserialize manifest in {pluginDir}");
            return null;
        }

        var assemblyPath = Path.Combine(pluginDir, manifest.AssemblyName);
        if (!File.Exists(assemblyPath))
        {
            Debug.WriteLine($"[PluginLoader] Assembly not found: {assemblyPath}");
            return null;
        }

        // Remove "Mark of the Web" from all plugin files to prevent SmartScreen blocks
        UnblockDirectory(pluginDir);

        var loadContext = new PluginAssemblyLoadContext(assemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

        var pluginType = assembly.GetType(manifest.PluginClass);
        if (pluginType is null)
        {
            Debug.WriteLine($"[PluginLoader] Plugin class '{manifest.PluginClass}' not found in {assemblyPath}");
            loadContext.Unload();
            return null;
        }

        if (!typeof(ITypeWhisperPlugin).IsAssignableFrom(pluginType))
        {
            Debug.WriteLine($"[PluginLoader] Class '{manifest.PluginClass}' does not implement ITypeWhisperPlugin");
            loadContext.Unload();
            return null;
        }

        var instance = Activator.CreateInstance(pluginType) as ITypeWhisperPlugin;
        if (instance is null)
        {
            Debug.WriteLine($"[PluginLoader] Failed to create instance of '{manifest.PluginClass}'");
            loadContext.Unload();
            return null;
        }

        return new LoadedPlugin(manifest, instance, loadContext, pluginDir);
    }

    /// <summary>
    /// Removes the Zone.Identifier alternate data stream (Mark of the Web) from all
    /// DLL and JSON files in a directory, preventing Windows SmartScreen from blocking them.
    /// </summary>
    private static void UnblockDirectory(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.*"))
            {
                var ext = Path.GetExtension(file);
                if (ext is ".dll" or ".json")
                    UnblockFile(file);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginLoader] Failed to unblock files in {directory}: {ex.Message}");
        }
    }

    private static void UnblockFile(string filePath)
    {
        // The Zone.Identifier is stored as an NTFS alternate data stream.
        // DeleteFile on "path:Zone.Identifier" removes it.
        DeleteFile(filePath + ":Zone.Identifier");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "DeleteFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteFile(string lpFileName);
}
