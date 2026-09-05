namespace TypeWhisper.WinUIPrototype;

internal sealed record PrototypeSettingSearchEntry(string Category, string Key, string Label, string Description, string Icon, string Keywords = "");

internal static class PrototypeSettingsSearch
{
    internal static IReadOnlyList<PrototypeSettingSearchEntry> Find(IEnumerable<PrototypeSettingSearchEntry> entries, string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];
        var phrase = string.Join(' ', terms);
        return entries.Where(entry => terms.All(term =>
                $"{entry.Label} {entry.Category} {entry.Description} {entry.Keywords}".Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(entry => entry.Label.Equals(phrase, StringComparison.OrdinalIgnoreCase) ? 3
                : entry.Label.StartsWith(phrase, StringComparison.OrdinalIgnoreCase) ? 2
                : entry.Label.Contains(phrase, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(entry => entry.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
