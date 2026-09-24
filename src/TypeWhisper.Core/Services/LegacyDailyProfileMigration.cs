using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeWhisper.Core.Services;

/// <summary>A linked file or directory was found inside a legacy profile; the importer never follows it.</summary>
public sealed class LegacyImportLinkException(string path)
    : IOException("Legacy import does not follow linked files or directories: " + path)
{
    /// <summary>The linked path inside the legacy profile.</summary>
    public string LinkedPath { get; } = path;
}

/// <summary>Copies portable legacy data into an absent profile before any profile consumers start.</summary>
public static class LegacyDailyProfileMigration
{
    /// <summary>Receipt stored with the imported profile; version 2 includes the host's staged settings/plugin conversion.</summary>
    public const string ReceiptName = "legacy-import.json";
    private const string StagePrefix = ".typewhisper-import-";
    private static readonly Regex StageName = new("^" + Regex.Escape(StagePrefix) + "[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static readonly string[] Files = ["dictionary.json", "snippets.json", "workflows.json", "history.json"];
    private static readonly string[] Licenses = ["licenses.dat", "license.json"];

    /// <summary>Returns the first root holding data the extended importer reads; empty leftovers (#318) are skipped.</summary>
    public static string? SelectSource(params string[] candidates) => candidates.FirstOrDefault(HasImportableContent);

    /// <summary>Whether a legacy root has settings, profile data, licenses or non-empty plugin folders.</summary>
    /// <remarks>Empty directories created by 1.0 startup or an interrupted 1.0.4 migration do not count.
    /// Unreadable roots count, so the import reports the problem instead of silently starting fresh.</remarks>
    public static bool HasImportableContent(string legacyRoot)
    {
        try
        {
            var data = Path.Combine(legacyRoot, "Data");
            return File.Exists(Path.Combine(legacyRoot, "settings.json")) ||
                Files.Concat(Licenses).Any(name => File.Exists(Path.Combine(data, name))) ||
                new[] { "PluginData", "Plugins" }.Select(name => Path.Combine(legacyRoot, name)).Any(root => Directory.Exists(root) &&
                    Directory.EnumerateDirectories(root).Any(plugin => Directory.EnumerateFileSystemEntries(plugin).Any()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Imports once, publishing the complete profile with a same-volume directory rename.</summary>
    /// <remarks>The caller must own the destination's single-instance lock. Existing profiles are never merged or replaced.
    /// Sources are read only; private staging is disposable and a terminated attempt can be retried.
    /// Links at the legacy root or its ancestors are resolved; links inside the source are rejected.</remarks>
    public static async Task<bool> ImportAsync(string legacyRoot, string destination,
        Action<string>? checkpoint = null, CancellationToken cancellationToken = default,
        Func<string, string, CancellationToken, Task>? prepareProfile = null)
    {
        destination = ResolveDestination(destination);
        var parent = Path.GetDirectoryName(destination)!;
        DeleteStaleStages(parent);
        if (Directory.Exists(destination) || File.Exists(destination)) return false;
        if (!Directory.Exists(legacyRoot)) return false;
        legacyRoot = ResolveLinkedPath(legacyRoot);
        var source = Path.Combine(legacyRoot, "Data");
        RejectLinks(source, legacyRoot);
        foreach (var name in Files) RejectLinks(Path.Combine(source, name), legacyRoot);
        if (prepareProfile is null ? !Files.Any(name => File.Exists(Path.Combine(source, name))) : !HasImportableContent(legacyRoot))
            return false;

        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, StagePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            // Export from a private copy: the backup reader creates working directories beside the files it reads.
            // It validates types, identities, duplicate fields and size, and drops device-bound paths and credentials.
            var copy = Path.Combine(stage, ".legacy-data");
            Directory.CreateDirectory(copy);
            foreach (var name in Files.Where(name => File.Exists(Path.Combine(source, name))))
                await CopyBoundedAsync(Path.Combine(source, name), Path.Combine(copy, name), cancellationToken);
            var archive = await new PersistedProfileBackup(copy).ExportAsync(PersistedProfileBackup.SupportedCategories, cancellationToken);
            Directory.Delete(copy, recursive: true);
            checkpoint?.Invoke("read");
            var restore = new PersistedProfileBackup(stage);
            var preview = await restore.PreviewAsync(archive, PersistedProfileBackup.SupportedCategories, cancellationToken);
            if (preview.ChangedFileCount > 0)
            {
                var result = restore.Apply(preview);
                if (!result.Applied) throw new IOException(result.Error ?? "Legacy profile staging failed.");
            }
            if (prepareProfile is not null) await prepareProfile(legacyRoot, stage, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(stage, ReceiptName), JsonSerializer.Serialize(new
            {
                version = prepareProfile is null ? 1 : 2, importedAt = DateTimeOffset.UtcNow,
                categories = Files, sourcePreserved = true,
                excluded = prepareProfile is null
                    ? new[] { "settings", "credentials", "plugins", "models", "audio", "recordings", "recovery" }
                    : new[] { "account-sign-ins", "audio", "recordings", "recovery" }
            }), cancellationToken);
            checkpoint?.Invoke("staged");
            cancellationToken.ThrowIfCancellationRequested();
            RejectLink(destination);
            // No overwrite, no cross-volume move, and no visible partially imported profile.
            Directory.Move(stage, destination);
            return true;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    /// <summary>Publishes an otherwise empty profile whose receipt records that the user declined the import.</summary>
    /// <remarks>The legacy profile is not read or modified. Returns false when a profile already exists.</remarks>
    public static bool SkipImport(string destination)
    {
        destination = ResolveDestination(destination);
        var parent = Path.GetDirectoryName(destination)!;
        DeleteStaleStages(parent);
        if (Directory.Exists(destination) || File.Exists(destination)) return false;
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, StagePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            File.WriteAllText(Path.Combine(stage, ReceiptName), JsonSerializer.Serialize(new
            { version = 2, skipped = true, skippedAt = DateTimeOffset.UtcNow, sourcePreserved = true }));
            RejectLink(destination);
            Directory.Move(stage, destination);
            return true;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    /// <summary>Whether the profile holds a completed import receipt rather than a recorded skip.</summary>
    public static bool WasImported(string profileRoot)
    {
        var path = Path.Combine(profileRoot, ReceiptName);
        if (!File.Exists(path)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return !(document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("skipped", out var skipped) && skipped.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return true; }
    }

    /// <summary>Explains a failed import without exception internals; the legacy profile is never changed by a failure.</summary>
    public static string DescribeFailure(Exception error) => error switch
    {
        LegacyImportLinkException => "Your previous TypeWhisper profile contains a linked file or folder (symbolic link or junction). " +
            "Links inside the profile are not followed. Replace the link with the real files and retry, or start with a new profile.",
        JsonException or InvalidDataException => "A file in your previous TypeWhisper profile is damaged or has an unsupported format, " +
            "so it could not be imported. Retry after repairing it, or start with a new profile.",
        UnauthorizedAccessException or IOException => "Files of your previous TypeWhisper profile could not be read or copied. " +
            "Close the previous TypeWhisper version, check free disk space and retry, or start with a new profile.",
        _ => "Your previous TypeWhisper profile could not be imported. Retry, or start with a new profile."
    } + " Your previous data is unchanged either way.";

    /// <summary>Returns an equivalent path without symbolic links or junctions at the path or any ancestor.</summary>
    /// <remarks>Only for read-only sources and destination parents. Non-link reparse points are kept as they are.</remarks>
    public static string ResolveLinkedPath(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (var hops = 0; hops < 40; hops++)
        {
            string? link = null, target = null;
            // Resolve the outermost link first, like realpath; its target may introduce further links.
            foreach (var current in Ancestry(path).Reverse())
                if (IsLink(current) && Directory.ResolveLinkTarget(current, returnFinalTarget: true)?.FullName is { } resolved)
                { link = current; target = resolved; break; }
            if (link is null) return path;
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Join(target, Path.GetRelativePath(link, path))));
        }
        throw new IOException("The legacy profile path contains too many nested links.");
    }

    /// <summary>Rejects links at the path or its ancestors below the (already resolved) root.</summary>
    public static void RejectLinks(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (var current in Ancestry(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))))
        {
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) return;
            RejectLink(current);
        }
    }

    // Only this importer's own staging names are removed; the caller's single-instance lock excludes a live import.
    // Stale stages can hold multi-gigabyte models and re-encrypted secrets from an interrupted attempt.
    private static void DeleteStaleStages(string parent)
    {
        if (!Directory.Exists(parent)) return;
        foreach (var stale in Directory.EnumerateDirectories(parent, StagePrefix + "*"))
        {
            if (!StageName.IsMatch(Path.GetFileName(stale))) continue;
            try
            {
                if (IsLink(stale)) Directory.Delete(stale);
                else Directory.Delete(stale, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // Retried at the next import.
        }
    }

    // A redirected %LOCALAPPDATA% is followed; a linked profile root itself is still refused.
    private static string ResolveDestination(string destination)
    {
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var result = Path.Combine(ResolveLinkedPath(Path.GetDirectoryName(destination)!), Path.GetFileName(destination));
        RejectLink(result);
        return result;
    }

    private static async Task CopyBoundedAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = File.OpenRead(source);
        if (input.Length > 64 * 1024 * 1024) throw new InvalidDataException("A legacy profile file exceeds the 64 MiB limit.");
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, ct);
    }

    private static IEnumerable<string> Ancestry(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current)) yield return current;
    }

    private static bool IsLink(string path) =>
        (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void RejectLink(string path)
    {
        if (IsLink(path)) throw new LegacyImportLinkException(path);
    }
}
