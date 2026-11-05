using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Games;

/// <summary>
/// World of Warcraft with the TypeWhisper add-on. The add-on opens the chat without closing an open one and shows the
/// dictation as a cast bar; its page in the game sets sending, the bar and a translation language.
/// </summary>
/// <param name="settings">The plugin's saved settings.</param>
/// <param name="findVersions">Finds the game's versions in a chosen folder, or through Battle.net and the default
/// folders for null. Tests pass their own.</param>
internal sealed class WorldOfWarcraftGame(GamesSettings settings, Func<string?, IReadOnlyList<WowVersionFolder>>? findVersions = null) : Game
{
    internal const string GameId = "wow";
    internal const string InstallActionId = GameId + ":install";
    private const int MaxFolderLength = 1024;

    public override string Id => GameId;
    public override string Name => GameCatalog.WorldOfWarcraft.Name;
    public override bool Enabled => settings.WowEnabled;
    public override IReadOnlyList<string> ProcessNames => GameCatalog.WorldOfWarcraft.ProcessNames;

    public override GamePlan Prepare(DictationTargetInfo target, PluginLlmStatus? llm)
    {
        var game = GameCatalog.WorldOfWarcraft;
        // The add-on's page in the game decides sending, the cast bar and translation. The game saves it on /reload or
        // logout, and the plugin reads it from the version folder the game runs from.
        var versionFolder = Path.GetDirectoryName(target.ExecutablePath);
        var addOn = WowAddOnSettings.Read(versionFolder);
        var installed = WowAddOn.IsInstalledIn(versionFolder);
        // The add-on page names the translating LLM after the next /reload.
        WowAddOn.WriteStatus(versionFolder, llm?.Name, llm?.Ready == true);
        return new(GameChat.ChatFor(game, addOn, installed), GameChat.IndicatorFor(game, addOn, installed), addOn.TranslateTo);
    }

    public override string Describe(GameText text, PluginLlmStatus? llm)
    {
        var about = text.L("While World of Warcraft is in front, dictation opens the chat, inserts the text and sends it, in messages of up to 255 bytes. The TypeWhisper add-on opens the chat without closing an open one and shows a cast bar in the game. Type /tw in the game to set sending, the bar and a translation.",
            "Ist World of Warcraft im Vordergrund, öffnet das Diktat den Chat, fügt den Text ein und sendet ihn, in Nachrichten mit höchstens 255 Bytes. Das TypeWhisper-Add-on öffnet den Chat, ohne einen offenen zu schließen, und zeigt einen Zauberbalken im Spiel. Gib /tw im Spiel ein, um Senden, Balken und Übersetzung einzustellen.");
        if (!Enabled)
            return about + "\n\n" + text.L("Turned off: TypeWhisper pastes into World of Warcraft as into any other app.",
                "Ausgeschaltet: TypeWhisper fügt in World of Warcraft ein wie in jede andere App.");
        var versions = FindVersions(settings.WowFolder);
        var missing = versions.Where(version => version.InstalledVersion != WowAddOn.BundledVersion).ToArray();
        var state = versions.Count == 0
            ? settings.WowFolder is null
                ? text.L("World of Warcraft was not found. Choose its folder below.", "World of Warcraft wurde nicht gefunden. Wähle unten seinen Ordner.")
                : text.L("This folder contains no World of Warcraft version. Choose the folder that holds _retail_, _classic_ or _forever_.",
                    "Dieser Ordner enthält keine World-of-Warcraft-Version. Wähle den Ordner, der _retail_, _classic_ oder _forever_ enthält.")
            : missing.Length == 0
                ? text.L("Add-on installed for {0}.", "Add-on installiert für {0}.", Names(versions))
                : missing.Length == versions.Count && versions.All(version => version.InstalledVersion is null)
                    ? text.L("Found: {0}. The add-on is not installed yet. Without it, TypeWhisper opens the chat with Enter, so start with the chat closed.",
                        "Gefunden: {0}. Das Add-on ist noch nicht installiert. Ohne es öffnet TypeWhisper den Chat mit Enter, starte also mit geschlossenem Chat.", Names(versions))
                    : text.L("The add-on needs an install or update for {0}.", "Das Add-on braucht eine Installation oder ein Update für {0}.", Names(missing));
        // A translation that cannot run is explained here too, without waiting for a /reload in the game.
        if (llm is not { Ready: true } && versions.Any(version => WowAddOnSettings.Read(version.Path).TranslateTo is not null))
            state += " " + TranslationWarning(text);
        return about + "\n\n" + state;
    }

    public override IReadOnlyList<PluginTextSetting> Fields(GameText text) =>
    [
        Field("enabled", text.L("Dictate into the chat", "In den Chat diktieren"),
            text.L("Off: TypeWhisper pastes into World of Warcraft as into any other app.", "Aus: TypeWhisper fügt in World of Warcraft ein wie in jede andere App."),
            Enabled ? "true" : "false") with { Choices = text.OnOff },
        Field("folder", text.L("World of Warcraft folder", "World-of-Warcraft-Ordner"),
            text.L("Leave empty to find World of Warcraft through Battle.net and its default folders. If it is installed elsewhere, choose the folder that holds _retail_, _classic_ or _forever_.",
                "Leer lassen, um World of Warcraft über Battle.net und die Standardordner zu finden. Ist es woanders installiert, wähle den Ordner, der _retail_, _classic_ oder _forever_ enthält."),
            settings.WowFolder ?? "", MaxFolderLength) with
        {
            Kind = PluginTextSettingKind.Folder,
            // An empty field finds the game by itself; the hint says where, or that it found nothing.
            Placeholder = DetectedFolder() is { } detected
                ? text.L("{0} (found automatically)", "{0} (automatisch gefunden)", detected)
                : text.L("Not found automatically. Choose the World of Warcraft folder.", "Nicht automatisch gefunden. Wähle den World-of-Warcraft-Ordner.")
        }
    ];

