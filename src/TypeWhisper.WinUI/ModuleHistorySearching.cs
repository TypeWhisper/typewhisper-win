using System.Collections.Frozen;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.ModuleKit;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// IHistorySearching over the History search index. Reads run off the UI thread and return only records History still
// has, so text a user deleted is never returned while the indexer is still removing it.
internal sealed class ModuleHistorySearching : IHistorySearching
{
    private readonly HistorySearchIndexer _indexer;
    private readonly HistorySearchIndex _index;
    private readonly IHistoryService _history;

    internal ModuleHistorySearching(HistorySearchIndexer indexer, HistorySearchIndex index, IHistoryService history)
    {
        _indexer = indexer;
        _index = index;
        _history = history;
    }

    public Task PrepareAsync(CancellationToken cancellationToken = default) => _indexer.PrepareAsync(cancellationToken);

    public Task DisableAsync(CancellationToken cancellationToken = default) => _indexer.DisableAsync(cancellationToken);

    public async Task<IReadOnlyList<ModuleHistorySearchHit>> SearchAsync(ModuleHistorySearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Words);
        if (!_indexer.IsEnabled || query.Limit <= 0) return [];
        return await ReadAsync(current => _index.Search(query.Words, query.Names ?? [], Scope(query.Scope), query.Limit, query.IncludeNewest)
            .Where(hit => current.Contains(hit.RecordId)).Select(ToModuleHit).ToArray(), [], cancellationToken);
    }

    public async Task<ModuleHistoryNewestRecords> NewestRecordsAsync(ModuleHistorySearchScope scope, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var empty = new ModuleHistoryNewestRecords([], 0);
        if (!_indexer.IsEnabled) return empty;
        return await ReadAsync(current =>
        {
            var (hits, total) = _index.NewestRecords(Scope(scope), Math.Max(0, limit));
            var kept = hits.Where(hit => current.Contains(hit.RecordId)).Select(ToModuleHit).ToArray();
            // Records removed since the indexer's last step leave the count as far as this read sees them.
            return new ModuleHistoryNewestRecords(kept, Math.Max(kept.Length, total - (hits.Count - kept.Length)));
        }, empty, cancellationToken);
    }

    public async Task<ModuleHistoryTranscript?> TranscriptAsync(string recordId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordId);
        if (!_indexer.IsEnabled) return null;
        return await ReadAsync(current => current.Contains(recordId) && _index.Transcript(recordId) is { } transcript
            ? new ModuleHistoryTranscript(transcript.Passages.Select(ToModulePassage).ToArray(), transcript.ContentHash) : null,
            null, cancellationToken);
    }

    public async Task<ModuleHistorySummary?> SummaryAsync(string recordId, string language, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordId);
        if (!_indexer.IsEnabled) return null;
        return await ReadAsync(current => current.Contains(recordId) && _index.Summary(recordId, language ?? "") is { } summary
            ? new ModuleHistorySummary(summary.Text, summary.ProviderId) : null, null, cancellationToken);
    }

    public async Task SetSummaryAsync(string recordId, string summary, string providerId, string contentHash, string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordId);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(contentHash);
        if (!_indexer.IsEnabled) return;
        await ReadAsync(current =>
        {
            if (current.Contains(recordId)) _index.SetSummary(recordId, summary, providerId, contentHash, language ?? "");
            return true;
        }, false, cancellationToken);
    }

    public async Task<ModuleHistoryVocabulary> VocabularyAsync(CancellationToken cancellationToken = default)
    {
        if (!_indexer.IsEnabled) return ModuleHistoryVocabulary.Empty;
        return await ReadAsync(_ =>
        {
            var vocabulary = _index.Vocabulary();
            return new ModuleHistoryVocabulary(vocabulary.Speakers, vocabulary.Apps.Select(app => new ModuleHistoryApp(app.Name, app.ProcessName)).ToArray(),
                vocabulary.Domains);
        }, ModuleHistoryVocabulary.Empty, cancellationToken);
    }

    // Runs a read with the identifiers of the records History has now, off the UI thread. Without History the read
    // finds nothing, because the index could hold records that History no longer has.
    private async Task<T> ReadAsync<T>(Func<IReadOnlySet<string>, T> read, T empty, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { await _history.EnsureLoadedAsync().WaitAsync(cancellationToken); }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { return empty; }
        return await Task.Run(() =>
        {
            var current = _history.Records.Where(HistorySearchRecord.Indexes).Select(record => record.Id).ToFrozenSet(StringComparer.Ordinal);
            cancellationToken.ThrowIfCancellationRequested();
            try { return read(current); }
            catch (HistorySearchIndexException ex) { throw new InvalidOperationException("The History search index could not be read.", ex); }
        }, cancellationToken);
    }

    internal static HistorySearchScope Scope(ModuleHistorySearchScope scope) => new(scope.Period?.Start, scope.Period?.End)
    {
        Sources = scope.Sources ?? new HashSet<string>(),
        AppProcessNames = scope.AppProcessNames ?? new HashSet<string>(),
        Domains = scope.Domains ?? new HashSet<string>(),
        RecordIds = scope.RecordIds
    };

    internal static ModuleHistoryPassage ToModulePassage(HistoryPassage passage) =>
        new(passage.Kind == HistoryPassageKind.Result ? ModuleHistoryPassageKind.Result : ModuleHistoryPassageKind.Transcript, passage.Text)
        {
            Speakers = passage.Speakers,
            StartSeconds = passage.StartSeconds,
            EndSeconds = passage.EndSeconds,
            TextSpan = passage.TextStart is { } start && passage.TextEnd is { } end ? new ModuleTextSpan(start, end) : null
        };

    internal static ModuleHistorySearchHit ToModuleHit(HistorySearchHit hit) =>
        new(hit.RecordId, hit.RecordedAt, hit.Source, hit.FirstOrdinal, hit.LastOrdinal, ToModulePassage(hit.Passage))
        {
            Title = hit.Title,
            AppName = hit.AppName,
            Domain = hit.Domain,
            Score = hit.Score
        };
}
