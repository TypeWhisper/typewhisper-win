using System.Globalization;
using System.Text;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.Sync;

public sealed record PaidEntitlements(bool CanUseCloudFolderSync = false);

public enum UserDataSyncCollection
{
    Dictionary,
    Snippets
}

public enum UserDataSyncDictionaryEntryType
{
    Term,
    Correction
}

public sealed record UserDataSyncDictionaryEntry(
    UserDataSyncDictionaryEntryType EntryType,
    string Original,
    string? Replacement,
    bool CaseSensitive,
    bool IsEnabled,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record UserDataSyncSnippet(
    string Trigger,
    string Replacement,
    bool CaseSensitive,
    bool IsEnabled,
    IReadOnlyList<string> Tags,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record UserDataSyncSnapshot(
    IReadOnlyList<UserDataSyncDictionaryEntry>? DictionaryEntries = null,
    IReadOnlyList<UserDataSyncSnippet>? Snippets = null)
{
    public IReadOnlyList<UserDataSyncDictionaryEntry> DictionaryEntries { get; init; } = DictionaryEntries ?? [];
    public IReadOnlyList<UserDataSyncSnippet> Snippets { get; init; } = Snippets ?? [];
}

public abstract record UserDataSyncMutation
{
    public sealed record UpsertDictionary(UserDataSyncDictionaryEntry Entry) : UserDataSyncMutation;
    public sealed record DeleteDictionary(string ItemId) : UserDataSyncMutation;
    public sealed record UpsertSnippet(UserDataSyncSnippet Snippet) : UserDataSyncMutation;
    public sealed record DeleteSnippet(string ItemId) : UserDataSyncMutation;
}

public interface IUserDataSyncStore
{
    UserDataSyncSnapshot Snapshot();
    void Apply(IReadOnlyList<UserDataSyncMutation> mutations);
    Guid ObserveLocalChanges(Action handler);
    void RemoveLocalChangeObserver(Guid id);
}

public static class UserDataSyncIdentity
{
    public static string DictionaryItemId(UserDataSyncDictionaryEntryType entryType, string original) =>
        $"dictionary:{JsonName(entryType)}:{EncodedKey(NormalizedKey(original))}";

    public static string DictionaryItemId(DictionaryEntryType entryType, string original) =>
        DictionaryItemId(entryType == DictionaryEntryType.Term
            ? UserDataSyncDictionaryEntryType.Term
            : UserDataSyncDictionaryEntryType.Correction, original);

    public static string SnippetItemId(string trigger) =>
        $"snippet:{EncodedKey(NormalizedKey(trigger))}";

    public static string NormalizedKey(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(trimmed.Length);

        foreach (var ch in trimmed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string EncodedKey(string value)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return encoded
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    internal static string JsonName(UserDataSyncDictionaryEntryType entryType) =>
        entryType == UserDataSyncDictionaryEntryType.Term ? "term" : "correction";
}
