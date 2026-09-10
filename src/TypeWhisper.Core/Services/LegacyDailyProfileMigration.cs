using System.Text.Json;

namespace TypeWhisper.Core.Services;

/// <summary>Copies portable legacy data into an absent profile before any profile consumers start.</summary>
public static class LegacyDailyProfileMigration
{
    /// <summary>Receipt stored with the imported profile. Settings, plugins, secrets and audio are not imported.</summary>
    public const string ReceiptName = "legacy-import.json";
    private static readonly string[] Files = ["dictionary.json", "snippets.json", "workflows.json", "history.json"];

    /// <summary>Imports once, publishing the complete profile with a same-volume directory rename.</summary>
    /// <remarks>The caller must own the destination's single-instance lock. Existing profiles are never merged or replaced.
    /// Sources are read only; private staging is disposable and a terminated attempt can be retried.</remarks>
    public static async Task<bool> ImportAsync(string legacyRoot, string destination,
        Action<string>? checkpoint = null, CancellationToken cancellationToken = default)
    {
        legacyRoot = Path.GetFullPath(legacyRoot);
        destination = Path.GetFullPath(destination);
        RejectLinks(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) return false;
        RejectLinks(legacyRoot);
        var source = Path.Combine(legacyRoot, "Data");
        RejectLinks(source);
        if (!Directory.Exists(source)) return false;
        foreach (var name in Files) RejectLinks(Path.Combine(source, name));
        if (!Files.Any(name => File.Exists(Path.Combine(source, name)))) return false;

        // The backup reader validates types, identities, duplicate fields and size before conversion.
        // Its portable format deliberately removes device-bound paths and credentials.
        var archive = await new PersistedProfileBackup(source).ExportAsync(PersistedProfileBackup.SupportedCategories, cancellationToken);
        checkpoint?.Invoke("read");
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".typewhisper-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var restore = new PersistedProfileBackup(stage);
            var preview = await restore.PreviewAsync(archive, PersistedProfileBackup.SupportedCategories, cancellationToken);
            var result = restore.Apply(preview);
            if (!result.Applied) throw new IOException(result.Error ?? "Legacy profile staging failed.");
            await File.WriteAllTextAsync(Path.Combine(stage, ReceiptName), JsonSerializer.Serialize(new
            {
                version = 1, importedAt = DateTimeOffset.UtcNow,
                categories = Files, sourcePreserved = true,
                excluded = new[] { "settings", "credentials", "plugins", "models", "audio", "recordings", "recovery" }
            }), cancellationToken);
            checkpoint?.Invoke("staged");
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(destination);
            // No overwrite, no cross-volume move, and no visible partially imported profile.
            Directory.Move(stage, destination);
            return true;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Legacy import does not follow linked files or directories.");
        }
    }
}
