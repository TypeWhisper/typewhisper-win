using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Calls run synchronously on the app dispatcher, alongside UI dictionary edits.
// Read the latest snapshot for every request; do not keep a second dictionary cache.
internal sealed class LocalApiDataHandler(string dictionaryPath, string workflowPath)
{
    internal LocalApiResponse? Handle(LocalApiRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Path is not ("/v1/rules" or "/v1/profiles" or "/v1/rules/toggle" or "/v1/profiles/toggle"
            or "/v1/dictionary/terms" or "/v1/dictionary/corrections")) return null;
        try
        {
            if (request.Path.StartsWith("/v1/dictionary/", StringComparison.Ordinal)) return Dictionary(request, ct);
            return Workflows(request, ct);
        }
        catch (ApiDataException ex) { return Error(ex.Status, ex.Message); }
        catch (JsonException) { return Error(400, "Invalid JSON or dictionary values."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Error(500, "The stored data could not be read or saved."); }
    }

    private static LocalApiResponse Error(int status, string message) => LocalApiResponse.Json(status,
        new { error = new { code = status switch { 400 => "bad_request", 404 => "not_found", 405 => "method_not_allowed", _ => "error" }, message } });

    private LocalApiResponse Workflows(LocalApiRequest request, CancellationToken ct)
    {
        var toggle = request.Path.EndsWith("/toggle", StringComparison.Ordinal);
        if (request.Method != (toggle ? "PUT" : "GET")) return Error(405, toggle ? "Use PUT." : "Use GET.");
        if (request.Body.Length != 0) return Error(400, "This endpoint accepts no request body.");
        if (request.Query.Keys.Any(key => !toggle || key != "id")) return Error(400, "Unknown query parameter.");
        var store = new ManualWorkflowStore(workflowPath);
        IReadOnlyList<Workflow> workflows;
        try { workflows = store.Read(); }
        catch (JsonException) { return Error(500, "The workflow catalog could not be read."); }
        if (!toggle)
        {
            var entries = workflows.Select(workflow =>
            {
                var hints = AppSettings.NormalizeLanguageHints(workflow.Behavior.InputLanguageHints);
                var language = workflow.Behavior.InputLanguage;
                var inherited = string.IsNullOrWhiteSpace(language) || language is "global" or "inherit_global";
                return new
                {
                    id = workflow.Id, name = workflow.Name, is_enabled = workflow.IsEnabled, priority = workflow.SortOrder,
                    bundle_identifiers = workflow.Trigger.ProcessNames, url_patterns = workflow.Trigger.WebsitePatterns,
                    input_language = hints.Count > 0 || inherited ? null : language,
                    language_mode = hints.Count > 0 ? "hints" : inherited ? "inherit_global" : language == "auto" ? "auto" : "exact",
                    language_hints = hints, translation_target_language = workflow.Behavior.TranslationTarget
                };
            }).ToArray();
            return LocalApiResponse.Json(200, new { rules = entries, profiles = entries });
        }
        if (!request.Query.TryGetValue("id", out var id) || !Guid.TryParse(id, out var uuid))
            return Error(400, "Missing or invalid 'id' query parameter.");
        var current = workflows.FirstOrDefault(workflow => Guid.TryParse(workflow.Id, out var candidate) && candidate == uuid);
        if (current is null) return Error(404, "Rule not found.");
        ct.ThrowIfCancellationRequested();
        Workflow updated;
        try { updated = store.SetEnabled(current.Id, !current.IsEnabled); }
        catch (InvalidOperationException) { return Error(404, "Rule not found."); }
        return LocalApiResponse.Json(200, new { id = updated.Id, name = updated.Name, rule_name = updated.Name,
            profile_name = updated.Name, is_enabled = updated.IsEnabled });
    }

