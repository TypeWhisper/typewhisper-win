using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.Soniox;

/// <summary>A speech-to-text model from Soniox's model list.</summary>
internal sealed record SonioxSttModel(string Id, string? AliasedModelId, string? Name, string Mode, IReadOnlyList<string> Languages);

/// <summary>A text-to-speech model from Soniox's model list, with its voice ids.</summary>
internal sealed record SonioxTtsModel(string Id, string? AliasedModelId, string? Name, IReadOnlyList<string> Languages, IReadOnlyList<string> Voices);

/// <summary>The model lists of one region, cached so that automatic selection works offline.</summary>
internal sealed record SonioxCatalog(
    string Region,
    DateTimeOffset FetchedAt,
    IReadOnlyList<SonioxSttModel> SttModels,
    IReadOnlyList<SonioxTtsModel> TtsModels);

/// <summary>Chooses Soniox models from the cached catalog, with built-in fallbacks when none is cached.</summary>
internal static partial class SonioxModels
{
    internal const string Automatic = "automatic";
    internal const string RealtimeMode = "real_time";
    internal const string AsyncMode = "async";
    internal const string RealtimePrefix = "stt-rt-";
    internal const string AsyncPrefix = "stt-async-";

    // Soniox retires v5 on 2026-12-15 and routes it to v6; new installations start on v6.
    internal const string FallbackRealtimeModelId = "stt-rt-v6";
    internal const string FallbackAsyncModelId = "stt-async-v6";
    internal const string FallbackTtsModelId = "tts-rt-v2";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex ModelIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._'-]{0,63}$")]
    private static partial Regex VoiceIdPattern();

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex LanguagePattern();

    [GeneratedRegex("[0-9]+")]
    private static partial Regex VersionNumbers();

    internal static bool IsValidModelId(string? id) => id is not null && ModelIdPattern().IsMatch(id);

    internal static bool IsValidVoiceId(string? id) => id is not null && VoiceIdPattern().IsMatch(id);

    internal static IReadOnlyList<SonioxSttModel> ParseSttModels(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ModelArray(document.RootElement).Select(model =>
        {
            var id = GetString(model, "id");
            var mode = GetString(model, "transcription_mode");
            if (!IsValidModelId(id) || mode is not (RealtimeMode or AsyncMode))
                return null;
            var alias = GetString(model, "aliased_model_id");
            return new SonioxSttModel(id!, IsValidModelId(alias) ? alias : null, Trimmed(GetString(model, "name")), mode, Languages(model));
        }).OfType<SonioxSttModel>().DistinctBy(model => model.Id).ToArray();
    }

