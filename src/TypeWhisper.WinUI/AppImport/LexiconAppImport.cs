using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal enum LexiconImportApp { WisprFlow, Handy }
internal enum AppImportOutcome { Add, Duplicate, Conflict, Unsupported }
internal sealed record AppImportLine(string Kind, string Key, string? Value, AppImportOutcome Outcome);
internal sealed record AppImportBatch(IReadOnlyList<DictionaryEntry> Dictionary, IReadOnlyList<Snippet> Snippets, int Excluded);
internal sealed record AppImportReview(bool IsSnippets, string? Baseline, string Json, IReadOnlyList<AppImportLine> Lines, int Excluded)
{
    internal int Additions => Lines.Count(line => line.Outcome == AppImportOutcome.Add);
    internal string Summary => $"{Additions} new · {Lines.Count(line => line.Outcome == AppImportOutcome.Duplicate)} already present · " +
        $"{Lines.Count(line => line.Outcome == AppImportOutcome.Conflict)} conflicts kept unchanged · " +
        $"{Excluded + Lines.Count(line => line.Outcome == AppImportOutcome.Unsupported)} excluded";
}

internal static partial class LexiconAppImport
{
    internal const int MaximumCatalogCharacters = 5_000_000;
    internal static string Name(LexiconImportApp app) => app == LexiconImportApp.WisprFlow ? "Wispr Flow" : "Handy";
    internal static string DefaultPath(LexiconImportApp app) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        app == LexiconImportApp.WisprFlow ? "Wispr Flow" : "com.pais.handy",
        app == LexiconImportApp.WisprFlow ? "flow.sqlite" : "settings_store.json");

    internal static AppImportBatch Load(LexiconImportApp app, string path, bool snippets, CancellationToken cancellationToken = default)
    {
        if (app == LexiconImportApp.Handy)
        {
            if (snippets) throw new InvalidDataException("Handy supports word import only.");
            return ReadHandy(path, cancellationToken: cancellationToken);
        }
        return MapWispr(WisprImportDatabase.Read(path, cancellationToken), snippets, cancellationToken);
    }

    internal static AppImportBatch MapWispr(IReadOnlyList<WisprImportRow> rows, bool snippets, CancellationToken cancellationToken = default)
    {
        if (rows.Count > WisprImportDatabase.MaximumRows) throw new InvalidDataException("Too many source entries. Nothing was imported.");
        var words = new List<DictionaryEntry>();
        var expansions = new List<Snippet>();
        var excluded = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Deleted || row.Snippet != snippets) { excluded++; continue; }
            var phrase = row.Phrase.Trim();
            if (snippets)
            {
                expansions.Add(new() { Id = Guid.NewGuid().ToString(), Trigger = phrase, Replacement = row.Replacement ?? "", UpdatedAt = DateTime.UtcNow });
            }
            else
            {
                var spelling = string.IsNullOrWhiteSpace(row.Replacement) ? phrase : row.Replacement.Trim();
                words.Add(Word(spelling));
                if (spelling != phrase)
                    words.Add(new() { Id = Guid.NewGuid().ToString(), EntryType = DictionaryEntryType.Correction,
                        Original = phrase, Replacement = spelling, UpdatedAt = DateTime.UtcNow });
            }
        }
        return new(words, expansions, excluded);
    }

    internal static AppImportBatch ReadHandy(string path, Func<string, byte[]>? read = null, CancellationToken cancellationToken = default)
    {
        read ??= file => ReadBounded(file, cancellationToken);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = read(path);
            cancellationToken.ThrowIfCancellationRequested();
            if (!first.AsSpan().SequenceEqual(read(path))) continue;
            try { return DecodeHandy(first); }
            catch (JsonException) when (attempt < 2) { }
        }
        throw new IOException("Could not read a stable Handy settings file. Quitting Handy and trying again can help.");
    }

    private static AppImportBatch DecodeHandy(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Choose a Handy settings_store.json file.");
        UniqueFields(root);
        if (!root.TryGetProperty("settings", out var settings) || settings.ValueKind == JsonValueKind.Null) return new([], [], 0);
        if (settings.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid Handy settings.");
        UniqueFields(settings);
        if (!settings.TryGetProperty("custom_words", out var words) || words.ValueKind == JsonValueKind.Null) return new([], [], 0);
        if (words.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid Handy word list.");
        if (words.GetArrayLength() > WisprImportDatabase.MaximumRows) throw new InvalidDataException("Handy has more than 10,000 words. Nothing was imported.");
        return new(words.EnumerateArray().Select(word => word.ValueKind == JsonValueKind.String
            ? Word(word.GetString()!.Trim()) : throw new JsonException("Invalid Handy word.")).ToArray(), [], 0);
    }

    private static void UniqueFields(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (element.EnumerateObject().Any(field => !names.Add(field.Name))) throw new JsonException("Duplicate settings field.");
    }

    private static byte[] ReadBounded(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Choose a Handy settings file smaller than 8 MB.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static DictionaryEntry Word(string value) => new()
    { Id = Guid.NewGuid().ToString(), Original = value, EntryType = DictionaryEntryType.Term, UpdatedAt = DateTime.UtcNow };

    internal static AppImportReview Review(AppImportBatch batch, bool snippets, string? baseline, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lines = new List<AppImportLine>();
        if (snippets)
        {
            var current = baseline is null ? [] : LexiconTransfer.ReadSnippets(baseline);
            var next = current.ToList();
            var budget = CatalogSize(current, entry => LexiconTransfer.WriteSnippets([entry]), cancellationToken);
            foreach (var entry in batch.Snippets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = AppImportOutcome.Add;
                var serialized = LexiconTransfer.WriteSnippets([entry]);
                try
                {
                    // Foreign text is literal. Do not silently turn its braces into TypeWhisper placeholders.
                    if (string.IsNullOrWhiteSpace(entry.Replacement) || Placeholders().IsMatch(entry.Replacement)) throw new JsonException();
                    _ = LexiconTransfer.ReadSnippets(serialized);
                    var collisions = next.Where(item => item.Trigger.Equals(entry.Trigger, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (collisions.Length > 0)
                        outcome = collisions.All(item => item.Replacement == entry.Replacement && item.CaseSensitive == entry.CaseSensitive && item.IsEnabled == entry.IsEnabled)
                            ? AppImportOutcome.Duplicate : AppImportOutcome.Conflict;
                }
                catch (JsonException) { outcome = AppImportOutcome.Unsupported; }
                lines.Add(new("Snippet", entry.Trigger, entry.Replacement, outcome));
                if (outcome == AppImportOutcome.Add) { IncludeEntry(ref budget, serialized.Length); next.Add(entry); }
            }
            if (next.Count > 10000) throw new InvalidDataException("The resulting snippet list would exceed 10,000 entries.");
            var json = LexiconTransfer.WriteSnippets(next);
            _ = LexiconTransfer.ReadSnippets(json);
            return new(true, baseline, json, lines, batch.Excluded);
        }
        else
        {
            var current = baseline is null ? [] : LexiconTransfer.ReadDictionary(baseline, allowPackEntries: true);
            var next = current.ToList();
            var budget = CatalogSize(current, entry => LexiconTransfer.WriteDictionary([entry]), cancellationToken);
            foreach (var entry in batch.Dictionary)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = AppImportOutcome.Add;
                var serialized = LexiconTransfer.WriteDictionary([entry]);
                try
                {
                    if (entry.EntryType == DictionaryEntryType.Correction && ReplacementEscapes().IsMatch(entry.Replacement ?? ""))
                        throw new JsonException("A literal source spelling cannot become a formatting command.");
                    _ = LexiconTransfer.ReadDictionary(serialized);
                    var collisions = next.Where(item => item.EntryType == entry.EntryType && item.Original.Equals(entry.Original, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (collisions.Length > 0)
                        outcome = collisions.All(item => item.Replacement == entry.Replacement && item.CaseSensitive == entry.CaseSensitive && item.IsEnabled == entry.IsEnabled && !item.IsRegex)
                            ? AppImportOutcome.Duplicate : AppImportOutcome.Conflict;
                }
                catch (JsonException) { outcome = AppImportOutcome.Unsupported; }
                lines.Add(new(entry.EntryType == DictionaryEntryType.Term ? "Word" : "Correction", entry.Original, entry.Replacement, outcome));
                if (outcome == AppImportOutcome.Add) { IncludeEntry(ref budget, serialized.Length); next.Add(entry); }
            }
            if (next.Count > 10000) throw new InvalidDataException("The resulting dictionary would exceed 10,000 entries.");
            var json = LexiconTransfer.WriteDictionary(next);
            _ = LexiconTransfer.ReadDictionary(json, allowPackEntries: true);
            return new(false, baseline, json, lines, batch.Excluded);
        }
    }

    private static int CatalogSize<T>(IEnumerable<T> entries, Func<T, string> serialize, CancellationToken cancellationToken)
    {
        var budget = 2;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IncludeEntry(ref budget, serialize(entry).Length);
        }
        return budget;
    }

    // Single-entry arrays slightly overestimate the final array, including indentation and JSON escaping.
    private static void IncludeEntry(ref int budget, int length)
    {
        if (length > MaximumCatalogCharacters - budget)
            throw new InvalidDataException("The resulting catalog would exceed five million JSON characters. Import a smaller selection.");
        budget += length;
    }

    [GeneratedRegex(@"\{(?:day|year)\}|\{(?:date|time|datetime|clipboard)(?::[^}]+)?\}")]
    private static partial Regex Placeholders();

    [GeneratedRegex(@"\\[snrt\\]")]
    private static partial Regex ReplacementEscapes();
}
