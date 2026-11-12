namespace TypeWhisper.PluginHost;

/// <summary>
/// Limits what a transcription worker loads. The worker is the host executable itself, so it accepts only the
/// plugins the host isolates, and only from the host's own package folders: an installed package (one folder named
/// by a GUID below <paramref name="installedPackagesDirectory"/>) or a bundled plugin (a folder named by the plugin
/// ID below <paramref name="bundledPluginsDirectory"/>). The manifest in that folder must name the expected plugin.
/// </summary>
public sealed class TranscriptionWorkerPackagePolicy(IReadOnlySet<string> pluginIds, string installedPackagesDirectory,
    string? bundledPluginsDirectory = null)
{
    /// <summary>Returns why the worker must not load the package, or null when it may.</summary>
    public string? Refusal(string packageDirectory, string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || !pluginIds.Contains(pluginId))
            return "The transcription worker does not run this plugin.";
        string directory, parent, name;
        try
        {
            directory = Normalize(packageDirectory);
            parent = Path.GetDirectoryName(directory) ?? "";
            name = Path.GetFileName(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return "The transcription worker received an invalid plugin package path."; }

        var installed = SameDirectory(parent, installedPackagesDirectory) && Guid.TryParseExact(name, "N", out _);
        var bundled = bundledPluginsDirectory is not null && SameDirectory(parent, bundledPluginsDirectory)
            && string.Equals(name, pluginId, StringComparison.OrdinalIgnoreCase);
        if (!installed && !bundled)
            return "The transcription worker loads plugins only from the installed plugin folders.";
        try
        {
            // A junction or symbolic link could point the expected folder name at another directory.
            if (!Directory.Exists(directory) || IsReparsePoint(directory) || IsReparsePoint(parent))
                return "The transcription worker loads plugins only from the installed plugin folders.";
            var manifest = PortablePluginPackage.ReadManifest(directory);
            if (!string.Equals(manifest.Id, pluginId, StringComparison.Ordinal))
                return "The plugin package does not contain the expected plugin.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { return "The plugin package could not be read: " + ex.Message; }
        return null;
    }

    private static bool SameDirectory(string path, string root)
    {
        try { return string.Equals(path, Normalize(root), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
