using System.Globalization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Games;

/// <summary>
/// Dictation into game chats, starting with World of Warcraft. While the game is in front, a dictation opens the chat,
/// inserts the text and sends it, split into messages the game accepts. The TypeWhisper add-on opens the chat without
/// closing an open one and shows the dictation as a cast bar; its page in the game sets sending, the bar and a
/// translation language.
/// </summary>
public sealed class GamesPlugin : IDictationTargetPlugin, IPluginTextSettings, IPluginSettingsActions
{
    internal const string FolderSettingId = "wowFolder";
    internal const string InstallActionId = "install-wow-addon";
    private const int MaxFolderLength = 1024;
    private readonly object _sync = new();
    private readonly Func<IReadOnlyList<WowVersionFolder>>? _findVersions;
    private IPluginHostServices? _host;
    // Only one dictation runs at a time; a new one replaces the state of the last.
    private GameDictation? _dictation;

    private sealed record GameDictation(Guid Id, ChatDeliveryOptions Chat, GameIndicatorSignals? Indicator);

    /// <summary>Creates the plugin the host loads.</summary>
    public GamesPlugin() { }

    // Tests pass the game versions instead of searching the machine.
    internal GamesPlugin(Func<IReadOnlyList<WowVersionFolder>> findVersions) => _findVersions = findVersions;

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.games";
    /// <inheritdoc />
    public string PluginName => "Games";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";

    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        lock (_sync) _dictation = null;
        _host = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() { }

    /// <inheritdoc />
    public IReadOnlyList<string> TargetProcessNames => GameCatalog.All.SelectMany(game => game.ProcessNames).ToArray();

    /// <inheritdoc />
    public Task<DictationTargetPlan?> PrepareDictationAsync(DictationTargetInfo target, CancellationToken cancellationToken)
    {
        if (GameCatalog.FindByProcess(target.ProcessName) is not { } game) return Task.FromResult<DictationTargetPlan?>(null);
        // The add-on's page in the game decides sending, the cast bar and translation. The game saves it on /reload or
        // logout, and the plugin reads it from the version folder the game runs from.
        var versionFolder = Path.GetDirectoryName(target.ExecutablePath);
        var addOn = WowAddOnSettings.Read(versionFolder);
        var installed = WowAddOn.IsInstalledIn(versionFolder);
        var chords = GameChat.IndicatorFor(game, addOn, installed);
        lock (_sync) _dictation = new(target.DictationId, GameChat.ChatFor(game, addOn, installed), chords is null ? null : new GameIndicatorSignals(chords));
        // The add-on page names the translating LLM after the next /reload.
        WriteStatus(versionFolder);
        return Task.FromResult<DictationTargetPlan?>(new DictationTargetPlan { AppName = game.Name, TranslateTo = addOn.TranslateTo });
    }

    /// <inheritdoc />
    public DateTimeOffset? OnDictationPhaseChanged(Guid dictationId, DictationTargetPhase phase, IDictationTargetInput input)
    {
        GameIndicatorSignals? indicator;
        lock (_sync) indicator = _dictation?.Id == dictationId ? _dictation.Indicator : null;
        if (indicator is null) return null;
        lock (indicator) return indicator.Observe(phase, input.SignalKeys);
    }

    /// <inheritdoc />
    public async Task<DictationDeliveryResult> DeliverDictationAsync(Guid dictationId, string text, IDictationTargetInput input,
        CancellationToken cancellationToken)
    {
        ChatDeliveryOptions? chat;
        lock (_sync) chat = _dictation?.Id == dictationId ? _dictation.Chat : null;
        if (chat is null) return new(0, 0, "unknown");
        var result = await ChatDeliveryOperation.RunAsync(new TargetInputPlatform(input, chat.SettleMilliseconds, cancellationToken),
            text, chat, cancellationToken);
        return new(result.Total, result.Sent, result.FailedAt switch
        {
            null => null,
            ChatDeliveryStage.TooLong => "too-long",
            var stage => stage.Value.ToString().ToLowerInvariant()
        });
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new(FolderSettingId, L("World of Warcraft folder", "World-of-Warcraft-Ordner"),
            L("Leave empty to find World of Warcraft through Battle.net and its default folders. If it is installed elsewhere, enter the folder that holds _retail_, _classic_ or _forever_.",
                "Leer lassen, um World of Warcraft über Battle.net und die Standardordner zu finden. Ist es woanders installiert, gib den Ordner ein, der _retail_, _classic_ oder _forever_ enthält."),
            ChosenFolder ?? "", MaxFolderLength)
    ];

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        if (id != FolderSettingId) throw new ArgumentException("Unknown setting.", nameof(id));
        var folder = value.Trim();
        if (folder.Length > 0 && (folder.Length > MaxFolderLength || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(folder)))
            throw new ArgumentException(L("Enter the full path of the World of Warcraft folder.", "Gib den vollständigen Pfad des World-of-Warcraft-Ordners ein."), nameof(value));
        Host.SetSetting<string?>(FolderSettingId, folder.Length == 0 ? null : folder);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions
    {
        get
        {
            var versions = FindVersions();
            var title = versions.Count == 0 || versions.All(version => version.InstalledVersion is null) ? L("Install add-on", "Add-on installieren")
                : versions.Any(version => version.InstalledVersion != WowAddOn.BundledVersion) ? L("Update add-on", "Add-on aktualisieren")
                : L("Reinstall add-on", "Add-on neu installieren");
            return
            [
                new(InstallActionId, title,
                    L("Copies the TypeWhisper add-on into every World of Warcraft version found. It opens the chat without closing an open one and shows your dictation as a cast bar. Type /tw in the game for its settings.",
                        "Kopiert das TypeWhisper-Add-on in jede gefundene World-of-Warcraft-Version. Es öffnet den Chat, ohne einen offenen zu schließen, und zeigt dein Diktat als Zauberbalken. Gib /tw im Spiel ein, um es einzustellen."))
            ];
        }
    }

