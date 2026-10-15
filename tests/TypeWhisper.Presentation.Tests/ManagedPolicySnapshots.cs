using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation.Tests;

// Builds validated snapshots the same way the registry source would feed them.
internal static class ManagedPolicySnapshots
{
    public static KeyValuePair<string, ManagedPolicyRawValue> Number(string key, long value) =>
        KeyValuePair.Create(key, ManagedPolicyRawValue.FromNumber(value));

    public static KeyValuePair<string, ManagedPolicyRawValue> Text(string key, string value) =>
        KeyValuePair.Create(key, ManagedPolicyRawValue.FromText(value));

    public static KeyValuePair<string, ManagedPolicyRawList> List(string key, params string[] items) =>
        KeyValuePair.Create(key, new ManagedPolicyRawList(items));

    public static ManagedPolicySnapshot Build(
        IEnumerable<KeyValuePair<string, ManagedPolicyRawValue>>? enforced = null,
        IEnumerable<KeyValuePair<string, ManagedPolicyRawValue>>? recommended = null,
        IEnumerable<KeyValuePair<string, ManagedPolicyRawList>>? enforcedLists = null)
    {
        var root = enforced is null && enforcedLists is null ? ManagedPolicyRawSection.Missing : ManagedPolicyRawSection.Of(enforced, enforcedLists);
        var recommendations = recommended is null ? ManagedPolicyRawSection.Missing : ManagedPolicyRawSection.Of(recommended);
        return ManagedPolicyValidator.Validate(new(true, null, root, recommendations), 1, DateTimeOffset.UtcNow);
    }

    public static ManagedPolicySnapshot Unreadable() =>
        ManagedPolicyValidator.Validate(ManagedPolicyReading.Unreadable("IOException"), 1, DateTimeOffset.UtcNow);
}
