using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

public sealed record InstalledPluginPackage(string Directory, PluginManifest? Manifest, string? Error);

// Metadata-only discovery: listing packages never activates plugin code.
public static class PortablePluginInventory
{
    public static IReadOnlyList<InstalledPluginPackage> Scan(string root, Version hostVersion)
    {
        if (!Directory.Exists(root)) return [];
        string[] directories;
        try { directories = Directory.GetDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return [new(root, null, "Plugin folder could not be read: " + ex.Message)]; }
        var results = directories.Order(StringComparer.OrdinalIgnoreCase).Select(path => Inspect(path, hostVersion)).ToArray();
        var duplicates = results.Where(p => p.Manifest is not null).GroupBy(p => p.Manifest!.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        return results.Select(p => p.Manifest is not null && duplicates.Contains(p.Manifest.Id)
            ? p with { Error = "Duplicate plugin identity. Keep only one package for this plugin." } : p).ToArray();
    }

    public static InstalledPluginPackage Inspect(string directory, Version hostVersion)
    {
        PluginManifest? manifest = null;
        try
        {
            manifest = PortablePluginPackage.ReadManifest(directory);
            if (manifest.MinHostVersion is not null &&
                (!Version.TryParse(manifest.MinHostVersion, out var minimum) || minimum > hostVersion))
                return new(directory, manifest, "This plugin requires a newer host version.");
            using var stream = File.OpenRead(Path.Combine(directory, manifest.AssemblyName));
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return new(directory, manifest, "The plugin assembly has no managed metadata.");
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if (name is "PresentationFramework" or "PresentationCore" or "WindowsBase")
                    return new(directory, manifest, "This package uses WPF and needs a portable WinUI-compatible build.");
            }
            return new(directory, manifest, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or BadImageFormatException or ArgumentException)
        { return new(directory, manifest, "Plugin package could not be read: " + ex.Message); }
    }
}
