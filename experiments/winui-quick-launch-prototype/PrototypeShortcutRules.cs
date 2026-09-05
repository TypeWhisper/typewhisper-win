namespace TypeWhisper.WinUIPrototype;

internal static class PrototypeShortcutRules
{
    internal static string[] Split(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    internal static string? Duplicate(string candidate, IEnumerable<string> current, int editingIndex) =>
        current.Where((_, index) => index != editingIndex).Any(value => Normalize(value) == Normalize(candidate))
            ? "This action already has that shortcut." : null;

    internal static string Upsert(IEnumerable<string> current, int index, string candidate)
    {
        var values = current.ToList();
        if (index < 0) values.Add(candidate);
        else values[index] = candidate;
        return string.Join(",", values);
    }

    internal static string RemoveAt(IEnumerable<string> current, int index) =>
        string.Join(",", current.Where((_, itemIndex) => itemIndex != index));

    internal static string Normalize(string value) => string.Join("+", value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.ToUpperInvariant() switch { "CONTROL" => "CTRL", "ESCAPE" => "ESC", "RETURN" => "ENTER", var token => token })
        .OrderBy(part => part switch { "CTRL" => 0, "ALT" => 1, "SHIFT" => 2, "WIN" => 3, _ => 4 }));

    internal static string? Validate(string candidate, bool allowModifiersOnly)
    {
        var parts = Normalize(candidate).Split('+');
        if (parts.Distinct().Count() != parts.Length) return "Use each key only once.";
        var keys = parts.Where(part => part is not ("CTRL" or "ALT" or "SHIFT" or "WIN")).ToArray();
        if (keys.Length == 0)
            return allowModifiersOnly && parts.Length >= 2 ? null : "Add a key to the modifier, such as Ctrl + Shift + K.";
        if (keys.Length != 1 || keys[0].Length == 0) return "Press one key together with your modifiers.";
        if (Normalize(candidate) is "ALT+F4" or "ALT+TAB" or "CTRL+ALT+DELETE") return "This combination is reserved by Windows.";
        if (parts.Length == 1 && !(keys[0].StartsWith('F') && int.TryParse(keys[0][1..], out var n) && n is >= 1 and <= 24))
            return "Use Ctrl, Alt or Shift with the key, or choose a function key.";
        return null;
    }

    internal static string? Conflict(string candidate, string ownKey, IEnumerable<(string Key, string Label, string Value)> bindings)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var normalized = Normalize(candidate);
        foreach (var binding in bindings.Where(binding => binding.Key != ownKey))
            if (binding.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(value => Normalize(value) == normalized))
                return $"Already used by {binding.Label}. Change that shortcut first.";
        return null;
    }
}
