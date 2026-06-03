using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.Sync;

public sealed class TypeWhisperUserDataSyncStore : IUserDataSyncStore
{
    private readonly IDictionaryService _dictionary;
    private readonly ISnippetService _snippets;
    private readonly Dictionary<Guid, Action> _observers = [];
    private bool _isApplyingRemoteChanges;

    public TypeWhisperUserDataSyncStore(IDictionaryService dictionary, ISnippetService snippets)
    {
        _dictionary = dictionary;
        _snippets = snippets;
        _dictionary.EntriesChanged += NotifyLocalChange;
        _snippets.SnippetsChanged += NotifyLocalChange;
    }

    public UserDataSyncSnapshot Snapshot()
    {
        var dictionaryEntries = _dictionary is DictionaryService concreteDictionary
            ? concreteDictionary.GetUserDataSyncEntries()
            : FallbackDictionarySnapshot();
        var snippets = _snippets is SnippetService concreteSnippets
            ? concreteSnippets.GetUserDataSyncSnippets()
            : FallbackSnippetSnapshot();

        return new UserDataSyncSnapshot(dictionaryEntries, snippets);
    }

    public void Apply(IReadOnlyList<UserDataSyncMutation> mutations)
    {
        if (mutations.Count == 0)
            return;

        _isApplyingRemoteChanges = true;
        try
        {
            if (_dictionary is DictionaryService concreteDictionary)
                concreteDictionary.ApplyUserDataSyncMutations(mutations);
            else
                ApplyDictionaryFallback(mutations);

            if (_snippets is SnippetService concreteSnippets)
                concreteSnippets.ApplyUserDataSyncMutations(mutations);
            else
                ApplySnippetFallback(mutations);
        }
        finally
        {
            _isApplyingRemoteChanges = false;
        }
    }

    public Guid ObserveLocalChanges(Action handler)
    {
        var id = Guid.NewGuid();
        _observers[id] = handler;
        return id;
    }

    public void RemoveLocalChangeObserver(Guid id)
    {
        _observers.Remove(id);
    }

    private IReadOnlyList<UserDataSyncDictionaryEntry> FallbackDictionarySnapshot() =>
        _dictionary.Entries
            .Where(entry => !entry.Id.StartsWith("pack:", StringComparison.Ordinal))
            .Select(entry => new UserDataSyncDictionaryEntry(
                entry.EntryType == DictionaryEntryType.Term
                    ? UserDataSyncDictionaryEntryType.Term
                    : UserDataSyncDictionaryEntryType.Correction,
                entry.Original,
                entry.EntryType == DictionaryEntryType.Correction ? entry.Replacement ?? string.Empty : null,
                entry.CaseSensitive,
                entry.IsEnabled,
                entry.CreatedAt,
                entry.UpdatedAt))
            .ToList();

    private IReadOnlyList<UserDataSyncSnippet> FallbackSnippetSnapshot() =>
        _snippets.Snippets
            .Select(snippet => new UserDataSyncSnippet(
                snippet.Trigger,
                snippet.Replacement,
                snippet.CaseSensitive,
                snippet.IsEnabled,
                snippet.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                snippet.CreatedAt,
                snippet.UpdatedAt))
            .ToList();

    private void ApplyDictionaryFallback(IReadOnlyList<UserDataSyncMutation> mutations)
    {
        foreach (var mutation in mutations)
        {
            switch (mutation)
            {
                case UserDataSyncMutation.UpsertDictionary upsert:
                    UpsertDictionaryFallback(upsert.Entry);
                    break;
                case UserDataSyncMutation.DeleteDictionary delete:
                    DeleteDictionaryFallback(delete.ItemId);
                    break;
            }
        }
    }

    private void UpsertDictionaryFallback(UserDataSyncDictionaryEntry synced)
    {
        var targetId = UserDataSyncIdentity.DictionaryItemId(synced.EntryType, synced.Original);
        var targetType = synced.EntryType == UserDataSyncDictionaryEntryType.Term
            ? DictionaryEntryType.Term
            : DictionaryEntryType.Correction;
        var existing = _dictionary.Entries.FirstOrDefault(entry =>
            entry.EntryType == targetType &&
            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original) == targetId);
        var replacement = targetType == DictionaryEntryType.Correction ? synced.Replacement ?? string.Empty : null;

        if (existing is not null)
        {
            _dictionary.UpdateEntry(existing with
            {
                Original = synced.Original,
                Replacement = replacement,
                CaseSensitive = synced.CaseSensitive,
                IsEnabled = synced.IsEnabled,
                UpdatedAt = synced.UpdatedAt
            });
            return;
        }

        _dictionary.AddEntry(new DictionaryEntry
        {
            Id = Guid.NewGuid().ToString(),
            EntryType = targetType,
            Original = synced.Original,
            Replacement = replacement,
            CaseSensitive = synced.CaseSensitive,
            IsEnabled = synced.IsEnabled,
            CreatedAt = synced.CreatedAt,
            UpdatedAt = synced.UpdatedAt
        });
    }

    private void DeleteDictionaryFallback(string itemId)
    {
        var entry = _dictionary.Entries.FirstOrDefault(entry =>
            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original) == itemId);
        if (entry is not null)
            _dictionary.DeleteEntry(entry.Id);
    }

    private void ApplySnippetFallback(IReadOnlyList<UserDataSyncMutation> mutations)
    {
        foreach (var mutation in mutations)
        {
            switch (mutation)
            {
                case UserDataSyncMutation.UpsertSnippet upsert:
                    UpsertSnippetFallback(upsert.Snippet);
                    break;
                case UserDataSyncMutation.DeleteSnippet delete:
                    DeleteSnippetFallback(delete.ItemId);
                    break;
            }
        }
    }

    private void UpsertSnippetFallback(UserDataSyncSnippet synced)
    {
        var targetId = UserDataSyncIdentity.SnippetItemId(synced.Trigger);
        var existing = _snippets.Snippets.FirstOrDefault(snippet =>
            UserDataSyncIdentity.SnippetItemId(snippet.Trigger) == targetId);
        var tags = string.Join(",", synced.Tags);

        if (existing is not null)
        {
            _snippets.UpdateSnippet(existing with
            {
                Trigger = synced.Trigger,
                Replacement = synced.Replacement,
                CaseSensitive = synced.CaseSensitive,
                IsEnabled = synced.IsEnabled,
                Tags = tags,
                UpdatedAt = synced.UpdatedAt
            });
            return;
        }

        _snippets.AddSnippet(new Snippet
        {
            Id = Guid.NewGuid().ToString(),
            Trigger = synced.Trigger,
            Replacement = synced.Replacement,
            CaseSensitive = synced.CaseSensitive,
            IsEnabled = synced.IsEnabled,
            Tags = tags,
            CreatedAt = synced.CreatedAt,
            UpdatedAt = synced.UpdatedAt
        });
    }

    private void DeleteSnippetFallback(string itemId)
    {
        var snippet = _snippets.Snippets.FirstOrDefault(snippet =>
            UserDataSyncIdentity.SnippetItemId(snippet.Trigger) == itemId);
        if (snippet is not null)
            _snippets.DeleteSnippet(snippet.Id);
    }

    private void NotifyLocalChange()
    {
        if (_isApplyingRemoteChanges)
            return;

        foreach (var observer in _observers.Values.ToArray())
            observer();
    }
}
