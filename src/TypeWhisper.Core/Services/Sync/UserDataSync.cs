using System.Globalization;
using System.Text;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.Sync;

/// <summary>
/// Represents paid entitlements data.
/// </summary>
/// <param name="CanUseCloudFolderSync">Can use cloud folder sync supplied to the member.</param>
public sealed record PaidEntitlements(bool CanUseCloudFolderSync = false);

/// <summary>
/// Lists the supported user data sync collection values.
/// </summary>
public enum UserDataSyncCollection
{
    /// <summary>
    /// Represents the dictionary option.
    /// </summary>
    Dictionary,
    /// <summary>
    /// Represents the snippets option.
    /// </summary>
    Snippets
}

/// <summary>
/// Lists the supported user data sync dictionary entry type values.
/// </summary>
public enum UserDataSyncDictionaryEntryType
{
    /// <summary>
    /// Represents the term option.
    /// </summary>
    Term,
    /// <summary>
    /// Represents the correction option.
    /// </summary>
    Correction
}

/// <summary>
/// Represents user data sync dictionary entry data.
/// </summary>
/// <param name="EntryType">Entry type supplied to the member.</param>
/// <param name="Original">Original supplied to the member.</param>
/// <param name="Replacement">Replacement supplied to the member.</param>
/// <param name="CaseSensitive">Case sensitive supplied to the member.</param>
/// <param name="IsEnabled">Is enabled supplied to the member.</param>
/// <param name="CreatedAt">Created at supplied to the member.</param>
/// <param name="UpdatedAt">Updated at supplied to the member.</param>
public sealed record UserDataSyncDictionaryEntry(
    UserDataSyncDictionaryEntryType EntryType,
    string Original,
    string? Replacement,
    bool CaseSensitive,
    bool IsEnabled,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Represents user data sync snippet data.
/// </summary>
/// <param name="Trigger">Trigger supplied to the member.</param>
/// <param name="Replacement">Replacement supplied to the member.</param>
/// <param name="CaseSensitive">Case sensitive supplied to the member.</param>
/// <param name="IsEnabled">Is enabled supplied to the member.</param>
/// <param name="Tags">Tags supplied to the member.</param>
/// <param name="CreatedAt">Created at supplied to the member.</param>
/// <param name="UpdatedAt">Updated at supplied to the member.</param>
public sealed record UserDataSyncSnippet(
    string Trigger,
    string Replacement,
    bool CaseSensitive,
    bool IsEnabled,
    IReadOnlyList<string> Tags,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Represents user data sync snapshot data.
/// </summary>
/// <param name="DictionaryEntries">Dictionary entries supplied to the member.</param>
/// <param name="Snippets">Snippets supplied to the member.</param>
public sealed record UserDataSyncSnapshot(
    IReadOnlyList<UserDataSyncDictionaryEntry>? DictionaryEntries = null,
    IReadOnlyList<UserDataSyncSnippet>? Snippets = null)
{
    /// <summary>
    /// Gets or sets the dictionary entries value.
    /// </summary>
    public IReadOnlyList<UserDataSyncDictionaryEntry> DictionaryEntries { get; init; } = DictionaryEntries ?? [];
    /// <summary>
    /// Gets or sets the snippets value.
    /// </summary>
    public IReadOnlyList<UserDataSyncSnippet> Snippets { get; init; } = Snippets ?? [];
}

/// <summary>
/// Represents user data sync mutation data.
/// </summary>
public abstract record UserDataSyncMutation
{
    /// <summary>
    /// Represents upsert dictionary data.
    /// </summary>
    /// <param name="Entry">Entry supplied to the member.</param>
    public sealed record UpsertDictionary(UserDataSyncDictionaryEntry Entry) : UserDataSyncMutation;
    /// <summary>
    /// Represents delete dictionary data.
    /// </summary>
    /// <param name="ItemId">Item id supplied to the member.</param>
    public sealed record DeleteDictionary(string ItemId) : UserDataSyncMutation;
    /// <summary>
    /// Represents upsert snippet data.
    /// </summary>
    /// <param name="Snippet">Snippet supplied to the member.</param>
    public sealed record UpsertSnippet(UserDataSyncSnippet Snippet) : UserDataSyncMutation;
    /// <summary>
    /// Represents delete snippet data.
    /// </summary>
    /// <param name="ItemId">Item id supplied to the member.</param>
    public sealed record DeleteSnippet(string ItemId) : UserDataSyncMutation;
}

/// <summary>
/// Defines the user data sync store contract.
/// </summary>
public interface IUserDataSyncStore
{
    /// <summary>
    /// Creates a snapshot asynchronously..
    /// </summary>
    UserDataSyncSnapshot Snapshot();
    /// <summary>
    /// Creates an app triggerly asynchronously..
    /// </summary>
    void Apply(IReadOnlyList<UserDataSyncMutation> mutations);
    /// <summary>
    /// Observes local changes.
    /// </summary>
    Guid ObserveLocalChanges(Action handler);
    /// <summary>
    /// Removes local change observer.
    /// </summary>
    void RemoveLocalChangeObserver(Guid id);
}

/// <summary>
/// Provides user data sync identity behavior.
/// </summary>
public static class UserDataSyncIdentity
{
    /// <summary>
    /// Returns the dictionary item id.
    /// </summary>
    public static string DictionaryItemId(UserDataSyncDictionaryEntryType entryType, string original) =>
        $"dictionary:{JsonName(entryType)}:{EncodedKey(NormalizedKey(original))}";

    /// <summary>
    /// Returns the dictionary item id.
    /// </summary>
    public static string DictionaryItemId(DictionaryEntryType entryType, string original) =>
        DictionaryItemId(entryType == DictionaryEntryType.Term
            ? UserDataSyncDictionaryEntryType.Term
            : UserDataSyncDictionaryEntryType.Correction, original);

    /// <summary>
    /// Returns the snippet item id.
    /// </summary>
    public static string SnippetItemId(string trigger) =>
        $"snippet:{EncodedKey(NormalizedKey(trigger))}";

    /// <summary>
    /// Returns the normalized key.
    /// </summary>
    public static string NormalizedKey(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(trimmed.Length);

        foreach (var ch in trimmed.Where(ch =>
                     CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark))
            builder.Append(ch);

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
