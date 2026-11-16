namespace TypeWhisper.WinUI;

// How module hotkey actions map onto the host's shortcut settings: one settings key, one saved file and one
// RegisterHotKey id range per action.
internal static class ModuleHotkeys
{
    private const string KeyPrefix = "ModuleHotkeys:";
    private const string FilePrefix = "module-";
    private const string FileSuffix = "-hotkeys.txt";

    // Host shortcuts use id bases up to 0x8A00 (paste last transcription) with 0x200 apart. Module actions start
    // above them, 0x100 apart, and the last range ends below 0xC000, where RegisterHotKey's application ids end.
    internal const int FirstIdBase = 0x9000;
    internal const int IdSpacing = 0x100;
    internal const int MaximumCount = 32;

    internal static string SettingsKey(string actionId) => KeyPrefix + actionId;

    internal static bool TryActionId(string settingsKey, out string actionId)
    {
        var matches = settingsKey.StartsWith(KeyPrefix, StringComparison.Ordinal);
        actionId = matches ? settingsKey[KeyPrefix.Length..] : "";
        return matches;
    }

    // Identifiers are validated before they get here, so the name is a plain file name. Two identifiers that differ
    // only in case are rejected as duplicates, because Windows file names ignore case.
    internal static string FileName(string actionId) => FilePrefix + actionId + FileSuffix;

    internal static int IdBase(int index) => index is >= 0 and < MaximumCount
        ? FirstIdBase + index * IdSpacing
        : throw new ArgumentOutOfRangeException(nameof(index));
}
