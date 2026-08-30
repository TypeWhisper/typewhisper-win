using TypeWhisper.Core.Models.Backup;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Exports and safely merges portable TypeWhisper user data.
/// </summary>
public interface IBackupRestoreService
{
    /// <summary>Exports the selected portable data categories as versioned JSON.</summary>
    Task<string> ExportAsync(
        BackupExportOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Validates JSON and returns metadata and category counts without changing data.</summary>
    BackupImportPreview PreviewImport(string json);

    /// <summary>Safely merges the selected categories from versioned backup JSON.</summary>
    Task<BackupImportResult> ImportAsync(
        string json,
        BackupImportOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional bridge for platform plugin discovery and installation.
/// </summary>
public interface IBackupPluginHandler
{
    /// <summary>Returns registry references for installed plugins without private plugin data.</summary>
    Task<IReadOnlyList<BackupPlugin>> ExportAsync(CancellationToken cancellationToken = default);

    /// <summary>Restores plugins from registry references and reports partial outcomes.</summary>
    Task<BackupPluginImportResult> ImportAsync(
        IReadOnlyList<BackupPlugin> plugins,
        CancellationToken cancellationToken = default);
}
