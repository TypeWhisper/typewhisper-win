using System.Text.Json;

namespace TypeWhisper.WinUI;

// The shared licensing service uses the existing English resources in this English host.
internal sealed class LicenseText
{
    internal static LicenseText Instance { get; } = new();
    private readonly Dictionary<string, string> _strings;
    private LicenseText()
    {
        using var stream = typeof(LicenseText).Assembly.GetManifestResourceStream("TypeWhisper.LicenseStrings.json")!;
        _strings = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
    internal string this[string key] => _strings.GetValueOrDefault(key, key);
}
