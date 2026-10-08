using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Sync;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Provides dictionary service behavior.
/// </summary>
public sealed class DictionaryService : IDictionaryService
{
    private const string PackEntryPrefix = "pack:";
    private static readonly TimeSpan CorrectionRegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly string _filePath;
    private List<DictionaryEntry> _cache = [];
    private bool _cacheLoaded;

    /// <summary>
    /// Gets the configured dictionary entries.
    /// </summary>
    /// <summary>
    /// Gets the configured dictionary entries.
    /// </summary>
    public IReadOnlyList<DictionaryEntry> Entries
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    /// <summary>
    /// Raised when entries changes.
    /// </summary>
    public event Action? EntriesChanged;

    /// <summary>
    /// Initializes a new instance of the DictionaryService class.
    /// </summary>
    public DictionaryService(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Gets why the dictionary file could not be read or parsed, or null when it loaded or does not exist yet.
    /// </summary>
    /// <remarks>
    /// While set, <see cref="Entries"/> is empty and every mutation is refused, so a locked or corrupt file is
    /// never replaced by that empty list. <see cref="Reload"/> clears it once the file can be read again.
    /// </remarks>
    public Exception? LoadError { get; private set; }

    /// <summary>
    /// Gets why the most recent mutation was not written to disk, or null when it was persisted.
    /// </summary>
    public Exception? LastSaveError { get; private set; }

    /// <summary>
    /// Reads the dictionary file again, for example after a sync client released it, and reports whether it loaded.
    /// </summary>
    public bool Reload()
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        _cacheLoaded = false;
        EnsureCacheLoaded();
        if (LoadError is not null) return false;

        NotifyEntriesChanged();
        return true;
    }

