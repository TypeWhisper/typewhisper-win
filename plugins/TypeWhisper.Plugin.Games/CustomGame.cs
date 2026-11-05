using System.Diagnostics;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Games;

/// <summary>A game the user added to the list, saved with the plugin's settings.</summary>
internal sealed record CustomGameSettings
{
    /// <summary>Stable id: "c" and 32 hexadecimal digits.</summary>
    public required string Id { get; init; }
    /// <summary>The name shown in the list.</summary>
    public required string Name { get; init; }
    /// <summary>Whether dictation goes into the game's chat.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>The game's process names without ".exe"; empty until the user enters one.</summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
    /// <summary>The key that opens the chat, one of <see cref="CustomGame.ChatKeys"/>.</summary>
    public string ChatKey { get; init; } = "enter";
    /// <summary>Whether Enter sends each message.</summary>
    public bool Send { get; init; } = true;
    /// <summary>The longest message in UTF-8 bytes, or null without a limit.</summary>
    public int? MaxMessageBytes { get; init; }
    /// <summary>The language to translate into, one of <see cref="CustomGame.Languages"/>, or null.</summary>
    public string? TranslateTo { get; init; }
}

/// <summary>
/// A game the user added. TypeWhisper opens its chat with the chosen key, pastes the text and presses Enter. Without
/// an add-on the game cannot say whether its chat is open, and the key would close an open one, so dictation starts
/// with the chat closed, and TypeWhisper's own indicator shows the dictation.
/// </summary>
internal sealed class CustomGame(CustomGameSettings settings, Func<IReadOnlyList<string>>? runningPrograms = null) : Game
{
    internal const int MaxNameLength = 60, MaxProcessNames = 5, MaxProcessNameLength = 100;
    /// <summary>Keys a game's chat commonly opens with. The value is saved; the virtual key is pressed.</summary>
    internal static readonly IReadOnlyList<(string Value, ushort[] Keys)> ChatKeys =
        [("enter", [ChatDeliveryOperation.EnterKey]), ("t", [0x54]), ("y", [0x59]), ("u", [0x55]), ("none", [])];
    internal static readonly int[] Limits = [100, 127, 150, 200, 255, 256, 500];
    /// <summary>Languages to translate into, by the English name TypeWhisper gives the LLM.</summary>
    internal static readonly string[] Languages =
        ["English", "German", "French", "Spanish", "Italian", "Portuguese", "Russian", "Polish", "Turkish", "Japanese", "Korean", "Chinese"];
    // Without a limit from the game, messages still go out one second apart so chat throttles keep them.
    private const int MessageIntervalMilliseconds = 1000;

    public CustomGameSettings Settings => settings;
    public override string Id => settings.Id;
    public override string Name => settings.Name;
    public override bool Enabled => settings.Enabled;
    public override IReadOnlyList<string> ProcessNames => settings.ProcessNames;

    public override GamePlan Prepare(DictationTargetInfo target, PluginLlmStatus? llm) => new(new ChatDeliveryOptions
    {
        OpenKeys = ChatKeys.Where(key => key.Value == settings.ChatKey).Select(key => key.Keys).FirstOrDefault() ?? [ChatDeliveryOperation.EnterKey],
        Submit = settings.Send, MaxMessageBytes = settings.MaxMessageBytes, MessageIntervalMilliseconds = MessageIntervalMilliseconds
    }, TranslateTo: settings.TranslateTo);

