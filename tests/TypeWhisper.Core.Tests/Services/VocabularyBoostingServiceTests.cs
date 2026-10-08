using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class VocabularyBoostingServiceTests
{
    [Fact]
    public void Apply_ExactTermAlreadyPresent_LeavesTextUnchanged()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "TypeWhisper"
            });

        var result = sut.Apply("TypeWhisper is ready");

        Assert.Equal("TypeWhisper is ready", result);
    }

    [Fact]
    public void Apply_SingleWordTerm_RewritesSimilarRecognition()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "Parakeet"
            });

        var result = sut.Apply("parrakeet is loaded");

        Assert.Equal("Parakeet is loaded", result);
    }

    [Fact]
    public void Apply_MultiWordWindow_RewritesToStoredTerm()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "TypeWhisper"
            });

        var result = sut.Apply("type whisper for windows");

        Assert.Equal("TypeWhisper for windows", result);
    }

    [Fact]
    public void Apply_LowSimilarity_DoesNotRewrite()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "Parakeet"
            });

        var result = sut.Apply("papaya is loaded");

        Assert.Equal("papaya is loaded", result);
    }

    [Fact]
    public void Apply_AmbiguousMatchWithinMargin_DoesNotRewrite()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "Parakeet"
            },
            new DictionaryEntry
            {
                Id = "manual-2",
                EntryType = DictionaryEntryType.Term,
                Original = "Parakeat"
            });

        var result = sut.Apply("parakeit is loaded");

        Assert.Equal("parakeit is loaded", result);
    }

    [Fact]
    public void Apply_LongerTerm_WinsOverShorterOverlap()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "Visual Studio"
            },
            new DictionaryEntry
            {
                Id = "manual-2",
                EntryType = DictionaryEntryType.Term,
                Original = "Studio"
            });

        var result = sut.Apply("visual studeo project");

        Assert.Equal("Visual Studio project", result);
    }

    [Fact]
    public void Apply_ManualTerm_WinsOverPackVariant()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "pack:dotnet:typewhisper",
                EntryType = DictionaryEntryType.Term,
                Original = "typewhisper"
            },
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "TypeWhisper"
            });

        var result = sut.Apply("type whisper");

        Assert.Equal("TypeWhisper", result);
    }

    [Fact]
    public void Apply_DisabledTerms_AreIgnored()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "TypeWhisper",
                IsEnabled = false
            });

        var result = sut.Apply("type whisper");

        Assert.Equal("type whisper", result);
    }

    [Fact]
    public void Apply_CorrectionEntries_AreIgnoredAsBoostSource()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Correction,
                Original = "type whisper",
                Replacement = "TypeWhisper"
            });

        var result = sut.Apply("type whisper");

        Assert.Equal("type whisper", result);
    }

    [Fact]
    public void Apply_HyphenAndWhitespaceNormalization_RewritesToStoredForm()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "Type-Whisper"
            });

        var result = sut.Apply("type whisper");

        Assert.Equal("Type-Whisper", result);
    }

    [Fact]
    public void Apply_TermWithReplacement_UsesCanonicalReplacementAsOutput()
    {
        var sut = CreateSut(
            new DictionaryEntry
            {
                Id = "manual-1",
                EntryType = DictionaryEntryType.Term,
                Original = "Type visped.",
                Replacement = "TypeWhisper"
            });

        var result = sut.Apply("type whisper");

        Assert.Equal("TypeWhisper", result);
    }

    private static VocabularyBoostingService CreateSut(params DictionaryEntry[] entries) =>
        new(new FakeDictionaryService(entries));

    [Fact]
    public void Snapshot_ReusesCatalogWithoutObservingLaterCollectionEdits()
    {
        DictionaryEntry[] entries = [new() { Id = "word", EntryType = DictionaryEntryType.Term, Original = "TypeWhisper" }];
        var snapshot = VocabularyBoostingService.CreateSnapshot(entries);
        entries[0] = entries[0] with { Original = "SomethingElse" };
        Assert.Equal("TypeWhisper", snapshot.Apply("type whisper"));
        Assert.Equal("TypeWhisper for windows", snapshot.Apply("type whisper for windows"));
    }

    [Fact]
    public void Apply_IndexedCatalog_MatchesStraightforwardReferenceForGeneratedCatalog()
    {
        var random = new Random(20261008);
        var entries = GenerateEntries(random, 400);
        var sut = VocabularyBoostingService.CreateSnapshot(entries);
        var reference = new ReferenceVocabularyBoosting(entries);
        var rewritten = 0;

        for (var i = 0; i < 300; i++)
        {
            var sentence = GenerateSentence(random, entries);
            var expected = reference.Apply(sentence);
            Assert.Equal(expected, sut.Apply(sentence));
            if (expected != sentence) rewritten++;
        }

        // The comparison must exercise real rewrites, not only sentences both implementations leave alone.
        Assert.InRange(rewritten, 40, 300);
    }

    private static readonly string[] Syllables =
        ["ta", "ri", "mo", "ken", "shi", "par", "whis", "vel", "ü", "ño", "ck", "stu", "dio", "é", "ber", "lin", "x", "7"];

    private static string GenerateWord(Random random)
    {
        var builder = new System.Text.StringBuilder();
        var count = random.Next(1, 4);
        for (var i = 0; i < count; i++) builder.Append(Syllables[random.Next(Syllables.Length)]);
        var word = builder.ToString();
        return random.Next(3) == 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word;
    }

    private static List<DictionaryEntry> GenerateEntries(Random random, int count)
    {
        var entries = new List<DictionaryEntry>();
        for (var i = 0; i < count; i++)
        {
            var tokens = random.Next(10) < 6 ? 1 : random.Next(2, 6);
            var joiner = random.Next(4) switch { 0 => "-", 1 => "_", 2 => "/", _ => " " };
            var original = string.Join(joiner, Enumerable.Range(0, tokens).Select(_ => GenerateWord(random)));
            var kind = random.Next(12);
            entries.Add(new DictionaryEntry
            {
                Id = (random.Next(4) == 0 ? "pack:test:" : "manual-") + i,
                EntryType = kind == 0 ? DictionaryEntryType.Correction : DictionaryEntryType.Term,
                Original = kind == 1 ? original.ToUpperInvariant() : original,
                Replacement = kind == 2 ? GenerateWord(random) : kind == 0 ? "ignored" : null,
                IsEnabled = kind != 3
            });
        }

        // Near-duplicates that differ only in case or separators compete for the same windows.
        foreach (var entry in entries.Where(e => e.EntryType == DictionaryEntryType.Term).Take(40).ToArray())
            entries.Add(entry with { Id = entry.Id + "-dup", Original = entry.Original.ToLowerInvariant().Replace(' ', '-') });

        return entries;
    }

    private static string Distort(Random random, string text)
    {
        var chars = text.ToList();
        var edits = random.Next(0, 3);
        for (var i = 0; i < edits && chars.Count > 1; i++)
        {
            var index = random.Next(chars.Count);
            switch (random.Next(5))
            {
                case 0: chars[index] = (char)('a' + random.Next(26)); break;
                case 1: chars.RemoveAt(index); break;
                case 2: chars.Insert(index, (char)('a' + random.Next(26))); break;
                case 3: if (index + 1 < chars.Count) (chars[index], chars[index + 1]) = (chars[index + 1], chars[index]); break;
                default: chars[index] = char.IsUpper(chars[index]) ? char.ToLowerInvariant(chars[index]) : char.ToUpperInvariant(chars[index]); break;
            }
        }

        var distorted = new string(chars.ToArray());
        return random.Next(4) switch
        {
            0 => distorted.Replace('-', ' ').Replace('_', ' '),
            1 => distorted.ToLowerInvariant(),
            _ => distorted
        };
    }

    private static string GenerateSentence(Random random, List<DictionaryEntry> entries)
    {
        var words = new List<string>();
        var count = random.Next(2, 16);
        for (var i = 0; i < count; i++)
        {
            var word = random.Next(2) == 0 ? Distort(random, entries[random.Next(entries.Count)].Original) : GenerateWord(random);
            if (random.Next(5) == 0) word += random.Next(3) switch { 0 => ",", 1 => ".", _ => "!" };
            if (random.Next(8) == 0) word = "(" + word + ")";
            words.Add(word);
        }

        return string.Join(random.Next(6) == 0 ? "  " : " ", words);
    }

    // A straightforward copy of the scan before terms were grouped and the distance rows reused: every term is
    // scored against every window, and the candidates of a window are sorted instead of ranked while scanning.
    private sealed class ReferenceVocabularyBoosting
    {
        private const int MaxWindowTokens = 4;
        private const int MaxReplacements = 10;
        private const double AmbiguityMargin = 0.08;
        private readonly Term[] _terms;

        public ReferenceVocabularyBoosting(IReadOnlyList<DictionaryEntry> entries) =>
            _terms = entries
                .Where(entry => entry.IsEnabled && entry.EntryType == DictionaryEntryType.Term && !string.IsNullOrWhiteSpace(entry.Original))
                .SelectMany(CreateTerms)
                .GroupBy(term => term.Normalized, StringComparer.Ordinal)
                .Select(group => group.OrderBy(term => term.IsPack).ThenByDescending(term => term.TokenCount).ThenByDescending(term => term.Normalized.Length).First())
                .OrderByDescending(term => term.TokenCount)
                .ThenByDescending(term => term.Normalized.Length)
                .ThenBy(term => term.IsPack)
                .ToArray();

        public string Apply(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText) || _terms.Length == 0) return rawText;
            var tokens = Tokenize(rawText);
            if (tokens.Count == 0) return rawText;
            var proposals = FindProposals(rawText, tokens);
            if (proposals.Count == 0) return rawText;
            proposals.Sort((left, right) =>
            {
                var byTokenCount = right.Term.TokenCount.CompareTo(left.Term.TokenCount);
                if (byTokenCount != 0) return byTokenCount;
                var byLength = right.Term.Normalized.Length.CompareTo(left.Term.Normalized.Length);
                if (byLength != 0) return byLength;
                var byManual = left.Term.IsPack.CompareTo(right.Term.IsPack);
                if (byManual != 0) return byManual;
                var byScore = right.Score.CompareTo(left.Score);
                if (byScore != 0) return byScore;
                return left.Start.CompareTo(right.Start);
            });
            var accepted = new List<Replacement>();
            foreach (var proposal in proposals)
            {
                if (accepted.Count >= MaxReplacements) break;
                if (accepted.Any(existing => existing.Start < proposal.End && proposal.Start < existing.End)) continue;
                accepted.Add(proposal);
            }
            if (accepted.Count == 0) return rawText;
            var builder = new System.Text.StringBuilder(rawText);
            foreach (var replacement in accepted.OrderByDescending(replacement => replacement.Start))
            {
                builder.Remove(replacement.Start, replacement.End - replacement.Start);
                builder.Insert(replacement.Start, replacement.ReplacementText);
            }
            return builder.ToString();
        }

        private List<Replacement> FindProposals(string rawText, List<(int Start, int End)> tokens)
        {
            var proposals = new List<Replacement>();
            for (var startIndex = 0; startIndex < tokens.Count; startIndex++)
            {
                var maxWindowLength = Math.Min(MaxWindowTokens, tokens.Count - startIndex);
                for (var windowLength = 1; windowLength <= maxWindowLength; windowLength++)
                {
                    var spanStart = tokens[startIndex].Start;
                    var rawSpan = rawText[spanStart..tokens[startIndex + windowLength - 1].End];
                    var start = 0;
                    var end = rawSpan.Length - 1;
                    while (start <= end && !char.IsLetterOrDigit(rawSpan[start])) start++;
                    while (end >= start && !char.IsLetterOrDigit(rawSpan[end])) end--;
                    if (end < start) continue;
                    var coreText = rawSpan.Substring(start, end - start + 1);
                    var normalizedWindow = Normalize(coreText);
                    if (string.IsNullOrEmpty(normalizedWindow)) continue;

                    var scored = new List<(Term Term, double Score)>();
                    foreach (var term in _terms)
                    {
                        if (!IsCompatibleWindow(term, normalizedWindow, windowLength)) continue;
                        if (string.Equals(coreText, term.OutputText, StringComparison.Ordinal)) continue;
                        var score = Score(term, normalizedWindow, windowLength);
                        if (score is null) continue;
                        scored.Add((term, score.Value));
                    }
                    if (scored.Count == 0) continue;
                    scored.Sort((left, right) =>
                    {
                        var byScore = right.Score.CompareTo(left.Score);
                        if (byScore != 0) return byScore;
                        var byTokenCount = right.Term.TokenCount.CompareTo(left.Term.TokenCount);
                        if (byTokenCount != 0) return byTokenCount;
                        var byLength = right.Term.Normalized.Length.CompareTo(left.Term.Normalized.Length);
                        if (byLength != 0) return byLength;
                        return left.Term.IsPack.CompareTo(right.Term.IsPack);
                    });
                    var best = scored[0];
                    var secondScore = scored.Count > 1 ? scored[1].Score : double.NegativeInfinity;
                    if (scored.Count > 1 && best.Score - secondScore < AmbiguityMargin) continue;
                    proposals.Add(new Replacement(spanStart + start, spanStart + end + 1, best.Term.OutputText, best.Score, best.Term));
                }
            }
            return proposals;
        }

        private static bool IsCompatibleWindow(Term term, string normalizedWindow, int windowTokenCount)
        {
            if (term.TokenCount > MaxWindowTokens) return false;
            if (Math.Abs(term.TokenCount - windowTokenCount) > 1) return false;
            var lengthDifference = Math.Abs(term.Normalized.Length - normalizedWindow.Length);
            if (term.TokenCount == 1) return lengthDifference <= 2;
            return lengthDifference <= Math.Max(3, term.Normalized.Length / 3);
        }

        private static double? Score(Term term, string normalizedWindow, int windowTokenCount)
        {
            var maxLength = Math.Max(term.Normalized.Length, normalizedWindow.Length);
            if (maxLength == 0) return null;
            var lengthDifference = Math.Abs(term.Normalized.Length - normalizedWindow.Length);
            var distance = Levenshtein(term.Normalized, normalizedWindow);
            var charSimilarity = 1d - (double)distance / maxLength;
            var sameFirst = term.First == normalizedWindow.FirstOrDefault(char.IsLetterOrDigit);
            var sameLast = term.Last == normalizedWindow.LastOrDefault(char.IsLetterOrDigit);
            if (term.TokenCount == 1)
            {
                if (!sameFirst || !sameLast) return null;
                if (lengthDifference > 2 || charSimilarity < 0.86d) return null;
            }
            else if (Math.Abs(term.TokenCount - windowTokenCount) > 1 || charSimilarity < 0.80d)
            {
                return null;
            }
            var score = charSimilarity;
            if (sameFirst) score += 0.02d;
            if (sameLast) score += 0.02d;
            if (term.TokenCount == windowTokenCount) score += 0.03d;
            if (lengthDifference >= 3) score -= 0.03d;
            return score;
        }

        private static IEnumerable<Term> CreateTerms(DictionaryEntry entry)
        {
            var outputText = string.IsNullOrWhiteSpace(entry.Replacement) ? entry.Original.Trim() : entry.Replacement.Trim();
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry.Original.Trim() };
            if (!string.IsNullOrWhiteSpace(entry.Replacement)) aliases.Add(entry.Replacement.Trim());
            var isPack = entry.Id.StartsWith("pack:", StringComparison.Ordinal);
            foreach (var alias in aliases)
            {
                var normalized = Normalize(alias);
                if (string.IsNullOrEmpty(normalized)) continue;
                var tokenCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if (tokenCount == 0) continue;
                yield return new Term(outputText, normalized, tokenCount, isPack,
                    normalized.FirstOrDefault(char.IsLetterOrDigit), normalized.LastOrDefault(char.IsLetterOrDigit));
            }
        }

        private static List<(int Start, int End)> Tokenize(string text)
        {
            var tokens = new List<(int, int)>();
            var index = 0;
            while (index < text.Length)
            {
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                if (index >= text.Length) break;
                var start = index;
                while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
                tokens.Add((start, index));
            }
            return tokens;
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var builder = new System.Text.StringBuilder();
            var pendingSpace = false;
            foreach (var ch in text.Normalize(System.Text.NormalizationForm.FormKD))
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (char.IsWhiteSpace(ch) || ch is '-' or '_' or '/') { pendingSpace = builder.Length > 0; continue; }
                if (char.IsLetterOrDigit(ch))
                {
                    if (pendingSpace && builder.Length > 0) builder.Append(' ');
                    builder.Append(char.ToLowerInvariant(ch));
                    pendingSpace = false;
                    continue;
                }
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(ch);
            }
            var collapsed = string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var start = 0;
            var end = collapsed.Length - 1;
            while (start <= end && !char.IsLetterOrDigit(collapsed[start])) start++;
            while (end >= start && !char.IsLetterOrDigit(collapsed[end])) end--;
            return end < start ? string.Empty : collapsed[start..(end + 1)];
        }

        private static int Levenshtein(string source, string target)
        {
            if (source.Length == 0) return target.Length;
            if (target.Length == 0) return source.Length;
            var previous = new int[target.Length + 1];
            var current = new int[target.Length + 1];
            for (var j = 0; j <= target.Length; j++) previous[j] = j;
            for (var i = 1; i <= source.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= target.Length; j++)
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (source[i - 1] == target[j - 1] ? 0 : 1));
                (previous, current) = (current, previous);
            }
            return previous[target.Length];
        }

        private sealed record Term(string OutputText, string Normalized, int TokenCount, bool IsPack, char First, char Last);
        private sealed record Replacement(int Start, int End, string ReplacementText, double Score, Term Term);
    }

    private sealed class FakeDictionaryService : IDictionaryService
    {
        public FakeDictionaryService(IEnumerable<DictionaryEntry> entries)
        {
            Entries = entries.ToArray();
        }

        public IReadOnlyList<DictionaryEntry> Entries { get; private set; }
        public event Action? EntriesChanged;

        public void AddEntry(DictionaryEntry entry) => throw new NotSupportedException();
        public void AddEntries(IEnumerable<DictionaryEntry> entries) => throw new NotSupportedException();
        public void UpdateEntry(DictionaryEntry entry) => throw new NotSupportedException();
        public void DeleteEntry(string id) => throw new NotSupportedException();
        public void DeleteEntries(IEnumerable<string> ids) => throw new NotSupportedException();
        public string ApplyCorrections(string text) => text;
        public string? GetTermsForPrompt() => null;
        public void LearnCorrection(string original, string replacement) => throw new NotSupportedException();
        public IReadOnlyList<LearnedDictionaryCorrection> LearnCorrections(IEnumerable<CorrectionSuggestion> suggestions) => throw new NotSupportedException();
        public void UndoLearnedCorrections(IEnumerable<LearnedDictionaryCorrection> learnedCorrections) => throw new NotSupportedException();
        public void ActivatePack(TermPack pack) => throw new NotSupportedException();
        public void DeactivatePack(string packId) => throw new NotSupportedException();

        public void SetEntries(params DictionaryEntry[] entries)
        {
            Entries = entries;
            EntriesChanged?.Invoke();
        }
    }
}
