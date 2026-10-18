using System.Collections.Frozen;
using System.Collections.ObjectModel;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>Turns a raw policy read into a validated snapshot following the contract in issue #470.</summary>
/// <remarks>
/// Invalid enforced values block their capability instead of relaxing it; invalid recommendations are only
/// reported. Issue messages name keys and rules but never carry the values that were rejected.
/// </remarks>
public static class ManagedPolicyValidator
{
    /// <summary>The longest accepted text value after trimming.</summary>
    public const int MaximumTextLength = 1024;

    /// <summary>The most entries a list may hold after deduplication.</summary>
    public const int MaximumListEntries = 256;

    private const string RecommendedSubkey = "Recommended";

    /// <summary>Validates one read.</summary>
    /// <param name="reading">The raw content.</param>
    /// <param name="revision">The revision to stamp on the result.</param>
    /// <param name="readAt">When the source was read.</param>
    public static ManagedPolicySnapshot Validate(ManagedPolicyReading reading, long revision, DateTimeOffset readAt)
    {
        var issues = new List<ManagedPolicyIssue>();
        if (!reading.Readable)
        {
            issues.Add(new(null, null, $"The policy source could not be read ({reading.Error ?? "unknown error"})."));
            return new(revision, ManagedPolicyReadStatus.Unreadable, readAt,
                ReadOnlyDictionary<string, ManagedPolicyValue>.Empty, ReadOnlyDictionary<string, ManagedPolicyValue>.Empty,
                FrozenSet<string>.Empty, issues.AsReadOnly());
        }

        var enforced = new Dictionary<string, ManagedPolicyValue>(StringComparer.Ordinal);
        var invalidEnforced = new HashSet<string>(StringComparer.Ordinal);
        ValidateScope(reading.Enforced, ManagedPolicyScope.Enforced, enforced, invalidEnforced, issues);

        var recommended = new Dictionary<string, ManagedPolicyValue>(StringComparer.Ordinal);
        ValidateScope(reading.Recommended, ManagedPolicyScope.Recommended, recommended, null, issues);

        var status = reading.Enforced.Exists || reading.Recommended.Exists
            ? ManagedPolicyReadStatus.Present
            : ManagedPolicyReadStatus.Absent;
        // The snapshot is shared process-wide and documented as immutable: publish frozen copies, not the working sets.
        return new(revision, status, readAt, enforced.AsReadOnly(), recommended.AsReadOnly(),
            invalidEnforced.ToFrozenSet(StringComparer.Ordinal), issues.AsReadOnly());
    }

    private static void ValidateScope(
        ManagedPolicyRawSection section,
        ManagedPolicyScope scope,
        Dictionary<string, ManagedPolicyValue> values,
        HashSet<string>? invalidEnforced,
        List<ManagedPolicyIssue> issues)
    {
        if (!section.Exists) return;
        var rawValues = CaseInsensitive(section.Values);
        var rawLists = CaseInsensitive(section.Lists);
        var consumedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consumedLists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        // The Recommended scope is read separately; it is never a list of the root.
        if (scope == ManagedPolicyScope.Enforced) consumedLists.Add(RecommendedSubkey);

        foreach (var definition in ManagedPolicyKeys.All)
        {
            var name = definition.Name;
            string? problem;
            ManagedPolicyValue? value;
            // A supported name in the wrong registry shape (a scalar written as a subkey, a list written as a value) is a
            // malformed enforced policy, not an unknown one: it blocks its setting like any other invalid value.
            var isList = definition.Kind == ManagedPolicyValueKind.List;
            if (isList ? rawValues.ContainsKey(name) : rawLists.ContainsKey(name))
            {
                (isList ? consumedValues : consumedLists).Add(name);
                invalid.Add(name);
                issues.Add(new(name, scope, isList
                    ? $"{name} must be a subkey of numbered values next to {definition.ConfiguredMarkerName}, not a value; the setting stays blocked."
                    : $"{name} must be a value, not a subkey; the setting stays blocked."));
            }
            if (isList && rawLists.ContainsKey(definition.ConfiguredMarkerName))
            {
                consumedLists.Add(definition.ConfiguredMarkerName);
                invalid.Add(name);
                issues.Add(new(name, scope, $"{definition.ConfiguredMarkerName} must be a REG_DWORD value, not a subkey; {name} stays blocked."));
            }
            if (isList)
            {
                var marker = definition.ConfiguredMarkerName;
                consumedValues.Add(marker);
                consumedLists.Add(name);
                var hasMarker = rawValues.TryGetValue(marker, out var rawMarker);
                var hasList = rawLists.TryGetValue(name, out var rawList);
                if (!hasMarker && !hasList) continue;
                if (scope == ManagedPolicyScope.Recommended && !definition.AllowsRecommendation)
                {
                    issues.Add(new(name, scope, $"{name} is a restriction and cannot be recommended; it is ignored."));
                    continue;
                }
                if (!hasMarker)
                {
                    issues.Add(new(name, scope, $"{name} is ignored because {marker} is not set."));
                    continue;
                }
                problem = ValidateList(definition, rawMarker!, hasList ? rawList : null, out value);
            }
            else
            {
                consumedValues.Add(name);
                if (!rawValues.TryGetValue(name, out var raw)) continue;
                if (scope == ManagedPolicyScope.Recommended && !definition.AllowsRecommendation)
                {
                    issues.Add(new(name, scope, $"{name} is a restriction and cannot be recommended; it is ignored."));
                    continue;
                }
                problem = ValidateScalar(definition, raw, out value);
            }

            if (problem is not null)
            {
                invalid.Add(name);
                issues.Add(new(name, scope, $"{name} {problem}."));
            }
            // Windows allows a value and a subkey of the same name: a key already marked malformed stays unset.
            else if (value is not null && !invalid.Contains(name))
            {
                values[name] = value;
            }
        }

        ValidatePairs(scope, values, invalid, issues);
        invalidEnforced?.UnionWith(invalid);

        foreach (var name in rawValues.Keys.Where(name => !consumedValues.Contains(name)).Order(StringComparer.OrdinalIgnoreCase))
            issues.Add(new(name, scope, $"Unknown value {name} is ignored."));
        foreach (var name in rawLists.Keys.Where(name => !consumedLists.Contains(name)).Order(StringComparer.OrdinalIgnoreCase))
            issues.Add(new(name, scope, $"Unknown subkey {name} is ignored."));
    }