    /// <summary>
    /// Adds a dictionary entry and persists the updated dictionary.
    /// </summary>
    public void AddEntry(DictionaryEntry entry)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        _cache.Add(BackfillTimestamps(entry));
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Adds entries.
    /// </summary>
    public void AddEntries(IEnumerable<DictionaryEntry> entries)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        _cache.AddRange(entries.Select(BackfillTimestamps));
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Updates entry.
    /// </summary>
    public void UpdateEntry(DictionaryEntry entry)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        var idx = _cache.FindIndex(e => e.Id == entry.Id);
        if (idx >= 0)
        {
            var existing = _cache[idx];
            _cache[idx] = BackfillTimestamps(entry) with { UpdatedAt = NextUpdatedAt(existing.UpdatedAt) };
        }
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Deletes entry.
    /// </summary>
    public void DeleteEntry(string id)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        _cache.RemoveAll(e => e.Id == id);
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Deletes entries.
    /// </summary>
    public void DeleteEntries(IEnumerable<string> ids)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        var idSet = ids.ToHashSet();
        _cache.RemoveAll(e => idSet.Contains(e.Id));
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Applies corrections.
    /// </summary>
    public string ApplyCorrections(string text)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();
        return new CorrectionSet(_cache).Apply(text, IncrementUsageCount);
    }

    /// <summary>Applies a recording's dictionary snapshot without modifying persisted entries or usage counters.</summary>
    public static string ApplyCorrectionsSnapshot(string text, IReadOnlyList<DictionaryEntry> entries) =>
        new CorrectionSet(entries).Apply(text);

    /// <summary>Compiles the enabled corrections of a fixed snapshot once, so applying them later builds no regexes.</summary>
    public static CorrectionSet CompileCorrections(IReadOnlyList<DictionaryEntry> entries) => new(entries);

    /// <summary>Enabled corrections of an entry snapshot, longest original first.</summary>
    public sealed class CorrectionSet
    {
        private readonly (string Id, Regex Regex, string Replacement)[] _rules;

        internal CorrectionSet(IReadOnlyList<DictionaryEntry> entries)
        {
            var rules = new List<(string, Regex, string)>();
            foreach (var entry in entries
                .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Correction && e.Replacement is not null)
                .OrderByDescending(e => e.Original.Length))
            {
                var replacement = ExpandReplacementEscapes(entry.Replacement!);
                var pattern = entry.IsRegex ? entry.Original : BuildCorrectionPattern(entry.Original);
                // ASR often punctuates a spoken layout command ("Hello new line. Next").
                // That mark belongs to the command, not to the start of the new line.
                // Preserve regex semantics and punctuation around ordinary corrections.
                if (!entry.IsRegex && replacement.Any(c => c is '\r' or '\n') && replacement.All(char.IsWhiteSpace))
                    pattern += @"(?:[ \t]*[.,;:!?][ \t]*)?";
                var options = entry.CaseSensitive
                    ? RegexOptions.CultureInvariant
                    : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
                try
                {
                    rules.Add((entry.Id, new Regex(pattern, options, CorrectionRegexTimeout), replacement));
                }
                catch (ArgumentException) when (entry.IsRegex)
                {
                    // Invalid persisted regex entries must not break post-processing.
                }
            }
            _rules = [.. rules];
        }

        /// <summary>Applies every correction in order to <paramref name="text"/>.</summary>
        public string Apply(string text) => Apply(text, null);

        internal string Apply(string text, Action<string>? onMatch)
        {
            foreach (var (id, regex, replacement) in _rules)
            {
                try
                {
                    if (!regex.IsMatch(text))
                        continue;

                    text = regex.Replace(text, _ => replacement);
                    onMatch?.Invoke(id);
                }
                catch (RegexMatchTimeoutException)
                {
                    // A pathological regex must not block dictation post-processing.
                }
            }

            return text;
        }
    }

    private static string ExpandReplacementEscapes(string replacement)
    {
        if (!replacement.Contains('\\', StringComparison.Ordinal))
            return replacement;

        var builder = new StringBuilder(replacement.Length);
        for (var index = 0; index < replacement.Length; index++)
        {
            var character = replacement[index];
            if (character != '\\' || index + 1 >= replacement.Length)
            {
                builder.Append(character);
                continue;
            }

            var escaped = replacement[index + 1];
            switch (escaped)
            {
                case 's':
                    builder.Append(' ');
                    index++;
                    break;
                case 'n':
                    builder.Append('\n');
                    index++;
                    break;
                case 'r':
                    builder.Append('\r');
                    index++;
                    break;
                case 't':
                    builder.Append('\t');
                    index++;
                    break;
                case '\\':
                    builder.Append('\\');
                    index++;
                    break;
                default:
                    builder.Append('\\');
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns terms for prompt.
    /// </summary>
    public string? GetTermsForPrompt()
    {
        EnsureCacheLoaded();
        var terms = GetEnabledTerms();

        if (terms.Count == 0) return null;
        return string.Join(", ", terms);
    }

    /// <summary>
    /// Returns enabled terms.
    /// </summary>
    public IReadOnlyList<string> GetEnabledTerms()
    {
        EnsureCacheLoaded();
        return NormalizeTerms(_cache
            .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Term)
            .Select(e => e.Original));
    }

    /// <summary>
    /// Returns enabled corrections.
    /// </summary>
    public IReadOnlyList<DictionaryEntry> GetEnabledCorrections()
    {
        EnsureCacheLoaded();
        return _cache
            .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Correction)
            .OrderBy(e => e.Original, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Sets terms.
    /// </summary>
    public void SetTerms(IEnumerable<string> terms, bool replaceExisting)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;

        var normalized = NormalizeTerms(terms);
        var desiredByKey = normalized.ToDictionary(TermKey, term => term);
        var existingTerms = _cache.Where(e => e.EntryType == DictionaryEntryType.Term).ToList();
        var touchedTerms = new List<DictionaryEntry>();
        var untouchedTerms = new List<DictionaryEntry>();

        foreach (var entry in existingTerms)
        {
            var key = TermKey(entry.Original);
            if (desiredByKey.TryGetValue(key, out var desiredTerm))
            {
                var idx = _cache.FindIndex(e => e.Id == entry.Id);
                if (idx >= 0)
                {
                    var updated = entry with
                    {
                        Original = desiredTerm,
                        IsEnabled = true,
                        UpdatedAt = NextUpdatedAt(entry.UpdatedAt)
                    };
                    _cache[idx] = updated;
                    touchedTerms.Add(updated);
                }
            }
            else if (replaceExisting)
            {
                _cache.Remove(entry);
            }
            else
            {
                untouchedTerms.Add(entry);
            }
        }

        var existingKeys = existingTerms.Select(e => TermKey(e.Original)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var addedTerms = normalized
            .Where(term => !existingKeys.Contains(TermKey(term)))
            .Select(term => new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Term,
                Original = term,
                CreatedAt = now,
                UpdatedAt = now
            })
            .ToList();
        _cache.AddRange(addedTerms);

        if (replaceExisting)
        {
            ReorderTerms(
                normalized
                    .Select(term => _cache.First(e =>
                        e.EntryType == DictionaryEntryType.Term &&
                        TermKey(e.Original) == TermKey(term)))
                    .ToList());
        }
        else if (touchedTerms.Count > 0)
        {
            ReorderTerms([.. touchedTerms, .. addedTerms, .. untouchedTerms]);
        }

        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Removes all terms.
    /// </summary>
    public void RemoveAllTerms()
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        _cache.RemoveAll(e => e.EntryType == DictionaryEntryType.Term);
        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Deletes term.
    /// </summary>
    public bool DeleteTerm(string term)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return false;
        var key = TermKey(term);
        var removed = _cache.RemoveAll(e =>
            e.EntryType == DictionaryEntryType.Term &&
            TermKey(e.Original) == key);

        if (removed == 0)
            return false;

        return TryCommitMutation(rollback);
    }

    /// <summary>
    /// Upserts correction.
    /// </summary>
    public void UpsertCorrection(string original, string replacement, bool caseSensitive)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;
        var existing = _cache.FirstOrDefault(e =>
            e.EntryType == DictionaryEntryType.Correction &&
            e.Original.Equals(original, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            var idx = _cache.FindIndex(e => e.Id == existing.Id);
            if (idx >= 0)
            {
                _cache[idx] = existing with
                {
                    Original = original,
                    Replacement = replacement,
                    CaseSensitive = caseSensitive,
                    IsEnabled = true,
                    UpdatedAt = NextUpdatedAt(existing.UpdatedAt)
                };
            }
        }
        else
        {
            var now = DateTime.UtcNow;
            _cache.Add(new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Correction,
                Original = original,
                Replacement = replacement,
                CaseSensitive = caseSensitive,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        TryCommitMutation(rollback);
    }

    /// <summary>
    /// Deletes correction.
    /// </summary>
    public bool DeleteCorrection(string original)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return false;
        var removed = _cache.RemoveAll(e =>
            e.EntryType == DictionaryEntryType.Correction &&
            e.Original.Equals(original, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
            return false;

        return TryCommitMutation(rollback);
    }

    /// <summary>
    /// Learns correction.
    /// </summary>
    public void LearnCorrection(string original, string replacement)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        EnsureCacheLoaded();

        var existing = _cache.FirstOrDefault(e =>
            e.EntryType == DictionaryEntryType.Correction &&
            e.Original.Equals(original, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            UpdateEntry(existing with { Replacement = replacement, UsageCount = existing.UsageCount + 1 });
        }
        else
        {
            var now = DateTime.UtcNow;
            AddEntry(new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Correction,
                Original = original,
                Replacement = replacement,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
    }

    /// <summary>
    /// Learns new corrections without overwriting existing dictionary entries.
    /// </summary>
    public IReadOnlyList<LearnedDictionaryCorrection> LearnCorrections(IEnumerable<CorrectionSuggestion> suggestions)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return [];

        var learned = new List<LearnedDictionaryCorrection>();
        var existingOriginals = _cache
            .Where(e => e.EntryType == DictionaryEntryType.Correction)
            .Select(e => e.Original)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenOriginals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var suggestion in suggestions)
        {
            var original = suggestion.Original.Trim();
            var replacement = suggestion.Replacement.Trim();
            if (original.Length == 0 ||
                replacement.Length == 0 ||
                string.Equals(original, replacement, StringComparison.OrdinalIgnoreCase) ||
                !IsSafeAutomaticallyLearnedToken(original) ||
                !IsSafeAutomaticallyLearnedToken(replacement) ||
                existingOriginals.Contains(original) ||
                !seenOriginals.Add(original))
            {
                continue;
            }

            var now = DateTime.UtcNow;
            var entry = new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Correction,
                Original = original,
                Replacement = replacement,
                Source = DictionaryEntrySource.AutoLearned,
                CreatedAt = now,
                UpdatedAt = now
            };
            _cache.Add(entry);
            existingOriginals.Add(original);
            learned.Add(new LearnedDictionaryCorrection(entry.Id, entry.Original, entry.Replacement!));
        }

        // Only corrections that reached disk count as learned; the caller offers to undo exactly these.
        if (learned.Count > 0 && !TryCommitMutation(rollback))
            return [];

        return learned;
    }

    private void NotifyEntriesChanged()
    {
        var handlers = EntriesChanged;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler).Invoke();
            }
            catch (Exception ex) when (IsNonFatalException(ex))
            {
                // Subscriber failures must not break dictionary persistence or automatic learning.
                LogDictionaryFailure("Dictionary entries changed subscriber failed", ex);
            }
        }
    }

    private static bool IsSafeAutomaticallyLearnedToken(string token)
    {
        if (token.Length == 0 ||
            !char.IsLetterOrDigit(token[0]) ||
            !char.IsLetterOrDigit(token[^1]))
        {
            return false;
        }

        return token.All(static c =>
            char.IsLetterOrDigit(c) ||
            c == '-' ||
            c == '\'');
    }

    /// <summary>
    /// Removes corrections that were created by automatic learning.
    /// </summary>
    public void UndoLearnedCorrections(IEnumerable<LearnedDictionaryCorrection> learnedCorrections)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;

        var learnedIds = learnedCorrections
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (learnedIds.Count == 0)
            return;

        var removed = _cache.RemoveAll(entry =>
            entry.EntryType == DictionaryEntryType.Correction &&
            learnedIds.Contains(entry.Id));

        if (removed > 0)
            TryCommitMutation(rollback);
    }

    /// <summary>
    /// Activates pack.
    /// </summary>
    public void ActivatePack(TermPack pack)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;

        var existingOriginals = _cache
            .Where(e => e.EntryType == DictionaryEntryType.Term)
            .Select(e => e.Original)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newEntries = pack.Terms
            .Where(t => !existingOriginals.Contains(t))
            .Select(t =>
            {
                var now = DateTime.UtcNow;
                return new DictionaryEntry
                {
                    Id = $"{PackEntryPrefix}{pack.Id}:{t}",
                    EntryType = DictionaryEntryType.Term,
                    Original = t,
                    CreatedAt = now,
                    UpdatedAt = now
                };
            })
            .ToList();

        if (newEntries.Count > 0)
        {
            _cache.AddRange(newEntries);
            TryCommitMutation(rollback);
        }
    }

    /// <summary>
    /// Deactivates pack.
    /// </summary>
    public void DeactivatePack(string packId)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;

        var prefix = $"pack:{packId}:";
        var removed = _cache.RemoveAll(e => e.Id.StartsWith(prefix, StringComparison.Ordinal));

        if (removed > 0)
            TryCommitMutation(rollback);
    }

    private void IncrementUsageCount(string id)
    {
        var idx = _cache.FindIndex(e => e.Id == id);
        if (idx >= 0)
        {
            _cache[idx] = _cache[idx] with { UsageCount = _cache[idx].UsageCount + 1 };
            // Usage counters are best effort: a failed write is recorded and retried with the next save.
            _ = SaveToDisk(_cache);
        }
    }

    /// <summary>
    /// Returns user data sync entries.
    /// </summary>
    public IReadOnlyList<UserDataSyncDictionaryEntry> GetUserDataSyncEntries()
    {
        EnsureCacheLoaded();
        return _cache
            .Where(IsUserAuthored)
            .Select(entry => new UserDataSyncDictionaryEntry(
                entry.EntryType == DictionaryEntryType.Term
                    ? UserDataSyncDictionaryEntryType.Term
                    : UserDataSyncDictionaryEntryType.Correction,
                entry.Original,
                entry.EntryType == DictionaryEntryType.Correction ? entry.Replacement ?? string.Empty : null,
                entry.CaseSensitive,
                entry.IsEnabled,
                entry.CreatedAt,
                entry.UpdatedAt,
                entry.Source,
                entry.IsRegex, entry.CtcMinSimilarity))
            .ToList();
    }

    /// <summary>
    /// Applies user data sync mutations.
    /// </summary>
    public void ApplyUserDataSyncMutations(IReadOnlyList<UserDataSyncMutation> mutations)
    {
        using var profileMutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out var rollback)) return;

        var changed = false;
        foreach (var mutation in mutations)
        {
            switch (mutation)
            {
                case UserDataSyncMutation.UpsertDictionary upsert:
                    changed |= UpsertSyncedDictionaryEntry(upsert.Entry);
                    break;
                case UserDataSyncMutation.DeleteDictionary delete:
                    changed |= DeleteSyncedDictionaryEntry(delete.ItemId);
                    break;
            }
        }

        if (!changed)
            return;

        TryCommitMutation(rollback);
    }

    private bool UpsertSyncedDictionaryEntry(UserDataSyncDictionaryEntry synced)
    {
        var targetType = synced.EntryType == UserDataSyncDictionaryEntryType.Term
            ? DictionaryEntryType.Term
            : DictionaryEntryType.Correction;
        var targetId = UserDataSyncIdentity.DictionaryItemId(synced.EntryType, synced.Original);
        var replacement = targetType == DictionaryEntryType.Correction ? synced.Replacement ?? string.Empty : null;

        var idx = _cache.FindIndex(entry =>
            entry.EntryType == targetType &&
            IsUserAuthored(entry) &&
            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original) == targetId);

        if (idx >= 0)
        {
            var existing = _cache[idx];
            _cache[idx] = existing with
            {
                Original = synced.Original,
                Replacement = replacement,
                CaseSensitive = synced.CaseSensitive,
                IsRegex = synced.IsRegex,
            CtcMinSimilarity = synced.CtcMinSimilarity,
                IsEnabled = synced.IsEnabled,
                Source = synced.Source,
                UpdatedAt = synced.UpdatedAt
            };
            return true;
        }

        _cache.Add(new DictionaryEntry
        {
            Id = Guid.NewGuid().ToString(),
            EntryType = targetType,
            Original = synced.Original,
            Replacement = replacement,
            CaseSensitive = synced.CaseSensitive,
            IsRegex = synced.IsRegex,
            CtcMinSimilarity = synced.CtcMinSimilarity,
            IsEnabled = synced.IsEnabled,
            Source = synced.Source,
            CreatedAt = synced.CreatedAt,
            UpdatedAt = synced.UpdatedAt
        });
        return true;
    }

    private bool DeleteSyncedDictionaryEntry(string itemId)
    {
        var removed = _cache.RemoveAll(entry =>
            IsUserAuthored(entry) &&
            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original) == itemId);
        return removed > 0;
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        LoadError = null;
        try
        {
            var json = File.ReadAllText(_filePath);
            // A zero-length file holds nothing to protect, so it counts as an empty catalog like a missing one.
            _cache = string.IsNullOrWhiteSpace(json)
                ? []
                : JsonSerializer.Deserialize<List<DictionaryEntry>>(json) ?? [];
            _cache = _cache.Select(BackfillTimestamps).ToList();
        }
        catch (FileNotFoundException) { _cache = []; }
        catch (DirectoryNotFoundException) { _cache = []; }
        catch (Exception ex) when (IsNonFatalException(ex))
        {
            RecordLoadFailure(ex);
        }

        _cacheLoaded = true;
    }

    // Mutations are refused rather than thrown while the file is unreadable: dictation, automatic learning and sync
    // reach them, and an exception there would abort the dictation or the sync cycle. The cache is empty in that
    // state, so writing it would replace the user's dictionary with an almost empty list. Callers can inspect
    // LoadError and LastSaveError.
    private bool TryBeginMutation(out List<DictionaryEntry> rollback)
    {
        EnsureCacheLoaded();
        if (LoadError is null)
        {
            rollback = _cache.ToList();
            return true;
        }

        LastSaveError = new InvalidOperationException(
            "The dictionary file could not be loaded, so changes are not saved until it loads again.", LoadError);
        rollback = [];
        return false;
    }

    // A failed write restores the previous list and raises no event, so the cache keeps matching the file on disk.
    private bool TryCommitMutation(List<DictionaryEntry> rollback)
    {
        if (SaveToDisk(_cache))
        {
            NotifyEntriesChanged();
            return true;
        }

        _cache = rollback;
        return false;
    }

    /// <inheritdoc />
    public bool TryReplaceAll(IReadOnlyList<DictionaryEntry> entries)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        if (!TryBeginMutation(out _)) return false;
        var replacement = entries.Select(BackfillTimestamps).ToList();
        if (!SaveToDisk(replacement))
            return false;

        _cache = replacement;
        NotifyEntriesChanged();
        return true;
    }

    private bool SaveToDisk(IReadOnlyList<DictionaryEntry> entries)
    {
        Exception? error;
        try
        {
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            if (AtomicFileWriter.TryWriteAllText(_filePath, json, out error))
            {
                LastSaveError = null;
                return true;
            }
        }
        catch (Exception ex) when (IsNonFatalException(ex))
        {
            error = ex;
        }

        LastSaveError = error ?? new IOException("The dictionary file could not be replaced atomically.");
        LogDictionaryFailure("Saving dictionary failed", LastSaveError);
        return false;
    }

    private void RecordLoadFailure(Exception ex)
    {
        // The file exists but cannot be trusted; keep it off limits instead of treating it as empty.
        LogDictionaryFailure("Loading dictionary failed", ex);
        LoadError = ex;
        _cache = [];
    }

    private static bool IsNonFatalException(Exception ex) =>
        ex is not OutOfMemoryException
            and not AccessViolationException
            and not AppDomainUnloadedException
            and not BadImageFormatException
            and not CannotUnloadAppDomainException
            and not InvalidProgramException;

    private static void LogDictionaryFailure(string message, Exception ex) =>
        Debug.WriteLine($"[DictionaryService] {message}: {ex.Message}");

    private void ReorderTerms(IReadOnlyList<DictionaryEntry> orderedTerms)
    {
        var orderedIds = orderedTerms.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var nonTerms = _cache.Where(e => e.EntryType != DictionaryEntryType.Term).ToList();
        var remainingTerms = _cache
            .Where(e => e.EntryType == DictionaryEntryType.Term && !orderedIds.Contains(e.Id))
            .ToList();

        _cache = [.. nonTerms, .. orderedTerms, .. remainingTerms];
    }

    private static IReadOnlyList<string> NormalizeTerms(IEnumerable<string> terms)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();

        foreach (var rawTerm in terms)
        {
            var term = rawTerm.Trim();
            if (term.Length == 0)
                continue;

            if (seen.Add(TermKey(term)))
                normalized.Add(term);
        }

        return normalized;
    }

    private static string TermKey(string term) => term.Trim().ToUpperInvariant();

    private static bool IsUserAuthored(DictionaryEntry entry) =>
        !entry.Id.StartsWith(PackEntryPrefix, StringComparison.Ordinal);

    private static string BuildCorrectionPattern(string original)
    {
        var pattern = Regex.Escape(original);
        return ContainsScriptWithoutWhitespaceBoundaries(original) ? pattern : @"\b" + pattern + @"\b";
    }

    private static bool ContainsScriptWithoutWhitespaceBoundaries(string text) =>
        text.Any(IsScriptWithoutWhitespaceBoundaries);

    private static bool IsScriptWithoutWhitespaceBoundaries(char ch) =>
        ch is >= '\u3040' and <= '\u30FF' // Hiragana and Katakana
            or >= '\u3400' and <= '\u9FFF' // CJK ideographs
            or >= '\uAC00' and <= '\uD7AF' // Hangul syllables
            or >= '\uF900' and <= '\uFAFF'; // CJK Compatibility Ideographs

    private static DictionaryEntry BackfillTimestamps(DictionaryEntry entry)
    {
        var createdAt = entry.CreatedAt == default ? DateTime.UtcNow : NormalizeUtc(entry.CreatedAt);
        var updatedAt = entry.UpdatedAt == default ? createdAt : NormalizeUtc(entry.UpdatedAt);
        return entry with { CreatedAt = createdAt, UpdatedAt = updatedAt };
    }

    private static DateTime NextUpdatedAt(DateTime previousUpdatedAt)
    {
        var previous = previousUpdatedAt == default ? DateTime.UtcNow : NormalizeUtc(previousUpdatedAt);
        var now = DateTime.UtcNow;
        return now > previous ? now : previous.AddTicks(1);
    }

    private static DateTime NormalizeUtc(DateTime date) =>
        date.Kind == DateTimeKind.Utc ? date : date.ToUniversalTime();
}
