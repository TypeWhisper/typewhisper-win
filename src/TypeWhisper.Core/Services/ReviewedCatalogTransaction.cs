namespace TypeWhisper.Core.Services;

/// <summary>Commits a validated catalog only while its reviewed baseline is still current.</summary>
public static class ReviewedCatalogTransaction
{
    /// <summary>Reads an existing catalog, distinguishing a missing file from unreadable storage.</summary>
    public static string? Read(string path)
    {
        try { return File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>Atomically writes reviewed content under the profile mutation gate; a stale review writes nothing.</summary>
    public static void Commit(string path, string? baseline, string next)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!string.Equals(Read(path), baseline, StringComparison.Ordinal))
            throw new CatalogChangedException(Loc.T("Your list changed while you were reviewing. Nothing was imported. Start the import again to review the updated list."));
        AtomicFileWriter.WriteAllText(path, next);
    }
}

/// <summary>The catalog changed after it was read; the attempted write was not committed.</summary>
public sealed class CatalogChangedException(string message) : InvalidOperationException(message);