    // Both members of a pair must be valid at the same scope; otherwise the pair is unusable there.
    private static void ValidatePairs(
        ManagedPolicyScope scope,
        Dictionary<string, ManagedPolicyValue> values,
        HashSet<string> invalid,
        List<ManagedPolicyIssue> issues)
    {
        foreach (var definition in ManagedPolicyKeys.All)
        {
            if (definition.PairedWith is not { } partner || string.CompareOrdinal(definition.Name, partner) > 0) continue;
            var first = definition.Name;
            var firstValid = values.ContainsKey(first);
            var partnerValid = values.ContainsKey(partner);
            if (firstValid && partnerValid) continue;
            var firstInvalid = invalid.Contains(first);
            var partnerInvalid = invalid.Contains(partner);
            if (!firstValid && !partnerValid && !firstInvalid && !partnerInvalid) continue;

            if (firstValid || partnerValid)
            {
                var missing = firstValid ? partner : first;
                issues.Add(new(missing, scope, $"{first} and {partner} must both be set at the same scope; the pair is ignored."));
            }
            values.Remove(first);
            values.Remove(partner);
            invalid.Add(first);
            invalid.Add(partner);
        }
    }

    private static string? ValidateScalar(ManagedPolicyDefinition definition, ManagedPolicyRawValue raw, out ManagedPolicyValue? value)
    {
        value = null;
        switch (definition.Kind)
        {
            case ManagedPolicyValueKind.Boolean:
                if (raw.Kind != ManagedPolicyRawKind.Number || raw.Number is not (0 or 1))
                    return "must be a REG_DWORD of 0 or 1";
                value = ManagedPolicyValue.ForBoolean(raw.Number == 1);
                return null;
            case ManagedPolicyValueKind.Enumeration:
                if (raw.Kind != ManagedPolicyRawKind.Number || raw.Number < 0 || raw.Number > definition.EnumerationMaximum)
                    return $"must be a REG_DWORD between 0 and {definition.EnumerationMaximum}";
                value = ManagedPolicyValue.ForEnumeration((int)raw.Number);
                return null;
            case ManagedPolicyValueKind.Text:
                if (raw.Kind != ManagedPolicyRawKind.Text)
                    return "must be a REG_SZ";
                var problem = ValidateText(raw.Text, out var text);
                if (problem is not null) return problem;
                value = ManagedPolicyValue.ForText(text);
                return null;
            default:
                return "has an unsupported kind";
        }
    }

    private static string? ValidateList(
        ManagedPolicyDefinition definition,
        ManagedPolicyRawValue marker,
        ManagedPolicyRawList? list,
        out ManagedPolicyValue? value)
    {
        value = null;
        if (marker.Kind != ManagedPolicyRawKind.Number || marker.Number is not (0 or 1))
            return $"is ignored because {definition.ConfiguredMarkerName} must be a REG_DWORD of 0 or 1";
        // Marker 0 means unrestricted regardless of any list content.
        if (marker.Number == 0) return null;
        // Marker 1 without a list is an empty allowlist: deny all.
        if (list is null)
        {
            value = ManagedPolicyValue.ForList([]);
            return null;
        }
        if (list.Malformed)
            return "contains entries that are not numbered REG_SZ values";

        var items = new List<string>(list.Items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in list.Items)
        {
            if (ValidateText(raw, out var item) is not null)
                return "contains an entry that is empty, too long or has control characters";
            if (seen.Add(item)) items.Add(item);
        }
        if (items.Count > MaximumListEntries)
            return $"must not exceed {MaximumListEntries} entries";
        value = ManagedPolicyValue.ForList(items);
        return null;
    }

    private static string? ValidateText(string? raw, out string text)
    {
        text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return "must not be empty";
        if (text.Length > MaximumTextLength) return $"must not exceed {MaximumTextLength} characters";
        if (text.Any(char.IsControl)) return "must not contain control characters";
        return null;
    }

    private static Dictionary<string, T> CaseInsensitive<T>(IReadOnlyDictionary<string, T> source)
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in source) map.TryAdd(entry.Key, entry.Value);
        return map;
    }
}
