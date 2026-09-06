using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

// Loading a package executes trusted local code, not a sandbox. Call only after
// explicit user enablement. Manifest inspection never executes assemblies.
public sealed class PortablePluginPackage : IAsyncDisposable
{
    private readonly PackageContext _context;
    private bool _disposed;
    private bool _activated;
    public ITypeWhisperPlugin Plugin { get; }
    private PortablePluginPackage(PackageContext context, ITypeWhisperPlugin plugin)
    { _context = context; Plugin = plugin; }

    public static PluginManifest ReadManifest(string directory)
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing plugin manifest.");
        if (!Regex.IsMatch(manifest.Id ?? "", @"^[a-z0-9]+(?:[.-][a-z0-9]+)+$", RegexOptions.CultureInvariant) ||
            string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.PluginClass) ||
            !Version.TryParse(manifest.Version, out _) ||
            string.IsNullOrWhiteSpace(manifest.AssemblyName) || manifest.AssemblyName.IndexOfAny(['/', '\\', ':']) >= 0 ||
            !manifest.AssemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid portable plugin manifest.");
        return manifest;
    }

    public static async Task<PortablePluginPackage> LoadAsync(string directory, IPluginHostServices services, Version hostVersion)
        => await OpenAsync(directory, services, hostVersion, activate: true);

    public static async Task RunInstallationHookAsync(string directory, IPluginHostServices services, Version hostVersion,
        string? previousVersion, bool uninstall, CancellationToken ct, IProgress<PluginInstallationProgress>? progress = null)
    {
        await using var package = await OpenAsync(directory, services, hostVersion, activate: false);
        if (package.Plugin is not IPluginInstallationLifecycle lifecycle) return;
        var context = new PluginInstallationContext(services, previousVersion, progress);
        if (uninstall) await lifecycle.OnUninstallAsync(context, ct);
        else await lifecycle.OnInstallAsync(context, ct);
    }

    private static async Task<PortablePluginPackage> OpenAsync(string directory, IPluginHostServices services, Version hostVersion, bool activate)
    {
        var manifest = ReadManifest(directory);
        if (manifest.MinHostVersion is not null &&
            (!Version.TryParse(manifest.MinHostVersion, out var minimum) || minimum > hostVersion))
            throw new InvalidDataException("The plugin requires a newer host.");
        var path = Path.GetFullPath(Path.Combine(directory, manifest.AssemblyName));
        var context = new PackageContext(path);
        ITypeWhisperPlugin? plugin = null;
        try
        {
            var assembly = context.LoadFromAssemblyPath(path);
            if (assembly.GetReferencedAssemblies().Any(a => a.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase"))
                throw new NotSupportedException("This host requires a portable plugin, not a WPF settings assembly.");
            var type = assembly.GetType(manifest.PluginClass, true)!;
            if (!typeof(ITypeWhisperPlugin).IsAssignableFrom(type) || type.IsAbstract)
                throw new InvalidDataException("The entry point does not implement the portable plugin contract.");
            plugin = (ITypeWhisperPlugin)Activator.CreateInstance(type)!;
            if (plugin.PluginId != manifest.Id || plugin.PluginVersion != manifest.Version)
                throw new InvalidDataException("Plugin identity does not match its manifest.");
            if (activate) await plugin.ActivateAsync(services);
            return new(context, plugin) { _activated = activate };
        }
        catch
        {
            if (plugin is not null)
            {
                if (activate) try { await plugin.DeactivateAsync(); } catch { /* Preserve the activation error. */ }
                try { plugin.Dispose(); } catch { /* Preserve the activation error. */ }
            }
            context.Unload();
            throw;
        }
    }

    // Owner must drain all plugin requests before disposal.
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_activated) await Plugin.DeactivateAsync(); }
        finally { try { Plugin.Dispose(); } finally { _context.Unload(); } }
    }

    private sealed class PackageContext(string mainAssembly) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(ITypeWhisperPlugin).Assembly.GetName().Name)
                return typeof(ITypeWhisperPlugin).Assembly;
            // Dependencies must not silently pull the legacy UI framework into WinUI.
            if (name.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase")
                throw new NotSupportedException("WPF dependencies are not supported by the portable host.");
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
