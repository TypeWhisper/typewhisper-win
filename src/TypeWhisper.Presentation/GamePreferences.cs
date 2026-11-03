using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>Whether dictation writes into one game's chat. Everything else is set on the add-on's page in the game.</summary>
public sealed record GameChatPreferences
{
    /// <summary>Whether dictation goes into the game's chat while the game is in front.</summary>
    public bool Enabled { get; init; }
    /// <summary>The game folder the player chose for the add-on, when it was not found automatically.</summary>
    public string? Folder { get; init; }
}

/// <summary>Game chat settings.</summary>
public sealed record GamePreferences
{
    /// <summary>World of Warcraft chat settings.</summary>
    public GameChatPreferences WorldOfWarcraft { get; init; } = new();

    /// <summary>The game whose chat a dictation into <paramref name="processName"/> goes to, or null when it is no
    /// enabled game.</summary>
    public GameDefinition? GameFor(string? processName)
    {
        var game = GameCatalog.FindByProcess(processName);
        return game == GameCatalog.WorldOfWarcraft && WorldOfWarcraft.Enabled ? game : null;
    }

    /// <summary>Returns the chat delivery for a dictation into <paramref name="processName"/>, or null when it is no
    /// enabled game. Only the add-on opens a chat that is already open without closing it; without it, Enter opens the chat.</summary>
    public ChatDeliveryOptions? ChatFor(string? processName, WowAddOnSettings addOn, bool addOnInstalled)
    {
        if (GameFor(processName) is not { } game) return null;
        return new ChatDeliveryOptions
        {
            OpenKey = addOnInstalled ? ChatOpenKey.AddOn : ChatOpenKey.Enter, AddOnChord = game.AddOnChord, Submit = addOn.Send,
            MaxMessageBytes = game.MaxMessageBytes, MessageIntervalMilliseconds = game.MessageIntervalMilliseconds
        };
    }

    /// <summary>Returns the chords for the add-on's cast bar, or null when the dictation into <paramref name="processName"/>
    /// shows TypeWhisper's own indicator. Only an installed add-on with its bar turned on draws one.</summary>
    public GameIndicatorChords? IndicatorFor(string? processName, WowAddOnSettings addOn, bool addOnInstalled) =>
        addOnInstalled && addOn.ShowBar ? GameFor(processName)?.Indicator : null;
}

/// <summary>Loads and atomically saves game chat settings in an explicitly supplied profile.</summary>
public sealed class GamePreferencesStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private GamePreferences _current = new();
    /// <summary>The loaded or last successfully saved settings. Safe to read from any thread.</summary>
    public GamePreferences Current => Volatile.Read(ref _current);
    /// <summary>A user-facing load or save failure, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Loads settings without writing; missing or invalid settings leave every game off. Settings that moved to
    /// the add-on are ignored.</summary>
    public GamePreferencesStore(string path)
    {
        _path = path;
        try
        {
            var loaded = JsonSerializer.Deserialize<GamePreferences>(File.ReadAllText(path), Json);
            if (loaded?.WorldOfWarcraft is null) throw new JsonException("Invalid game settings.");
            _current = loaded;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // No settings saved yet: every game stays off.
            _current = new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Error = Loc.T("Game settings could not be loaded. Game chat is off until you save them again.");
        }
    }

    /// <summary>Persists the settings; a failed write keeps the previous ones.</summary>
    public string? Save(GamePreferences next)
    {
        try
        {
            AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(next, Json));
            Volatile.Write(ref _current, next);
            return Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error = Loc.T("Game settings could not be saved. Your previous settings still apply.");
        }
    }
}
