using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Games;

/// <summary>
/// Dictation into game chats. The plugin's page lists the games: World of Warcraft, whose TypeWhisper add-on opens the
/// chat and shows a cast bar, and any game the user adds with the key its chat opens with. While a listed game is in
/// front, a dictation opens its chat, inserts the text and sends it, split into messages the game accepts.
/// </summary>
public sealed class GamesPlugin : IDictationTargetPlugin, IPluginProfileSettings, IPluginSettingsActions
{
    internal const string SettingsKey = "games.v1";
    internal const string SelectorId = "game";
    internal const string AddGameActionId = "add-game";
    internal const int MaxCustomGames = 30;
    private readonly object _sync = new();
    private readonly Func<string?, IReadOnlyList<WowVersionFolder>>? _findVersions;
    private readonly Func<IReadOnlyList<string>>? _runningPrograms;
    private readonly GameText _text;
    private IPluginHostServices? _host;
    // Replaced as a whole on every change, so dictation calls beside the settings page always read one consistent state.
    private GamesSettings _settings = new();
    // The game whose settings the page shows; not saved, so the page opens on World of Warcraft.
    private string _selected = WorldOfWarcraftGame.GameId;
    // Only one dictation runs at a time; a new one replaces the state of the last.
    private GameDictation? _dictation;

    private sealed record GameDictation(Guid Id, ChatDeliveryOptions Chat, GameIndicatorSignals? Indicator);

    /// <summary>Creates the plugin the host loads.</summary>
    public GamesPlugin() => _text = new(() => _host);