    internal static IReadOnlyList<SonioxTtsModel> ParseTtsModels(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ModelArray(document.RootElement).Select(model =>
        {
            var id = GetString(model, "id");
            if (!IsValidModelId(id))
                return null;
            var alias = GetString(model, "aliased_model_id");
            // An alias speaks with its target's voices; caching them once keeps the settings file small.
            var voices = !IsValidModelId(alias) && model.TryGetProperty("voices", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(voice => voice.ValueKind == JsonValueKind.Object ? GetString(voice, "id") : null)
                    .Where(IsValidVoiceId).Select(voice => voice!).Distinct(StringComparer.Ordinal).ToArray()
                : [];
            return new SonioxTtsModel(id!, IsValidModelId(alias) ? alias : null, Trimmed(GetString(model, "name")), Languages(model), voices);
        }).OfType<SonioxTtsModel>().DistinctBy(model => model.Id).ToArray();
    }

    /// <summary>Drops cached entries that a newer plugin or a damaged settings file would not accept.</summary>
    internal static SonioxCatalog? Sanitize(SonioxCatalog? catalog)
    {
        if (catalog is null || string.IsNullOrWhiteSpace(catalog.Region))
            return null;
        var stt = (catalog.SttModels ?? []).Where(model => model is not null && IsValidModelId(model.Id)
                && model.Mode is RealtimeMode or AsyncMode)
            .Select(model => model with
            {
                AliasedModelId = IsValidModelId(model.AliasedModelId) ? model.AliasedModelId : null,
                Languages = (model.Languages ?? []).Where(IsLanguageCode).ToArray()
            }).DistinctBy(model => model.Id).ToArray();
        var tts = (catalog.TtsModels ?? []).Where(model => model is not null && IsValidModelId(model.Id))
            .Select(model => model with
            {
                AliasedModelId = IsValidModelId(model.AliasedModelId) ? model.AliasedModelId : null,
                Languages = (model.Languages ?? []).Where(IsLanguageCode).ToArray(),
                Voices = (model.Voices ?? []).Where(IsValidVoiceId).Distinct(StringComparer.Ordinal).ToArray()
            }).DistinctBy(model => model.Id).ToArray();
        return catalog with { SttModels = stt, TtsModels = tts };
    }

    /// <summary>
    /// The newest model for a mode. An alias named latest, default or recommended wins; otherwise the highest
    /// version that is neither an alias nor a preview. Previews stay selectable by version but never become automatic.
    /// </summary>
    internal static string PreferredSttModelId(IReadOnlyList<SonioxSttModel>? models, string mode)
    {
        var fallback = mode == AsyncMode ? FallbackAsyncModelId : FallbackRealtimeModelId;
        var scoped = (models ?? []).Where(model => model.Mode == mode).ToArray();
        return PreferredId(scoped.Select(model => (model.Id, model.AliasedModelId)), fallback);
    }

    internal static string PreferredTtsModelId(IReadOnlyList<SonioxTtsModel>? models) =>
        PreferredId((models ?? []).Select(model => (model.Id, model.AliasedModelId)), FallbackTtsModelId);

    private static string PreferredId(IEnumerable<(string Id, string? Alias)> models, string fallback)
    {
        var list = models.ToArray();
        var alias = list.FirstOrDefault(model => model.Alias is not null && IsMovingAlias(model.Id));
        if (alias.Id is not null)
            return alias.Id;
        var newest = list.Where(model => model.Alias is null && !model.Id.Contains("preview", StringComparison.Ordinal))
            .Select(model => model.Id).OrderBy(id => id, ModelIdComparer.Instance).LastOrDefault();
        return newest ?? fallback;
    }

    private static bool IsMovingAlias(string id) =>
        id.Contains("latest", StringComparison.Ordinal) || id.Contains("default", StringComparison.Ordinal)
        || id.Contains("recommended", StringComparison.Ordinal);

    /// <summary>The version part shared by a real-time and an async model, such as "v6" for stt-rt-v6 and stt-async-v6.</summary>
    internal static string? VersionKey(string id) =>
        id.StartsWith(RealtimePrefix, StringComparison.Ordinal) ? id[RealtimePrefix.Length..]
        : id.StartsWith(AsyncPrefix, StringComparison.Ordinal) ? id[AsyncPrefix.Length..]
        : null;

    internal static bool IsValidVersionKey(string? key) => key is { Length: > 0 } && IsValidModelId(RealtimePrefix + key);

    /// <summary>Versions offered in settings: every model Soniox lists that is not an alias of another one, newest first.</summary>
    internal static IReadOnlyList<string> SelectableVersions(IReadOnlyList<SonioxSttModel>? models) =>
        (models ?? []).Where(model => model.AliasedModelId is null)
            .Select(model => VersionKey(model.Id)).Where(IsValidVersionKey).Select(key => key!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(key => RealtimePrefix + key, ModelIdComparer.Instance).ToArray();

    /// <summary>
    /// Resolves the stored selection to the model id for one mode. A pinned version keeps both modes on the same
    /// generation even when the cached list lacks one of them, because Soniox names both modes the same way.
    /// </summary>
    internal static string ResolveSttModelId(string selection, IReadOnlyList<SonioxSttModel>? models, string mode)
    {
        if (selection == Automatic || !IsValidVersionKey(selection))
            return PreferredSttModelId(models, mode);
        return (mode == AsyncMode ? AsyncPrefix : RealtimePrefix) + selection;
    }

    internal static bool IsLanguageCode(string? code) => code is not null && LanguagePattern().IsMatch(code);

    private static IEnumerable<JsonElement> ModelArray(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Soniox returned an invalid model list.");
        return models.EnumerateArray().Where(model => model.ValueKind == JsonValueKind.Object).Select(model => model.Clone()).ToArray();
    }

    private static string[] Languages(JsonElement model) =>
        model.TryGetProperty("languages", out var languages) && languages.ValueKind == JsonValueKind.Array
            ? languages.EnumerateArray().Select(language => language.ValueKind switch
            {
                JsonValueKind.Object => GetString(language, "code"),
                JsonValueKind.String => language.GetString(),
                _ => null
            }).Where(IsLanguageCode).Select(code => code!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    private static string? Trimmed(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= 120 ? trimmed : trimmed[..120];
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Orders ids by the numbers they contain, so v10 sorts after v9.</summary>
    internal sealed class ModelIdComparer : IComparer<string>
    {
        internal static ModelIdComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            var left = Numbers(x);
            var right = Numbers(y);
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var compared = left[index].CompareTo(right[index]);
                if (compared != 0)
                    return compared;
            }

            return left.Length != right.Length
                ? left.Length.CompareTo(right.Length)
                : string.CompareOrdinal(x, y);
        }

        private static long[] Numbers(string? id) =>
            id is null ? [] : VersionNumbers().Matches(id)
                .Select(match => long.TryParse(match.Value, out var number) ? number : long.MaxValue).ToArray();
    }
}