    /// <inheritdoc />
    public Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        if (id != InstallActionId) throw new ArgumentException("Unknown action.", nameof(id));
        var versions = FindVersions();
        if (versions.Count == 0)
            return Task.FromResult<string?>(ChosenFolder is null
                ? L("World of Warcraft was not found. Enter its folder under World of Warcraft folder and save.",
                    "World of Warcraft wurde nicht gefunden. Gib seinen Ordner unter World-of-Warcraft-Ordner ein und speichere.")
                : L("This folder contains no World of Warcraft version. Enter the folder that holds _retail_, _classic_ or _forever_.",
                    "Dieser Ordner enthält keine World-of-Warcraft-Version. Gib den Ordner ein, der _retail_, _classic_ oder _forever_ enthält."));
        var failures = WowAddOn.Install(versions);
        foreach (var version in versions) WriteStatus(version.Path);
        var installed = versions.Where(version => failures.All(failure => failure.Folder != version)).ToArray();
        var message = failures.Count == 0
            ? L("Installed for {0}. If the game is running, type /reload in the chat. Settings: type /tw in the game.",
                "Installiert für {0}. Läuft das Spiel, gib /reload im Chat ein. Einstellungen: Gib /tw im Spiel ein.", Names(installed))
            : L("The add-on could not be installed for {0}. Close the game and try again.",
                "Das Add-on konnte für {0} nicht installiert werden. Schließe das Spiel und versuche es erneut.", Names(failures.Select(failure => failure.Folder)));
        // A translation that cannot run is explained here too, without waiting for a /reload in the game.
        if (Host.DefaultLlm is not { Ready: true } && versions.Any(version => WowAddOnSettings.Read(version.Path).TranslateTo is not null))
            message += " " + L("Translation is chosen in the game, but the default LLM is missing or unavailable, so messages go out untranslated. Open Default LLM in Workflows to choose one.",
                "Im Spiel ist eine Übersetzung gewählt, aber das Standard-LLM fehlt oder ist nicht verfügbar, deshalb gehen Nachrichten unübersetzt raus. Öffne „Standard-LLM“ unter Workflows, um eines zu wählen.");
        return Task.FromResult<string?>(message);
    }

    private IPluginHostServices Host => _host ?? throw new InvalidOperationException("The plugin is not active.");

    private string? ChosenFolder => _host?.GetSetting<string?>(FolderSettingId) is { Length: > 0 } folder ? folder : null;

    private IReadOnlyList<WowVersionFolder> FindVersions() => _findVersions?.Invoke()
        ?? WowAddOn.FindInstallRoots(ChosenFolder).SelectMany(WowAddOn.VersionFolders).ToArray();

    // Tells the add-on which LLM translates its messages; it shows the name on its page after the next /reload.
    private void WriteStatus(string? versionFolder)
    {
        var llm = _host?.DefaultLlm;
        WowAddOn.WriteStatus(versionFolder, llm?.Name, llm?.Ready == true);
    }

    private static string Names(IEnumerable<WowVersionFolder> versions) => string.Join(", ", versions.Select(version => version.Name).Distinct());

    private string L(string english, string german) => PluginLocalization.Get(_host, english, german);

    private string L(string english, string german, params object[] args) => string.Format(CultureInfo.CurrentCulture, L(english, german), args);
}
