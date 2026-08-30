using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Materializes restored built-in and registry term packs independently of settings view-model construction.
/// </summary>
public sealed class BackupTermPackHandler : IBackupTermPackHandler
{
    private readonly IDictionaryService _dictionary;
    private readonly TermPackRegistryService _registry;
    private readonly LicenseService _license;

    /// <summary>Initializes the term-pack restore bridge.</summary>
    public BackupTermPackHandler(
        IDictionaryService dictionary,
        TermPackRegistryService registry,
        LicenseService license)
    {
        _dictionary = dictionary;
        _registry = registry;
        _license = license;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> MaterializeAsync(
        IReadOnlyList<string> enabledPackIds,
        CancellationToken cancellationToken = default)
    {
        var requested = enabledPackIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
            return [];

        var builtIn = TermPack.VisiblePacks(_license.HasCommercialLicense)
            .ToDictionary(pack => pack.Id, StringComparer.OrdinalIgnoreCase);
        ActivateAvailable(requested, builtIn, cancellationToken);
        if (requested.Count == 0)
            return [];

        var available = (await _registry.GetRemotePacksAsync(cancellationToken))
            .Where(pack => _license.HasCommercialLicense || !pack.RequiresCommercialLicense)
            .GroupBy(pack => pack.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(pack => pack.Id, StringComparer.OrdinalIgnoreCase);

        ActivateAvailable(requested, available, cancellationToken);

        return requested
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => $"Dictionary pack '{id}' is not available on this installation and was not activated.")
            .ToArray();
    }

    private void ActivateAvailable(
        ISet<string> requested,
        IReadOnlyDictionary<string, TermPack> available,
        CancellationToken cancellationToken)
    {
        foreach (var packId in requested.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!available.TryGetValue(packId, out var pack))
                continue;

            _dictionary.ActivatePack(pack);
            requested.Remove(packId);
        }
    }
}
