using System.Text.Json;

namespace TypeWhisper.Plugin.Voxtral;

/// <summary>A transcription model with the features the account catalog reports for it.</summary>
internal sealed record MistralTranscriptionModel(string Id, string Name, bool Realtime, bool ContextBias, string[] Languages);

/// <summary>A text model offered for text processing.</summary>
internal sealed record MistralTextModel(string Id, string Name);

/// <summary>
/// The persisted model catalog: one entry per model, with every alias Mistral lists for it mapped to that entry's ID.
/// </summary>
internal sealed record MistralModelCatalog(
    int Version, MistralTranscriptionModel[] Transcription, MistralTextModel[] Text, Dictionary<string, string> Aliases)
{
    /// <summary>Maps a saved, alias or retired model ID to the ID of an entry in <paramref name="ids"/>.</summary>
    internal string? Resolve(string? saved, IEnumerable<string> ids)
    {
        if (string.IsNullOrWhiteSpace(saved)) return null;
        var available = ids as ICollection<string> ?? ids.ToHashSet(StringComparer.Ordinal);
        var raw = saved.Trim();
        var id = Canonical(raw);
        if (available.Contains(id)) return id;
        if (MistralCatalog.Replacements.TryGetValue(raw, out var replacement) || MistralCatalog.Replacements.TryGetValue(id, out replacement))
        {
            replacement = Canonical(replacement);
            if (available.Contains(replacement)) return replacement;
        }
        return null;
    }

    private string Canonical(string id) => Aliases.GetValueOrDefault(id, id);
}

/// <summary>Builds the cleaned model catalog from Mistral's <c>/v1/models</c> response.</summary>
internal static class MistralCatalog
{
    /// <summary>Catalogs saved by plugin versions before 1.4.0 have no version and are replaced by the default catalog.</summary>
    internal const int CurrentVersion = 2;
    internal const string MiniTranscribe = "voxtral-mini-transcribe-3-0";
    internal const string SmallTranscribe = "voxtral-small-transcribe-3-0";
    internal const string RealtimeTranscribe = "voxtral-mini-transcribe-realtime-3-0";
    internal const string LargeText = "mistral-large-4";
    internal const string MediumText = "mistral-medium-2604";
    internal const string SmallText = "mistral-small-2603";

    /// <summary>
    /// Languages Mistral accepted for Voxtral Transcribe 3 and Realtime 3 on 2026-11-06. A refreshed catalog uses the
    /// list the account reports for each model.
    /// </summary>
    internal static readonly string[] Voxtral3Languages =
    [
        "ar", "en", "de", "es", "fr", "hi", "it", "nl", "pt", "zh", "ru", "ko", "ja", "id", "tr", "vi", "sv", "pl", "th", "tl",
        "no", "da", "hu", "cs", "uk", "ro", "ms", "ca", "el", "sk", "fi", "lv", "km", "pa", "ta", "my", "bg", "mr", "ur", "et",
        "te", "gu", "kn", "fa", "sl", "ml", "is", "bn", "hr", "mk", "eu", "sr", "ps", "lb", "ga", "or", "lt", "mt"
    ];

    /// <summary>Names and picker order for models TypeWhisper knows. Other models keep the name the catalog gives them.</summary>
    private static readonly (string Id, string Name)[] Known =
    [
        (MiniTranscribe, "Voxtral Mini Transcribe 3"),
        (SmallTranscribe, "Voxtral Small Transcribe 3"),
        (RealtimeTranscribe, "Voxtral Mini Transcribe Realtime 3"),
        (LargeText, "Mistral Large 4"),
        (MediumText, "Mistral Medium 3.5"),
        (SmallText, "Mistral Small 4"),
        ("ministral-14b-2512", "Ministral 3 14B"),
        ("ministral-8b-2512", "Ministral 3 8B"),
        ("ministral-3b-2512", "Ministral 3 3B")
    ];