    public override string Describe(GameText text, PluginLlmStatus? llm)
    {
        var about = text.L("While {0} is in front, dictation opens its chat with the key you choose, inserts the text and sends it. Start dictating with the chat closed, since the key would close an open chat. TypeWhisper's own indicator shows the dictation.",
            "Ist {0} im Vordergrund, öffnet das Diktat seinen Chat mit der gewählten Taste, fügt den Text ein und sendet ihn. Starte mit geschlossenem Chat, denn die Taste würde einen offenen Chat schließen. TypeWhisper zeigt das Diktat mit der eigenen Anzeige.", Name);
        var notes = new List<string>();
        if (!Enabled) notes.Add(text.L("Turned off: TypeWhisper pastes into this game as into any other app.", "Ausgeschaltet: TypeWhisper fügt in dieses Spiel ein wie in jede andere App."));
        else if (ProcessNames.Count == 0)
            notes.Add(text.L("Enter the game's program name and save, so TypeWhisper recognizes the game.", "Gib den Programmnamen des Spiels ein und speichere, damit TypeWhisper das Spiel erkennt."));
        if (Enabled && settings.TranslateTo is not null && llm is not { Ready: true })
            notes.Add(text.L("A translation is chosen, but the default LLM is missing or unavailable, so messages go out untranslated. Open Default LLM in Workflows to choose one.",
                "Eine Übersetzung ist gewählt, aber das Standard-LLM fehlt oder ist nicht verfügbar, deshalb gehen Nachrichten unübersetzt raus. Öffne „Standard-LLM“ unter Workflows, um eines zu wählen."));
        return notes.Count == 0 ? about : about + "\n\n" + string.Join(" ", notes);
    }