    // Tests pass the World of Warcraft versions and running programs instead of looking at the machine.
    internal GamesPlugin(Func<string?, IReadOnlyList<WowVersionFolder>>? findVersions, Func<IReadOnlyList<string>>? runningPrograms = null) : this()
    {
        _findVersions = findVersions;
        _runningPrograms = runningPrograms;
    }

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.games";
    /// <inheritdoc />
    public string PluginName => "Games";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";

    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host)
    {
        var saved = host.GetSetting<GamesSettings>(SettingsKey) ?? new GamesSettings();
        // Entries that cannot have come from this plugin are left out instead of failing the whole list.
        var games = (saved.CustomGames ?? []).Where(game => game is { Name: { Length: > 0 }, Id: { Length: 33 } } && game.Id[0] == 'c'
            && game.Id.Skip(1).All(char.IsAsciiHexDigitLower) && game.ProcessNames is not null).DistinctBy(game => game.Id).Take(MaxCustomGames).ToArray();
        Volatile.Write(ref _settings, saved with { CustomGames = games });
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

    private IReadOnlyList<Game> Games
    {
        get
        {
            var settings = Volatile.Read(ref _settings);
            return [new WorldOfWarcraftGame(settings, _findVersions), .. settings.CustomGames.Select(game => new CustomGame(game, _runningPrograms))];
        }
    }

    private Game Selected => Games.FirstOrDefault(game => game.Id == _selected) ?? Games[0];

    /// <inheritdoc />
    public IReadOnlyList<string> TargetProcessNames =>
        Games.Where(game => game.Enabled).SelectMany(game => game.ProcessNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <inheritdoc />
    public Task<DictationTargetPlan?> PrepareDictationAsync(DictationTargetInfo target, CancellationToken cancellationToken)
    {
        if (Games.FirstOrDefault(game => game.Enabled && game.ProcessNames.Contains(target.ProcessName, StringComparer.OrdinalIgnoreCase)) is not { } game)
            return Task.FromResult<DictationTargetPlan?>(null);
        var plan = game.Prepare(target, _host?.DefaultLlm);
        lock (_sync) _dictation = new(target.DictationId, plan.Chat, plan.Indicator is null ? null : new GameIndicatorSignals(plan.Indicator));
        return Task.FromResult<DictationTargetPlan?>(new DictationTargetPlan { AppName = game.Name, TranslateTo = plan.TranslateTo });
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
            _ => result.FailedAt.Value.ToString().ToLowerInvariant()
        });
    }

    /// <inheritdoc />
    public string ProfileSelectorId => SelectorId;
    /// <inheritdoc />
    public string? AddProfileActionId => Volatile.Read(ref _settings).CustomGames.Count < MaxCustomGames ? AddGameActionId : null;
    /// <inheritdoc />
    public string? RemoveProfileActionId => Selected is CustomGame game ? RemoveActionId(game.Id) : null;

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings
    {
        get
        {
            var games = Games;
            var selected = games.FirstOrDefault(game => game.Id == _selected) ?? games[0];
            // The list's description shows above the chosen game's settings, so it says what dictation does there.
            return
            [
                new(SelectorId, _text.L("Games", "Spiele"), selected.Describe(_text, _host?.DefaultLlm), selected.Id)
                {
                    Choices = games.Select(game => new PluginSettingChoice(game.Id, game.Enabled ? game.Name : _text.L("{0} (off)", "{0} (aus)", game.Name))).ToArray()
                },
                .. selected.Fields(_text)
            ];
        }
    }

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        if (id != SelectorId) return SaveProfileSettingsAsync(Selected.Id, new Dictionary<string, string> { [id] = value }, null, cancellationToken);
        if (Games.All(game => game.Id != value)) throw new ArgumentException("Unknown game.", nameof(value));
        _selected = value;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveProfileSettingsAsync(string profileId, IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var game = Games.FirstOrDefault(candidate => candidate.Id == profileId)
            ?? throw new ArgumentException(_text.L("This game is no longer in the list. Reopen the plugin's settings.", "Dieses Spiel ist nicht mehr in der Liste. Öffne die Einstellungen des Plugins erneut."));
        var next = game.Save(Volatile.Read(ref _settings), values, _text);
        // Two games cannot share a program: a dictation into it would not know which chat it belongs to.
        var all = new[] { new WorldOfWarcraftGame(next, _findVersions) }.Concat<Game>(next.CustomGames.Select(custom => new CustomGame(custom, _runningPrograms))).ToArray();
        foreach (var other in all.Where(candidate => candidate.Id != profileId))
            if (all.Single(candidate => candidate.Id == profileId).ProcessNames.FirstOrDefault(name => other.ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase)) is { } shared)
                throw new ArgumentException(_text.L("{0} already belongs to {1}.", "{0} gehört schon zu {1}.", shared, other.Name));
        Save(next);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<PluginProfileActionResult> ExecuteProfileActionAsync(string profileId, string actionId, IReadOnlyDictionary<string, string> values,
        string? apiKey, CancellationToken cancellationToken)
    {
        var game = Games.FirstOrDefault(candidate => candidate.Id == profileId)
            ?? throw new ArgumentException(_text.L("This game is no longer in the list. Reopen the plugin's settings.", "Dieses Spiel ist nicht mehr in der Liste. Öffne die Einstellungen des Plugins erneut."));
        return new(await game.RunAsync(actionId, values, _text, _host?.DefaultLlm, cancellationToken) ?? "");
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions
    {
        get
        {
            var selected = Selected;
            var actions = new List<PluginSettingsAction>();
            if (AddProfileActionId is { } add)
                actions.Add(new(add, _text.L("Add game", "Spiel hinzufügen"), "") { Section = PluginSettingsSection.Connection });
            actions.AddRange(selected.Actions(_text));
            if (selected is CustomGame custom)
                actions.Add(new(RemoveActionId(custom.Id), _text.L("Remove game…", "Spiel entfernen …"), "") { Section = PluginSettingsSection.Connection });
            return actions;
        }
    }

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        var settings = Volatile.Read(ref _settings);
        if (id == AddGameActionId && settings.CustomGames.Count < MaxCustomGames)
        {
            var game = new CustomGameSettings
            {
                Id = "c" + Guid.NewGuid().ToString("N"),
                Name = _text.L("New game {0}", "Neues Spiel {0}", settings.CustomGames.Count + 1)
            };
            Save(settings with { CustomGames = [.. settings.CustomGames, game] });
            _selected = game.Id;
            return _text.L("Game added. Enter its program name and the key its chat opens with, then save.",
                "Spiel hinzugefügt. Gib seinen Programmnamen und die Taste zum Öffnen des Chats ein und speichere.");
        }
        if (Selected is CustomGame selected && id == RemoveActionId(selected.Id))
        {
            Save(settings with { CustomGames = settings.CustomGames.Where(game => game.Id != selected.Id).ToArray() });
            _selected = WorldOfWarcraftGame.GameId;
            // The page puts the game's name before the message.
            return _text.L("Removed.", "Entfernt.");
        }
        // A host that shows the page without the list runs the chosen game's buttons with its saved values.
        if (Selected.Actions(_text).Any(action => action.Id == id))
            return await Selected.RunAsync(id, new Dictionary<string, string>(), _text, _host?.DefaultLlm, cancellationToken);
        throw new ArgumentException("Unknown or stale action.", nameof(id));
    }

    private static string RemoveActionId(string gameId) => "remove:" + gameId;

    // Saved first, then published: a failed write keeps the previous settings. The host reads the games' programs
    // again, so a dictation into a new or changed game reaches the plugin.
    private void Save(GamesSettings next)
    {
        var host = _host ?? throw new InvalidOperationException("The plugin is not active.");
        host.SetSetting(SettingsKey, next);
        Volatile.Write(ref _settings, next);
        host.NotifyCapabilitiesChanged();
    }
}
