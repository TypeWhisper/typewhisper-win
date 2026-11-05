using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Games;

/// <summary>What a dictation into a game needs: how the chat takes the text, the add-on's cast bar if there is one,
/// and a language to translate into.</summary>
internal sealed record GamePlan(ChatDeliveryOptions Chat, GameIndicatorChords? Indicator = null, string? TranslateTo = null);

/// <summary>
/// One game in the plugin's list. Its settings show when it is chosen there; their ids start with the game's id, so
/// unsaved edits of two games never mix. Games are rebuilt from the saved settings after every change.
/// </summary>
internal abstract class Game
{
    /// <summary>Stable id, also the prefix of the game's setting ids.</summary>
    public abstract string Id { get; }
    /// <summary>The name shown in the list.</summary>
    public abstract string Name { get; }
    /// <summary>Whether dictation goes into the game's chat; otherwise TypeWhisper pastes as into any app.</summary>
    public abstract bool Enabled { get; }
    /// <summary>Process names without ".exe"; a dictation into one of them goes into this game's chat.</summary>
    public abstract IReadOnlyList<string> ProcessNames { get; }

    /// <summary>Prepares a dictation into the game. The user is already speaking, so it only reads local files.</summary>
    public abstract GamePlan Prepare(DictationTargetInfo target, PluginLlmStatus? llm);

    /// <summary>What the game's page says above its settings: how dictation works there, and its state.</summary>
    public abstract string Describe(GameText text, PluginLlmStatus? llm);

    /// <summary>The game's settings, in the order shown.</summary>
    public abstract IReadOnlyList<PluginTextSetting> Fields(GameText text);

    /// <summary>Applies edited values to the saved settings. Throws <see cref="ArgumentException"/> with a message for
    /// the user when a value is not valid; values that were not edited keep their saved state.</summary>
    public abstract GamesSettings Save(GamesSettings settings, IReadOnlyDictionary<string, string> values, GameText text);

    /// <summary>Buttons on the game's page, besides adding and removing games.</summary>
    public virtual IReadOnlyList<PluginSettingsAction> Actions(GameText text) => [];

    /// <summary>Runs one of <see cref="Actions"/> with the page's current, possibly unsaved values.</summary>
    public virtual Task<string?> RunAsync(string actionId, IReadOnlyDictionary<string, string> values, GameText text, PluginLlmStatus? llm,
        CancellationToken cancellationToken) => throw new ArgumentException("Unknown action.", nameof(actionId));

    protected string FieldId(string name) => Id + ":" + name;

    protected string Value(IReadOnlyDictionary<string, string> values, string name, string saved) =>
        values.TryGetValue(FieldId(name), out var value) ? value : saved;

    protected bool Switch(IReadOnlyDictionary<string, string> values, string name, bool saved) => Value(values, name, saved ? "true" : "false") switch
    {
        "true" => true,
        "false" => false,
        _ => throw new ArgumentException("Invalid setting.", nameof(values))
    };

    protected PluginTextSetting Field(string name, string title, string description, string value, int maxLength = 256) =>
        new(FieldId(name), title, description, value, maxLength) { Section = PluginSettingsSection.Connection };
}

/// <summary>The plugin's saved settings: World of Warcraft's and the games the user added.</summary>
internal sealed record GamesSettings
{
    /// <summary>Whether dictation goes into the World of Warcraft chat.</summary>
    public bool WowEnabled { get; init; } = true;
    /// <summary>The World of Warcraft folder the user chose, or null to find it through Battle.net.</summary>
    public string? WowFolder { get; init; }
    /// <summary>The games the user added, in the order they were added.</summary>
    public IReadOnlyList<CustomGameSettings> CustomGames { get; init; } = [];
}