    private LocalApiResponse Dictionary(LocalApiRequest request, CancellationToken ct)
    {
        if (request.Method is not ("GET" or "PUT" or "DELETE")) return Error(405, "Use GET, PUT or DELETE.");
        if (request.Query.Count != 0) return Error(400, "This endpoint accepts no query parameters.");
        var terms = request.Path.EndsWith("/terms", StringComparison.Ordinal);
        if (request.Method == "GET" && request.Body.Length != 0) return Error(400, "GET accepts no request body.");
        var lexicon = new Lexicon(dictionaryPath);
        if (lexicon.LastError is not null) return Error(500, "The dictionary could not be read.");
        if (request.Method == "GET") return DictionaryResponse(lexicon, terms);
        using var document = ReadBody(request);
        var root = document.RootElement;
        var current = File.Exists(dictionaryPath)
            ? LexiconTransfer.ReadDictionary(File.ReadAllText(dictionaryPath), allowPackEntries: true)
                .Where(entry => !entry.Id.StartsWith("pack:", StringComparison.Ordinal)).ToList()
            : new List<DictionaryEntry>();
        var kind = terms ? DictionaryEntryType.Term : DictionaryEntryType.Correction;
        bool deleted = false;
        if (request.Method == "DELETE")
        {
            Fields(root, terms ? ["term"] : ["original"]);
            var key = RequiredString(root, terms ? "term" : "original").Trim();
            if (key.Length == 0) return Error(400, "The phrase cannot be empty.");
            deleted = current.RemoveAll(entry => entry.EntryType == kind && entry.Original.Equals(key,
                StringComparison.OrdinalIgnoreCase)) > 0;
        }
        else if (terms)
        {
            Fields(root, ["terms", "term_entries", "replace"]);
            var hasTerms = root.TryGetProperty("terms", out var values);
            var hasEntries = root.TryGetProperty("term_entries", out var entries);
            if (hasTerms == hasEntries) return Error(400, "Use either 'terms' or 'term_entries'.");
            var source = hasTerms ? values : entries;
            if (source.ValueKind != JsonValueKind.Array || source.GetArrayLength() > 10000)
                return Error(400, "Provide an array of at most 10,000 terms.");
            // Validate the entire batch before changing the persistent dictionary.
            var incoming = new List<(string Key, float? Similarity)>();
            foreach (var item in source.EnumerateArray())
            {
                if (hasTerms)
                {
                    if (item.ValueKind != JsonValueKind.String) return Error(400, "Terms must be strings.");
                    incoming.Add((item.GetString()!.Trim(), null));
                }
                else
                {
                    Fields(item, ["term", "ctc_min_similarity", "ctcMinSimilarity"]);
                    if (item.TryGetProperty("ctc_min_similarity", out _) && item.TryGetProperty("ctcMinSimilarity", out _))
                        return Error(400, "Provide one CTC similarity value.");
                    var hasSimilarity = item.TryGetProperty("ctc_min_similarity", out var similarity)
                        || item.TryGetProperty("ctcMinSimilarity", out similarity);
                    float? threshold = null;
                    if (hasSimilarity && similarity.ValueKind != JsonValueKind.Null)
                    {
                        if (similarity.ValueKind != JsonValueKind.Number || !similarity.TryGetSingle(out var parsed))
                            return Error(400, "CTC similarity must be a number.");
                        threshold = parsed;
                    }
                    incoming.Add((RequiredString(item, "term").Trim(), threshold));
                }
            }
            var previous = current.ToArray();
            if (OptionalBoolean(root, "replace", false)) current.RemoveAll(entry => entry.EntryType == DictionaryEntryType.Term);
            foreach (var (key, similarity) in incoming)
            {
                var existing = current.FirstOrDefault(entry => entry.EntryType == kind && entry.Original.Equals(key, StringComparison.OrdinalIgnoreCase))
                    ?? previous.FirstOrDefault(entry => entry.EntryType == kind && entry.Original.Equals(key, StringComparison.OrdinalIgnoreCase));
                var next = (existing ?? NewEntry(kind, key)) with
                { Original = key, CtcMinSimilarity = similarity, IsEnabled = true, UpdatedAt = DateTime.UtcNow };
                current.RemoveAll(entry => entry.Id == next.Id);
                current.Add(next);
            }
        }
        else
        {
            Fields(root, ["original", "replacement", "caseSensitive"]);
            var key = RequiredString(root, "original").Trim();
            var replacement = RequiredString(root, "replacement");
            var sensitive = OptionalBoolean(root, "caseSensitive", false);
            var existing = current.FirstOrDefault(entry => entry.EntryType == kind && entry.Original.Equals(key, StringComparison.OrdinalIgnoreCase));
            var next = (existing ?? NewEntry(kind, key)) with { Original = key, Replacement = replacement,
                CaseSensitive = sensitive, IsEnabled = true, UpdatedAt = DateTime.UtcNow };
            current.RemoveAll(entry => entry.Id == next.Id);
            current.Add(next);
        }
        var json = LexiconTransfer.WriteDictionary(current);
        _ = LexiconTransfer.ReadDictionary(json); // Strict validation before the atomic write.
        ct.ThrowIfCancellationRequested();
        if (lexicon.Import(json, snippets: false, replace: true) is not null)
            return Error(500, "The dictionary could not be saved.");
        if (request.Method == "DELETE")
            return LocalApiResponse.Json(200, new { deleted, count = lexicon.Entries.Count(entry =>
                entry.Kind == (terms ? LexiconKind.Word : LexiconKind.Correction) && (!terms || entry.Enabled)) });
        return DictionaryResponse(lexicon, terms);
    }

    private static DictionaryEntry NewEntry(DictionaryEntryType kind, string key) => new()
    { Id = Guid.NewGuid().ToString(), EntryType = kind, Original = key };

    private static LocalApiResponse DictionaryResponse(Lexicon lexicon, bool terms)
    {
        if (terms)
        {
            var entries = lexicon.Entries.Where(entry => entry.Kind == LexiconKind.Word && entry.Enabled)
                .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
            return LocalApiResponse.Json(200, new { terms = entries.Select(entry => entry.Key),
                term_entries = entries.Select(entry => new { term = entry.Key, ctc_min_similarity = entry.CtcMinSimilarity }), count = entries.Length });
        }
        var corrections = lexicon.Entries.Where(entry => entry.Kind == LexiconKind.Correction)
            .Select(entry => new { original = entry.Key, replacement = entry.Value, caseSensitive = entry.CaseSensitive }).ToArray();
        return LocalApiResponse.Json(200, new { corrections, count = corrections.Length });
    }

    private static JsonDocument ReadBody(LocalApiRequest request)
    {
        if (request.Body.Length == 0 || request.Body.Length > 5_000_000) throw new ApiDataException(400, "Provide a JSON request body of at most 5 MB.");
        if (request.ContentType?.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) != true)
            throw new ApiDataException(400, "Use Content-Type: application/json.");
        return JsonDocument.Parse(request.Body);
    }
    private static void Fields(JsonElement element, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ApiDataException(400, "Expected a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!names.Add(field.Name) || !allowed.Contains(field.Name, StringComparer.Ordinal))
                throw new ApiDataException(400, "Unknown or duplicate JSON field.");
    }
    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!
            : throw new ApiDataException(400, $"'{name}' must be a string.");
    private static bool OptionalBoolean(JsonElement root, string name, bool fallback) =>
        !root.TryGetProperty(name, out var value) ? fallback : value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw new ApiDataException(400, $"'{name}' must be a boolean.");
    private sealed class ApiDataException(int status, string message) : Exception(message)
    { internal int Status { get; } = status; }
}
