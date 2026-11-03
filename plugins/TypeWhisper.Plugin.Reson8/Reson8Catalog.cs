using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.Reson8;

/// <summary>
/// A speech-to-text model as <c>GET /v1/model</c> lists it. The list is not in Reson8's API reference,
/// so every field beyond the id and name is optional.
/// </summary>
internal sealed record Reson8Model(
    string Id,
    string DisplayName,
    IReadOnlyList<string>? Endpoints,
    IReadOnlyList<string>? Languages,
    bool? IsDefault);

/// <summary>How the transcript treats filler words. Natural sends no <c>filler_mode</c>, the API's default.</summary>
internal static class Reson8FillerModes
{
    internal const string Natural = "natural";
    internal const string Clean = "clean";
    internal const string Verbatim = "verbatim";

    internal static IReadOnlyList<string> All { get; } = [Natural, Clean, Verbatim];

    internal static string Normalize(string? mode) =>
        All.FirstOrDefault(value => value.Equals(mode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Natural;
}

/// <summary>
/// Patterns for structured tokens such as order codes, offered as a model of its own so that a workflow selects
/// them where such tokens are spoken. Reson8 advises against applying patterns to every request.
/// </summary>
internal sealed record Reson8PatternSet(string Id, string Name, IReadOnlyList<string> Patterns)
{
    internal const string ModelIdPrefix = "patterns:";

    internal string ModelId => ModelIdPrefix + Id;

    /// <summary>The patterns as Reson8 takes them.</summary>
    internal string? QueryValue
    {
        get
        {
            var patterns = (Patterns ?? []).Select(pattern => pattern.Trim()).Where(pattern => pattern.Length > 0).ToArray();
            return patterns.Length == 0 ? null : string.Join(",", patterns);
        }
    }
}

internal static partial class Reson8Catalog
{
    internal const int MaximumPatternSets = 20;
    internal const int MaximumPatternsPerSet = 50;
    internal const int MaximumPatternLength = 120;
    internal const int MaximumPatternSetNameLength = 60;
    /// <summary>Patterns take the place of phrases in the query string, so they share its budget below Reson8's 414.</summary>
    internal const int MaximumEncodedPatternsLength = Reson8Plugin.MaximumEncodedPhrasesLength;

    /// <summary>
    /// Query parameters a model rejects, as Reson8's model documentation lists them; <c>GET /v1/model</c> does not.
    /// Models not listed take every parameter. https://docs.reson8.dev/speech-to-text/models/
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> UnsupportedParameters =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["rumble-2-flash"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "diarize", "include_timestamps", "include_words", "phrases", "custom_model_id", "patterns", "filler_mode",
            },
        };

    /// <summary>Languages of the default model, used until the model list was fetched.</summary>
    internal static IReadOnlyList<string> FallbackLanguages { get; } = ["nl", "en", "fr", "de", "it", "pl", "pt", "es", "sv", "fy"];

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex ModelIdPattern();

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex LanguagePattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugSeparators();

    internal static bool IsValidModelId(string? id) => id is not null && ModelIdPattern().IsMatch(id);

    internal static bool TakesParameter(Reson8Model? model, string parameter) =>
        model is null || !UnsupportedParameters.TryGetValue(model.Id, out var rejected) || !rejected.Contains(parameter);

    internal static IReadOnlyList<Reson8Model> ParseModels(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("The Reson8 model list is not an array.");
        return Sanitize(document.RootElement.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.Object)
            .Select(element => new Reson8Model(
                GetString(element, "id") ?? "",
                GetString(element, "displayName") ?? "",
                StringArray(element, "endpoints"),
                StringArray(element, "languages"),
                element.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? isDefault.GetBoolean() : null))
            .ToArray());
    }

    /// <summary>Drops entries a damaged settings file or an unexpected server answer would add.</summary>
    internal static IReadOnlyList<Reson8Model> Sanitize(IEnumerable<Reson8Model?>? models) =>
        (models ?? []).Where(model => model is not null && IsValidModelId(model.Id) && !string.IsNullOrWhiteSpace(model.DisplayName))
            .Select(model => model! with
            {
                DisplayName = model!.DisplayName.Trim() is { Length: > 80 } name ? name[..80] : model.DisplayName.Trim(),
                Endpoints = model.Endpoints?.Where(endpoint => endpoint is not null).Select(endpoint => endpoint.Trim()).ToArray(),
                Languages = model.Languages?.Where(LanguagePatternMatches).ToArray(),
            })
            .DistinctBy(model => model.Id).ToArray();

    private static bool LanguagePatternMatches(string? code) => code is not null && LanguagePattern().IsMatch(code);

    /// <summary>
    /// Reads pattern sets from the settings text: blocks separated by an empty line, each with the name on its
    /// first line and one pattern per following line. A set keeps its id while its name stays the same, so
    /// workflows that select it keep working when its patterns change.
    /// </summary>
    internal static IReadOnlyList<Reson8PatternSet> ParsePatternSets(string? text, IReadOnlyList<Reson8PatternSet> previous)
    {
        var blocks = new List<List<string>>();
        var current = new List<string>();
        foreach (var rawLine in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                if (current.Count > 0)
                    blocks.Add(current);
                current = [];
                continue;
            }

            if (line.Any(char.IsControl))
                throw new ArgumentException("Patterns must not contain control characters.");
            current.Add(line);
        }

