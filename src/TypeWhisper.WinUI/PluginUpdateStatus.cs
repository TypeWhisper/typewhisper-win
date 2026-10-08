using TypeWhisper.Core.Services;
using TypeWhisper.PluginHost;

namespace TypeWhisper.WinUI;

internal static class PluginUpdateStatus
{
    /// <summary>The update progress or outcome worth showing, in the interface language.</summary>
    internal static string? Text(PortablePluginUpdates updates) =>
        updates.Busy ? updates.Progress is { } progress
            ? Loc.T("Updating {0} ({1}/{2})…", progress.Name, progress.Index, progress.Total) : Loc.T("Preparing updates…")
        : updates.Failed.Count > 0 ? Loc.T("{0} updated. Could not update: {1}. Try again.", updates.Updated, string.Join(", ", updates.Failed))
        : updates.RestartRequired ? Loc.T("Updates ready. Restart TypeWhisper to apply them.")
        : updates.CheckFailed ? Loc.T("Plugin updates could not be checked. Check your connection and try again.")
        : null;
}

/// <summary>Whether plugin updates are installed without asking; on unless the user turned it off.</summary>
internal sealed class PluginAutoUpdatePreference
{
    private readonly string _path;
    internal bool Enabled { get; private set; } = true;

    internal PluginAutoUpdatePreference(string path)
    {
        _path = path;
        try { if (File.Exists(path)) Enabled = File.ReadAllText(path).Trim() != "false"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppDiagnostics.Write("plugin.auto-update.read-failed", ex); }
    }

    /// <summary>Saves the choice; a failed write keeps the previous one.</summary>
    internal bool Save(bool enabled)
    {
        try { AtomicFileWriter.WriteAllText(_path, enabled ? "true" : "false"); Enabled = enabled; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