    public override IReadOnlyList<PluginTextSetting> Fields(GameText text) =>
    [
        Field("name", text.L("Name", "Name"), "", settings.Name, MaxNameLength),
        Field("enabled", text.L("Dictate into the chat", "In den Chat diktieren"),
            text.L("Off keeps the game in the list while TypeWhisper pastes into it as into any other app.", "Aus behält das Spiel in der Liste, während TypeWhisper wie in jede andere App einfügt."),
            settings.Enabled ? "true" : "false") with { Choices = text.OnOff },
        Field("process", text.L("Program name", "Programmname"),
            text.L("The game's program as Task Manager shows it, without .exe. Start the game first to pick it from the list. Separate several names with commas.",
                "Das Programm des Spiels, wie es der Task-Manager zeigt, ohne .exe. Starte das Spiel zuerst, um es aus der Liste zu wählen. Mehrere Namen trennst du mit Kommas."),
            string.Join(", ", settings.ProcessNames), MaxProcessNames * (MaxProcessNameLength + 2)) with { Suggestions = (runningPrograms ?? RunningPrograms)() },
        Field("chatKey", text.L("Open the chat with", "Chat öffnen mit"),
            text.L("The key the game opens its text chat with. With Nothing, open the chat yourself before you dictate; text that needs more than one message then stays on the clipboard.",
                "Die Taste, mit der das Spiel seinen Text-Chat öffnet. Bei Nichts öffnest du den Chat selbst, bevor du diktierst; Text, der mehr als eine Nachricht braucht, bleibt dann in der Zwischenablage."),
            settings.ChatKey) with
        {
            Choices = [new("enter", "Enter"), new("t", "T"), new("y", "Y"), new("u", "U"),
                new("none", text.L("Nothing: I open the chat myself", "Nichts: Ich öffne den Chat selbst"))]
        },
        Field("send", text.L("Send with Enter", "Mit Enter senden"),
            text.L("Off leaves the text in the chat so you can check it before you send it.", "Aus lässt den Text im Chat, damit du ihn vor dem Senden prüfen kannst."),
            settings.Send ? "true" : "false") with { Choices = text.OnOff },
        Field("limit", text.L("Longest message", "Längste Nachricht"),
            text.L("Longer text goes out as several messages, one second apart. Counted in bytes, so letters such as ä or é take two.",
                "Längerer Text geht als mehrere Nachrichten raus, eine Sekunde nacheinander. Gezählt in Bytes, Buchstaben wie ä oder é zählen also doppelt."),
            settings.MaxMessageBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none") with
        {
            Choices = [new("none", text.L("No limit", "Keine Grenze")), .. Limits.Select(limit => new PluginSettingChoice(
                limit.ToString(System.Globalization.CultureInfo.InvariantCulture), text.L("{0} bytes", "{0} Bytes", limit)))]
        },
        Field("translate", text.L("Translate my messages into", "Meine Nachrichten übersetzen in"),
            text.L("TypeWhisper translates each dictation with the default LLM of its workflows before it goes into the chat. A workflow of your own for the game applies instead.",
                "TypeWhisper übersetzt jedes Diktat mit dem Standard-LLM seiner Workflows, bevor es in den Chat geht. Ein eigener Workflow für das Spiel gilt stattdessen."),
            settings.TranslateTo ?? "off") with
        {
            Choices = [new("off", text.L("Off", "Aus")), .. Languages.Select(language => new PluginSettingChoice(language, LanguageName(language, text)))]
        }
    ];

    public override GamesSettings Save(GamesSettings saved, IReadOnlyDictionary<string, string> values, GameText text)
    {
        var name = Value(values, "name", settings.Name).Trim();
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
            throw new ArgumentException(text.L("Enter a name for the game.", "Gib einen Namen für das Spiel ein."));
        var chatKey = Value(values, "chatKey", settings.ChatKey);
        if (ChatKeys.All(key => key.Value != chatKey)) throw new ArgumentException("Invalid chat key.", nameof(values));
        var limit = Value(values, "limit", settings.MaxMessageBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
        int? maxBytes = null;
        if (limit != "none")
        {
            if (!int.TryParse(limit, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes) || !Limits.Contains(bytes))
                throw new ArgumentException("Invalid message limit.", nameof(values));
            maxBytes = bytes;
        }
        var translate = Value(values, "translate", settings.TranslateTo ?? "off");
        if (translate != "off" && !Languages.Contains(translate)) throw new ArgumentException("Invalid language.", nameof(values));
        var next = settings with
        {
            Name = name, Enabled = Switch(values, "enabled", settings.Enabled), ProcessNames = ParseProcessNames(Value(values, "process", string.Join(", ", settings.ProcessNames)), text),
            ChatKey = chatKey, Send = Switch(values, "send", settings.Send), MaxMessageBytes = maxBytes, TranslateTo = translate == "off" ? null : translate
        };
        return saved with { CustomGames = saved.CustomGames.Select(game => game.Id == next.Id ? next : game).ToArray() };
    }

    /// <summary>Reads comma-separated program names. A name may end in .exe, but must not name a folder.</summary>
    internal static IReadOnlyList<string> ParseProcessNames(string value, GameText text)
    {
        var names = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4].TrimEnd() : name)
            .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length > MaxProcessNames || names.Any(name => name.Length > MaxProcessNameLength || name.Any(char.IsControl)
            || name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0))
            throw new ArgumentException(text.L("Enter up to five program names without their folder, for example Albion-Online.",
                "Gib bis zu fünf Programmnamen ohne Ordner ein, zum Beispiel Albion-Online."));
        if (names.Contains("TypeWhisper", StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(text.L("TypeWhisper cannot dictate into itself.", "TypeWhisper kann nicht in sich selbst diktieren."));
        return names;
    }

    // Programs with a window, so a running game can be picked by name. Protected or ended processes are left out.
    private static IReadOnlyList<string> RunningPrograms()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero) names.Add(process.ProcessName);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    continue;
                }
            }
        }
        return names.Take(100).ToArray();
    }

    private static string LanguageName(string language, GameText text) => language switch
    {
        "English" => text.L("English", "Englisch"),
        "German" => text.L("German", "Deutsch"),
        "French" => text.L("French", "Französisch"),
        "Spanish" => text.L("Spanish", "Spanisch"),
        "Italian" => text.L("Italian", "Italienisch"),
        "Portuguese" => text.L("Portuguese", "Portugiesisch"),
        "Russian" => text.L("Russian", "Russisch"),
        "Polish" => text.L("Polish", "Polnisch"),
        "Turkish" => text.L("Turkish", "Türkisch"),
        "Japanese" => text.L("Japanese", "Japanisch"),
        "Korean" => text.L("Korean", "Koreanisch"),
        "Chinese" => text.L("Chinese", "Chinesisch"),
        _ => language
    };
}
