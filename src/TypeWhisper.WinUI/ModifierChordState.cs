namespace TypeWhisper.WinUI;

// A modifier-only shortcut fires on release, never while a larger chord is being typed.
internal sealed class ModifierChordState
{
    private readonly HashSet<int> _down = [];
    private string? _candidate;
    private bool _blocked;
    internal string? Process(int key, bool down, IReadOnlySet<string> bindings)
    {
        static string? Name(int key) => key switch
        {
            0x11 or 0xA2 or 0xA3 => "CTRL", 0x12 or 0xA4 or 0xA5 => "ALT",
            0x10 or 0xA0 or 0xA1 => "SHIFT", 0x5B or 0x5C => "WIN", _ => null
        };
        if (down)
        {
            if (!_down.Add(key)) return null;
            if (Name(key) is null) _blocked = true;
            var chord = string.Join("+", _down.Select(Name).Where(n => n is not null).Distinct()
                .OrderBy(n => n switch { "CTRL" => 0, "ALT" => 1, "SHIFT" => 2, _ => 3 }));
            if (!_blocked) _candidate = bindings.Contains(chord) ? chord : null;
            return null;
        }
        _down.Remove(key);
        if (_down.Count != 0) return null;
        var result = _blocked ? null : _candidate;
        _candidate = null; _blocked = false;
        return result;
    }
}