        if (current.Count > 0)
            blocks.Add(current);
        if (blocks.Count > MaximumPatternSets)
            throw new ArgumentException($"Use at most {MaximumPatternSets} pattern sets.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsed = new List<(string Name, string[] Patterns)>();
        foreach (var block in blocks)
        {
            var name = block[0];
            var patterns = block.Skip(1).ToArray();
            if (name.Length > MaximumPatternSetNameLength)
                throw new ArgumentException($"A pattern set name has at most {MaximumPatternSetNameLength} characters.");
            if (!names.Add(name))
                throw new ArgumentException($"The pattern set name “{name}” is used twice.");
            if (patterns.Length == 0)
                throw new ArgumentException($"The pattern set “{name}” needs at least one pattern below its name.");
            if (patterns.Length > MaximumPatternsPerSet)
                throw new ArgumentException($"A pattern set has at most {MaximumPatternsPerSet} patterns.");
            if (patterns.FirstOrDefault(pattern => pattern.Length > MaximumPatternLength) is { } longPattern)
                throw new ArgumentException($"The pattern “{longPattern[..20]}…” is longer than {MaximumPatternLength} characters.");
            if (EncodedPatternsLength(patterns) is var encoded && encoded > MaximumEncodedPatternsLength)
                throw new ArgumentException(
                    $"The pattern set “{name}” is too long for a Reson8 request ({encoded} of {MaximumEncodedPatternsLength} characters once encoded). Shorten its patterns or split it into two sets.");
            parsed.Add((name, patterns));
        }

        // Sets that keep their name reserve their ids first, so a new set whose name gives the same slug cannot
        // take one, wherever it is inserted.
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ids = parsed.Select(set => previous.FirstOrDefault(old => old.Name.Equals(set.Name, StringComparison.OrdinalIgnoreCase))?.Id)
            .Select(id => id is not null && used.Add(id) ? id : null).ToArray();
        for (var index = 0; index < ids.Length; index++)
        {
            if (ids[index] is not null)
                continue;
            ids[index] = UniqueSlug(parsed[index].Name, used);
            used.Add(ids[index]!);
        }

        return parsed.Select((set, index) => new Reson8PatternSet(ids[index]!, set.Name, set.Patterns)).ToArray();
    }

    internal static string FormatPatternSets(IReadOnlyList<Reson8PatternSet> sets) =>
        string.Join("\n\n", sets.Select(set => string.Join("\n", [set.Name, .. set.Patterns])));

    /// <summary>Keeps only stored sets that the parser would also produce.</summary>
    internal static IReadOnlyList<Reson8PatternSet> SanitizePatternSets(IEnumerable<Reson8PatternSet?>? sets)
    {
        var result = new List<Reson8PatternSet>();
        foreach (var set in sets ?? [])
        {
            if (set is null || !IsValidModelId(set.Id) || string.IsNullOrWhiteSpace(set.Name)
                || result.Any(existing => existing.Id == set.Id || existing.Name.Equals(set.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            var patterns = (set.Patterns ?? []).Where(pattern => !string.IsNullOrWhiteSpace(pattern) && pattern.Length <= MaximumPatternLength)
                .Select(pattern => pattern.Trim()).Take(MaximumPatternsPerSet).ToArray();
            if (patterns.Length > 0 && result.Count < MaximumPatternSets && EncodedPatternsLength(patterns) <= MaximumEncodedPatternsLength)
                result.Add(set with { Name = set.Name.Trim(), Patterns = patterns });
        }

        return result;
    }

    /// <summary>The length of the patterns in the query string, measured like the phrases.</summary>
    internal static int EncodedPatternsLength(IEnumerable<string> patterns) => Reson8Query.Escape(string.Join(",", patterns)).Length;

    private static string UniqueSlug(string name, ISet<string> used)
    {
        var slug = SlugSeparators().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 40)
            slug = slug[..40].Trim('-');
        if (slug.Length == 0 || !char.IsAsciiLetterOrDigit(slug[0]))
            slug = "set";
        var candidate = slug;
        for (var index = 2; used.Contains(candidate); index++)
            candidate = $"{slug}-{index}";
        return candidate;
    }

    private static string[]? StringArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToArray()
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Builds Reson8 query strings, percent-encoding values so that "C++" keeps its plus signs. Commas stay literal,
/// as in the macOS plugin, because Reson8 reads languages, phrases and patterns as comma-separated lists.
/// </summary>
internal sealed class Reson8Query
{
    private readonly List<KeyValuePair<string, string>> _items = [];

    internal IReadOnlyList<KeyValuePair<string, string>> Items => _items;

    internal Reson8Query Add(string name, string value)
    {
        _items.Add(new(name, value));
        return this;
    }

    internal bool Contains(string name) => _items.Any(item => item.Key == name);

    internal string? Value(string name) => _items.FirstOrDefault(item => item.Key == name).Value;

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var (name, value) in _items)
        {
            if (builder.Length > 0)
                builder.Append('&');
            builder.Append(Uri.EscapeDataString(name)).Append('=').Append(Escape(value));
        }

        return builder.ToString();
    }

    internal static string Escape(string value) => Uri.EscapeDataString(value).Replace("%2C", ",", StringComparison.Ordinal);
}