    /// <summary>
    /// Earlier models and the current model replacing them. A replaced model the API still lists is hidden while its
    /// successor is available, and a saved selection of it moves to the successor. The chat entries match the macOS plugin.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["voxtral-mini-2602"] = MiniTranscribe,
        ["voxtral-mini-latest"] = MiniTranscribe,
        ["voxtral-mini-transcribe-realtime-2602"] = RealtimeTranscribe,
        ["voxtral-mini-realtime-2602"] = RealtimeTranscribe,
        ["voxtral-mini-realtime-latest"] = RealtimeTranscribe,
        ["mistral-large-latest"] = LargeText,
        ["mistral-large-2512"] = LargeText,
        ["mistral-large-2411"] = LargeText,
        ["mistral-large-2407"] = LargeText,
        ["mistral-large-2402"] = LargeText,
        ["mistral-small-2506"] = SmallText,
        ["mistral-small-2503"] = SmallText,
        ["mistral-small-2501"] = SmallText,
        ["mistral-small-2409"] = SmallText,
        ["mistral-small-2402"] = SmallText,
        ["pixtral-12b-2409"] = "ministral-14b-2512",
        ["pixtral-large-2411"] = "ministral-14b-2512",
        ["pixtral-large-latest"] = "ministral-14b-2512",
        ["ministral-8b-2410"] = "ministral-8b-2512",
        ["ministral-3b-2410"] = "ministral-3b-2512"
    };

    /// <summary>
    /// Reasoning levels Mistral documents per model, fastest first. Large 4 rejects <c>none</c>; Ministral and models
    /// without reasoning reject the parameter, so they get no levels.
    /// </summary>
    internal static string[] ReasoningEfforts(string modelId) => modelId switch
    {
        LargeText => ["low", "medium", "high"],
        SmallText or MediumText => ["none", "high"],
        _ => []
    };

    internal static string DisplayName(string id, string fallback) =>
        Known.FirstOrDefault(k => k.Id == id).Name ?? fallback;

    /// <summary>The catalog used until the user refreshes models with their key.</summary>
    internal static MistralModelCatalog Default { get; } = new(
        CurrentVersion,
        [
            new(MiniTranscribe, "Voxtral Mini Transcribe 3.0", false, true, Voxtral3Languages),
            new(SmallTranscribe, "Voxtral Small Transcribe 3.0", false, true, Voxtral3Languages),
            new(RealtimeTranscribe, "Voxtral Mini Transcribe Realtime 3.0", true, true, Voxtral3Languages)
        ],
        [
            new(LargeText, LargeText), new(MediumText, MediumText), new(SmallText, SmallText),
            new("ministral-14b-2512", "ministral-14b-2512"), new("ministral-8b-2512", "ministral-8b-2512"),
            new("ministral-3b-2512", "ministral-3b-2512")
        ],
        new(StringComparer.Ordinal)
        {
            ["voxtral-mini-transcribe-3"] = MiniTranscribe,
            ["voxtral-mini-transcribe-latest"] = MiniTranscribe,
            ["voxtral-small-transcribe-3"] = SmallTranscribe,
            ["voxtral-small-transcribe-latest"] = SmallTranscribe,
            ["voxtral-mini-transcribe-realtime-3"] = RealtimeTranscribe,
            ["mistral-large-4-0"] = LargeText,
            ["mistral-medium-latest"] = MediumText,
            ["mistral-medium"] = MediumText,
            ["mistral-medium-3"] = MediumText,
            ["mistral-medium-3-5"] = MediumText,
            ["mistral-medium-3.5"] = MediumText,
            ["mistral-small-latest"] = SmallText,
            ["ministral-14b-latest"] = "ministral-14b-2512",
            ["ministral-8b-latest"] = "ministral-8b-2512",
            ["ministral-3b-latest"] = "ministral-3b-2512"
        });

    /// <summary>Reads a saved catalog. Missing, older or damaged catalogs fall back to <see cref="Default"/>.</summary>
    internal static MistralModelCatalog Load(string saved)
    {
        if (saved.Length == 0) return Default;
        try
        {
            using (var document = JsonDocument.Parse(saved))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty(nameof(MistralModelCatalog.Version), out var version)
                    || !version.TryGetInt32(out var number) || number != CurrentVersion)
                    return Default;
            }
            var catalog = JsonSerializer.Deserialize<MistralModelCatalog>(saved);
            return catalog is { Transcription: not null, Text: not null, Aliases: not null }
                && catalog.Transcription.All(m => m is { Id: not null, Name: not null, Languages: not null })
                && catalog.Text.All(m => m is { Id: not null, Name: not null })
                ? catalog with { Aliases = new(catalog.Aliases, StringComparer.Ordinal) } : Default;
        }
        catch (JsonException) { return Default; }
    }

    /// <summary>
    /// Groups each model with its aliases, keeps transcription and general text models, hides models that a listed
    /// successor replaces, and orders known models first.
    /// </summary>
    internal static MistralModelCatalog Parse(JsonElement root)
    {
        var data = ProviderConnection.Required(root, "data", JsonValueKind.Array);
        var entries = new List<Entry>();
        foreach (var model in data.EnumerateArray())
        {
            var id = ProviderConnection.RequiredText(model, "id");
            if (!ValidId(id)) throw ProviderConnection.InvalidResponse();
            var capabilities = ProviderConnection.Required(model, "capabilities", JsonValueKind.Object);
            if (Flag(model, "archived")) continue;
            var aliases = model.TryGetProperty("aliases", out var rawAliases) && rawAliases.ValueKind == JsonValueKind.Array
                ? rawAliases.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).Where(ValidId).ToArray()
                : [];
            entries.Add(new(id, ProviderConnection.Text(model, "name"), aliases, capabilities,
                model.TryGetProperty("audio_transcription_capabilities", out var audio) && audio.ValueKind == JsonValueKind.Object ? audio : null));
        }

        // Mistral lists every alias as its own model; aliases of one model form a group.
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string id)
        {
            while (parent.TryGetValue(id, out var next) && next != id) id = parent[id] = parent.GetValueOrDefault(next, next);
            return id;
        }
        void Union(string a, string b)
        {
            parent.TryAdd(a, a); parent.TryAdd(b, b);
            var (rootA, rootB) = (Find(a), Find(b));
            if (rootA != rootB) parent[rootB] = rootA;
        }
        foreach (var entry in entries)
        {
            Union(entry.Id, entry.Id);
            foreach (var alias in entry.Aliases) Union(entry.Id, alias);
        }

        var rootOf = parent.Keys.ToArray().ToDictionary(id => id, Find, StringComparer.Ordinal);
        var membersOf = rootOf.GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(pair => pair.Key).ToArray(), StringComparer.Ordinal);

        var transcription = new List<MistralTranscriptionModel>();
        var text = new List<MistralTextModel>();
        var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in entries.GroupBy(e => rootOf[e.Id], StringComparer.Ordinal))
        {
            var members = membersOf[group.Key];
            var apiName = group.Select(e => e.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            var id = Canonical(members, apiName);
            var source = group.FirstOrDefault(e => e.Id == id) ?? group.First();
            foreach (var member in members.Where(m => m != id)) aliasMap[member] = id;
            var name = string.IsNullOrWhiteSpace(apiName) || members.Contains(apiName, StringComparer.Ordinal) ? id : apiName.Trim();
            var caps = source.Capabilities;
            if (Flag(caps, "audio_transcription") || Flag(caps, "audio_transcription_realtime"))
            {
                var audio = source.Audio;
                transcription.Add(new(id, name, Flag(caps, "audio_transcription_realtime"),
                    audio is { } a && Flag(a, "context_bias"), audio is { } b ? Languages(b) : []));
            }
            // Text processing needs a general chat model: no speech, audio, code completion or Labs models.
            if (Flag(caps, "completion_chat") && !Flag(caps, "audio_speech") && !Flag(caps, "audio") && !Flag(caps, "completion_fim")
                && !members.Any(m => m.StartsWith("labs-", StringComparison.Ordinal)))
                text.Add(new(id, name));
        }

        return new(CurrentVersion,
            Order(WithoutReplaced(transcription, m => m.Id, aliasMap), m => m.Id, m => m.Name).ToArray(),
            Order(WithoutReplaced(text, m => m.Id, aliasMap), m => m.Id, m => m.Name).ToArray(),
            aliasMap);
    }

    private static IEnumerable<T> WithoutReplaced<T>(List<T> models, Func<T, string> id, Dictionary<string, string> aliases)
    {
        var ids = models.Select(id).ToHashSet(StringComparer.Ordinal);
        string Canonical(string value) => aliases.GetValueOrDefault(value, value);
        bool Replaced(string modelId) => Replacements.Any(pair => Canonical(pair.Key) == modelId
            && Canonical(pair.Value) is var successor && successor != modelId && ids.Contains(successor));
        return models.Where(m => !Replaced(id(m)));
    }

    private static IEnumerable<T> Order<T>(IEnumerable<T> models, Func<T, string> id, Func<T, string> name) =>
        models.OrderBy(m => Array.FindIndex(Known, k => k.Id == id(m)) is var index and >= 0 ? index : int.MaxValue)
            .ThenBy(m => DisplayName(id(m), name(m)), StringComparer.OrdinalIgnoreCase)
            .ThenBy(id, StringComparer.Ordinal);

    // Known IDs win, then the ID the catalog names the model by, then the most specific ID. A "-latest" alias moves to the
    // next release, so it is used only when the model has no other ID; a saved selection then stays on the same model.
    private static string Canonical(string[] members, string? apiName)
    {
        foreach (var (id, _) in Known)
            if (members.Contains(id, StringComparer.Ordinal)) return id;
        if (apiName is not null && !apiName.EndsWith("-latest", StringComparison.Ordinal) && members.Contains(apiName, StringComparer.Ordinal))
            return apiName;
        return members.OrderBy(i => i.EndsWith("-latest", StringComparison.Ordinal))
            .ThenByDescending(i => i.Length).ThenBy(i => i, StringComparer.Ordinal).First();
    }

    private static string[] Languages(JsonElement audio) =>
        audio.TryGetProperty("languages", out var languages) && languages.ValueKind == JsonValueKind.Array
            ? languages.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!.Trim().ToLowerInvariant())
                .Where(l => l.Length is >= 2 and <= 3 && l.All(char.IsAsciiLetterLower)).Distinct(StringComparer.Ordinal).ToArray()
            : [];

    private static bool ValidId(string id) =>
        id.Length is > 0 and <= 256 && !id.Any(char.IsWhiteSpace) && !id.Any(char.IsControl);

    private static bool Flag(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;

    private sealed record Entry(string Id, string? Name, string[] Aliases, JsonElement Capabilities, JsonElement? Audio);
}
