namespace TypeWhisper.Plugin.Games;

/// <summary>
/// A game whose text chat the plugin can write into.
/// </summary>
/// <param name="Id">Stable id used in settings.</param>
/// <param name="Name">Display name; a product name that is not translated.</param>
/// <param name="ProcessNames">Process names without .exe that belong to the game.</param>
/// <param name="MaxMessageBytes">Longest chat message in UTF-8 bytes, or null without a limit.</param>
/// <param name="AddOnChord">Virtual keys the TypeWhisper add-on binds to open the chat, modifiers first; empty without an add-on.</param>
/// <param name="AddOnChordName">The add-on chord as shown to the player.</param>
/// <param name="MessageIntervalMilliseconds">Pause between the messages of one long dictation.</param>
/// <param name="Indicator">Chords that tell the add-on what a dictation is doing, or null without an in-game indicator.</param>
internal sealed record GameDefinition(string Id, string Name, IReadOnlyList<string> ProcessNames, int? MaxMessageBytes,
    IReadOnlyList<ushort> AddOnChord, string AddOnChordName, int MessageIntervalMilliseconds, GameIndicatorChords? Indicator = null);

/// <summary>
/// Virtual keys, modifiers first, that the game add-on binds to show a dictation in the game. The add-on cannot
/// receive anything else from TypeWhisper, so these keys are the whole protocol.
/// </summary>
/// <param name="Recording">Recording started.</param>
/// <param name="Transcribing">Recording ended and the speech is being transcribed.</param>
/// <param name="Stopped">The dictation was canceled or failed. A finished dictation needs no chord: the add-on sees the chat open.</param>
internal sealed record GameIndicatorChords(IReadOnlyList<ushort> Recording, IReadOnlyList<ushort> Transcribing, IReadOnlyList<ushort> Stopped);

/// <summary>
/// Lists the games with chat support.
/// </summary>
internal static class GameCatalog
{
    /// <summary>
    /// World of Warcraft in all versions (Retail, Classic, Forever, test realms). Chat messages hold 255 UTF-8 bytes,
    /// and the server drops say and yell messages that follow each other too quickly. The add-on binds
    /// Ctrl+Shift+F11 to the game's Open Chat command; the game does not know F13 and higher. Ctrl+Shift+F8, F9 and
    /// F10 drive the add-on's cast bar.
    /// </summary>
    public static GameDefinition WorldOfWarcraft { get; } = new("wow", "World of Warcraft",
        ["Wow", "WowT", "WowB", "WowClassic", "WowClassicT", "WowClassicB"], 255, [0x11, 0x10, 0x7A], "Ctrl+Shift+F11", 1000,
        new([0x11, 0x10, 0x77], [0x11, 0x10, 0x78], [0x11, 0x10, 0x79]));

    /// <summary>
    /// Gets all games with chat support.
    /// </summary>
    public static IReadOnlyList<GameDefinition> All { get; } = [WorldOfWarcraft];

    /// <summary>
    /// Returns the game that owns the process, or null.
    /// </summary>
    public static GameDefinition? FindByProcess(string? processName) => string.IsNullOrWhiteSpace(processName) ? null
        : All.FirstOrDefault(game => game.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase));
}