    public override GamesSettings Save(GamesSettings saved, IReadOnlyDictionary<string, string> values, GameText text) => saved with
    {
        WowEnabled = Switch(values, "enabled", Enabled),
        WowFolder = Folder(Value(values, "folder", saved.WowFolder ?? ""), text)
    };

    public override IReadOnlyList<PluginSettingsAction> Actions(GameText text)
    {
        var versions = FindVersions(settings.WowFolder);
        var title = versions.Count == 0 || versions.All(version => version.InstalledVersion is null) ? text.L("Install add-on", "Add-on installieren")
            : versions.Any(version => version.InstalledVersion != WowAddOn.BundledVersion) ? text.L("Update add-on", "Add-on aktualisieren")
            : text.L("Reinstall add-on", "Add-on neu installieren");
        return
        [
            new(InstallActionId, title,
                text.L("Copies the TypeWhisper add-on into every World of Warcraft version found. It opens the chat without closing an open one and shows your dictation as a cast bar. Type /tw in the game for its settings.",
                    "Kopiert das TypeWhisper-Add-on in jede gefundene World-of-Warcraft-Version. Es öffnet den Chat, ohne einen offenen zu schließen, und zeigt dein Diktat als Zauberbalken. Gib /tw im Spiel ein, um es einzustellen."))
            { Section = PluginSettingsSection.Connection }
        ];
    }

    // Installs into the folder on the page, saved or not, so a folder just chosen works before it is saved.
    public override Task<string?> RunAsync(string actionId, IReadOnlyDictionary<string, string> values, GameText text, PluginLlmStatus? llm,
        CancellationToken cancellationToken)
    {
        if (actionId != InstallActionId) return base.RunAsync(actionId, values, text, llm, cancellationToken);
        var folder = Folder(Value(values, "folder", settings.WowFolder ?? ""), text);
        var versions = FindVersions(folder);
        if (versions.Count == 0)
            return Task.FromResult<string?>(folder is null
                ? text.L("World of Warcraft was not found. Choose its folder under World of Warcraft folder.",
                    "World of Warcraft wurde nicht gefunden. Wähle seinen Ordner unter World-of-Warcraft-Ordner.")
                : text.L("This folder contains no World of Warcraft version. Choose the folder that holds _retail_, _classic_ or _forever_.",
                    "Dieser Ordner enthält keine World-of-Warcraft-Version. Wähle den Ordner, der _retail_, _classic_ oder _forever_ enthält."));
        var failures = WowAddOn.Install(versions);
        foreach (var version in versions) WowAddOn.WriteStatus(version.Path, llm?.Name, llm?.Ready == true);
        var installed = versions.Where(version => failures.All(failure => failure.Folder != version)).ToArray();
        var message = failures.Count == 0
            ? text.L("Installed for {0}. If the game is running, type /reload in the chat. Settings: type /tw in the game.",
                "Installiert für {0}. Läuft das Spiel, gib /reload im Chat ein. Einstellungen: Gib /tw im Spiel ein.", Names(installed))
            : text.L("The add-on could not be installed for {0}. Close the game and try again.",
                "Das Add-on konnte für {0} nicht installiert werden. Schließe das Spiel und versuche es erneut.", Names(failures.Select(failure => failure.Folder)));
        if (llm is not { Ready: true } && versions.Any(version => WowAddOnSettings.Read(version.Path).TranslateTo is not null))
            message += " " + TranslationWarning(text);
        return Task.FromResult<string?>(message);
    }

    private static string TranslationWarning(GameText text) =>
        text.L("Translation is chosen in the game, but the default LLM is missing or unavailable, so messages go out untranslated. Open Default LLM in Workflows to choose one.",
            "Im Spiel ist eine Übersetzung gewählt, aber das Standard-LLM fehlt oder ist nicht verfügbar, deshalb gehen Nachrichten unübersetzt raus. Öffne „Standard-LLM“ unter Workflows, um eines zu wählen.");

    // An empty value finds the game by itself. A chosen one must be a full path, since a relative one would resolve
    // against TypeWhisper's own folder.
    private static string? Folder(string value, GameText text)
    {
        var folder = value.Trim();
        if (folder.Length == 0) return null;
        if (folder.Length > MaxFolderLength || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(folder))
            throw new ArgumentException(text.L("Enter the full path of the World of Warcraft folder.", "Gib den vollständigen Pfad des World-of-Warcraft-Ordners ein."));
        return folder;
    }

    private IReadOnlyList<WowVersionFolder> FindVersions(string? folder) => findVersions?.Invoke(folder)
        ?? WowAddOn.FindInstallRoots(folder, detect: folder is null).SelectMany(WowAddOn.VersionFolders).ToArray();

    // The installation found without a chosen folder, through Battle.net and the default folders.
    private string? DetectedFolder() => FindVersions(null).Select(version => Path.GetDirectoryName(version.Path))
        .FirstOrDefault(folder => !string.IsNullOrEmpty(folder));

    private static string Names(IEnumerable<WowVersionFolder> versions) => string.Join(", ", versions.Select(version => version.Name).Distinct());
}
