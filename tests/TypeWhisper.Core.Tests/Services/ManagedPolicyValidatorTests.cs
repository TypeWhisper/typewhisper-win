using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class ManagedPolicyValidatorTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static ManagedPolicyRawValue Number(long value) => ManagedPolicyRawValue.FromNumber(value);
    private static ManagedPolicyRawValue Text(string value) => ManagedPolicyRawValue.FromText(value);

    private static ManagedPolicyRawSection Section(params (string Name, ManagedPolicyRawValue Value)[] values) =>
        ManagedPolicyRawSection.Of(values.Select(entry => KeyValuePair.Create(entry.Name, entry.Value)));

    private static ManagedPolicyRawSection Section(
        IEnumerable<(string Name, ManagedPolicyRawValue Value)> values,
        IEnumerable<(string Name, ManagedPolicyRawList List)> lists) =>
        ManagedPolicyRawSection.Of(
            values.Select(entry => KeyValuePair.Create(entry.Name, entry.Value)),
            lists.Select(entry => KeyValuePair.Create(entry.Name, entry.List)));

    private static ManagedPolicySnapshot Validate(ManagedPolicyRawSection? enforced = null, ManagedPolicyRawSection? recommended = null, long revision = 7) =>
        ManagedPolicyValidator.Validate(
            new(true, null, enforced ?? ManagedPolicyRawSection.Missing, recommended ?? ManagedPolicyRawSection.Missing), revision, ReadAt);

    private static ManagedPolicyRawSection Allowlist(string name, long marker, params string[] items) =>
        Section([(name + "Configured", Number(marker))], [(name, new ManagedPolicyRawList(items))]);

    [Fact]
    public void UnreadableSource_BlocksEveryKeyAndCarriesNoValues()
    {
        var snapshot = ManagedPolicyValidator.Validate(ManagedPolicyReading.Unreadable("UnauthorizedAccessException"), 3, ReadAt);

        Assert.Equal(ManagedPolicyReadStatus.Unreadable, snapshot.Status);
        Assert.Equal(3, snapshot.Revision);
        Assert.Equal(ReadAt, snapshot.ReadAt);
        Assert.False(snapshot.IsManaged);
        Assert.True(snapshot.IsBlocked(ManagedPolicyKeys.AllowCloudTranscription));
        var issue = Assert.Single(snapshot.Issues);
        Assert.Null(issue.Key);
        Assert.Null(issue.Scope);
        Assert.Contains("UnauthorizedAccessException", issue.Message);
    }

    [Fact]
    public void MissingKeys_AreAbsentWithoutIssues()
    {
        var snapshot = Validate();

        Assert.Equal(ManagedPolicyReadStatus.Absent, snapshot.Status);
        Assert.False(snapshot.IsManaged);
        Assert.Empty(snapshot.Issues);
        Assert.False(snapshot.IsBlocked(ManagedPolicyKeys.AllowCloudTranscription));
        Assert.False(snapshot.IsEnforced(ManagedPolicyKeys.AllowCloudTranscription));
    }

    [Fact]
    public void EmptyRoot_IsPresentButUnmanaged()
    {
        var snapshot = Validate(Section());

        Assert.Equal(ManagedPolicyReadStatus.Present, snapshot.Status);
        Assert.False(snapshot.IsManaged);
        Assert.Empty(snapshot.Issues);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void Boolean_AcceptsExactlyZeroAndOne(long raw, bool expected)
    {
        var snapshot = Validate(Section((ManagedPolicyKeys.AllowCloudTranscription, Number(raw))));

        var value = Assert.IsType<ManagedPolicyValue>(snapshot.Enforced(ManagedPolicyKeys.AllowCloudTranscription));
        Assert.Equal(ManagedPolicyValueKind.Boolean, value.Kind);
        Assert.Equal(expected, value.IsTrue);
        Assert.True(snapshot.IsEnforced(ManagedPolicyKeys.AllowCloudTranscription));
        Assert.True(snapshot.IsManaged);
        Assert.Empty(snapshot.Issues);
    }

    [Fact]
    public void Boolean_RejectsOtherNumbersTextAndUnsupportedTypes()
    {
        var snapshot = Validate(Section(
            (ManagedPolicyKeys.AllowCloudTranscription, Number(2)),
            (ManagedPolicyKeys.AllowCloudTextProcessing, Text("1")),
            (ManagedPolicyKeys.AllowPluginDownloads, ManagedPolicyRawValue.Unsupported("Binary"))));

        string[] keys = [ManagedPolicyKeys.AllowCloudTextProcessing, ManagedPolicyKeys.AllowCloudTranscription, ManagedPolicyKeys.AllowPluginDownloads];
        Assert.Empty(snapshot.EnforcedValues);
        Assert.Equal(keys, snapshot.InvalidEnforcedKeys.Order(StringComparer.Ordinal));
        Assert.All(keys, key => Assert.True(snapshot.IsBlocked(key)));
        Assert.True(snapshot.IsManaged);
        Assert.Equal(3, snapshot.Issues.Count);
        Assert.All(snapshot.Issues, issue => Assert.Equal(ManagedPolicyScope.Enforced, issue.Scope));
        Assert.All(snapshot.Issues, issue => Assert.Contains("REG_DWORD of 0 or 1", issue.Message));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Enumeration_AcceptsItsRange(long raw)
    {
        var snapshot = Validate(Section((ManagedPolicyKeys.UpdateMode, Number(raw))));

        Assert.Equal(raw, snapshot.Enforced(ManagedPolicyKeys.UpdateMode)!.Number);
        Assert.Empty(snapshot.Issues);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(uint.MaxValue)]
    public void Enumeration_RejectsValuesOutsideItsRange(long raw)
    {
        var snapshot = Validate(Section((ManagedPolicyKeys.UpdateMode, Number(raw))));

        Assert.Null(snapshot.Enforced(ManagedPolicyKeys.UpdateMode));
        Assert.True(snapshot.IsBlocked(ManagedPolicyKeys.UpdateMode));
        var issue = Assert.Single(snapshot.Issues);
        Assert.Equal(ManagedPolicyKeys.UpdateMode, issue.Key);
        Assert.Contains("between 0 and 2", issue.Message);
    }

    [Fact]
    public void Text_IsTrimmed()
    {
        var snapshot = Validate(Section(
            (ManagedPolicyKeys.TranscriptionProviderId, Text("  com.typewhisper.sherpa-onnx  ")),
            (ManagedPolicyKeys.TranscriptionModelId, Text("parakeet-tdt-0.6b-v3\t"))));

        Assert.Equal("com.typewhisper.sherpa-onnx", snapshot.Enforced(ManagedPolicyKeys.TranscriptionProviderId)!.Text);
        Assert.Equal("parakeet-tdt-0.6b-v3", snapshot.Enforced(ManagedPolicyKeys.TranscriptionModelId)!.Text);
        Assert.Empty(snapshot.Issues);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\tb")]
    [InlineData("line\nbreak")]
    public void Text_RejectsEmptyAndControlCharacters(string raw)
    {
        var snapshot = Validate(Section(
            (ManagedPolicyKeys.TranscriptionProviderId, Text(raw)),
            (ManagedPolicyKeys.TranscriptionModelId, Text("model"))));

        Assert.Empty(snapshot.EnforcedValues);
        Assert.Contains(ManagedPolicyKeys.TranscriptionProviderId, snapshot.InvalidEnforcedKeys);
        Assert.Contains(snapshot.Issues, issue => issue.Key == ManagedPolicyKeys.TranscriptionProviderId && !issue.Message.Contains("same scope"));
    }

    [Fact]
    public void Text_RejectsMoreThanMaximumLength()
    {
        var longest = new string('a', ManagedPolicyValidator.MaximumTextLength);
        var tooLong = longest + "a";

        var accepted = Validate(Section(
            (ManagedPolicyKeys.LlmProviderId, Text(longest)), (ManagedPolicyKeys.LlmModelId, Text("m"))));
        var rejected = Validate(Section(
            (ManagedPolicyKeys.LlmProviderId, Text(tooLong)), (ManagedPolicyKeys.LlmModelId, Text("m"))));

        Assert.Equal(longest, accepted.Enforced(ManagedPolicyKeys.LlmProviderId)!.Text);
        Assert.Empty(rejected.EnforcedValues);
        Assert.Contains(rejected.Issues, issue => issue.Message.Contains("1024 characters"));
    }

    [Fact]
    public void Text_RejectsNumbers()
    {
        var snapshot = Validate(Section(
            (ManagedPolicyKeys.LlmProviderId, Number(1)), (ManagedPolicyKeys.LlmModelId, Text("m"))));

        Assert.Contains(snapshot.Issues, issue => issue.Key == ManagedPolicyKeys.LlmProviderId && issue.Message.Contains("REG_SZ"));
        Assert.Empty(snapshot.EnforcedValues);
    }

    [Fact]
    public void Pair_RequiresBothMembersAtTheSameScope()
    {
        var snapshot = Validate(
            Section((ManagedPolicyKeys.TranscriptionProviderId, Text("provider"))),
            Section((ManagedPolicyKeys.TranscriptionModelId, Text("model"))));

        Assert.Empty(snapshot.EnforcedValues);
        Assert.Empty(snapshot.RecommendedValues);
        Assert.Contains(ManagedPolicyKeys.TranscriptionProviderId, snapshot.InvalidEnforcedKeys);
        Assert.Contains(ManagedPolicyKeys.TranscriptionModelId, snapshot.InvalidEnforcedKeys);
        Assert.Contains(snapshot.Issues, issue =>
            issue.Key == ManagedPolicyKeys.TranscriptionModelId && issue.Scope == ManagedPolicyScope.Enforced && issue.Message.Contains("same scope"));
        Assert.Contains(snapshot.Issues, issue =>
            issue.Key == ManagedPolicyKeys.TranscriptionProviderId && issue.Scope == ManagedPolicyScope.Recommended && issue.Message.Contains("same scope"));
        Assert.Equal(2, snapshot.Issues.Count);
    }

    [Fact]
    public void Pair_WithBothMembers_IsAcceptedAtEitherScope()
    {
        var snapshot = Validate(
            Section((ManagedPolicyKeys.TranscriptionProviderId, Text("p1")), (ManagedPolicyKeys.TranscriptionModelId, Text("m1"))),
            Section((ManagedPolicyKeys.LlmProviderId, Text("p2")), (ManagedPolicyKeys.LlmModelId, Text("m2"))));

        Assert.Equal(2, snapshot.EnforcedValues.Count);
        Assert.Equal(2, snapshot.RecommendedValues.Count);
        Assert.Equal("m2", snapshot.Recommended(ManagedPolicyKeys.LlmModelId)!.Text);
        Assert.Empty(snapshot.Issues);
        Assert.Empty(snapshot.InvalidEnforcedKeys);
    }

    [Fact]
    public void Pair_WithOneInvalidMember_IsRejectedAsAWhole()
    {
        var snapshot = Validate(Section(
            (ManagedPolicyKeys.LlmProviderId, Text("provider")), (ManagedPolicyKeys.LlmModelId, Text(""))));

        Assert.Empty(snapshot.EnforcedValues);
        Assert.Equal([ManagedPolicyKeys.LlmModelId, ManagedPolicyKeys.LlmProviderId], snapshot.InvalidEnforcedKeys.Order(StringComparer.Ordinal));
        Assert.Equal(2, snapshot.Issues.Count);
    }

    [Fact]
    public void List_WithoutMarker_IsIgnoredWithAnIssue()
    {
        var snapshot = Validate(Section([], [(ManagedPolicyKeys.AllowedPlugins, new ManagedPolicyRawList(["a"]))]));

        Assert.Empty(snapshot.EnforcedValues);
        Assert.Empty(snapshot.InvalidEnforcedKeys);
        var issue = Assert.Single(snapshot.Issues);
        Assert.Equal(ManagedPolicyKeys.AllowedPlugins, issue.Key);
        Assert.Contains("AllowedPluginsConfigured is not set", issue.Message);
    }

    [Fact]
    public void List_WithMarkerZero_IsUnrestrictedRegardlessOfEntries()
    {
        var snapshot = Validate(Allowlist(ManagedPolicyKeys.AllowedPlugins, 0, "a", "b"));

        Assert.Empty(snapshot.EnforcedValues);
        Assert.Empty(snapshot.Issues);
        Assert.False(snapshot.IsBlocked(ManagedPolicyKeys.AllowedPlugins));
    }

    [Fact]
    public void List_WithMarkerOneAndNoSubkey_DeniesAll()
    {
        var snapshot = Validate(Section((ManagedPolicyKeys.AllowedPlugins + "Configured", Number(1))));

        var value = snapshot.Enforced(ManagedPolicyKeys.AllowedPlugins);
        Assert.NotNull(value);
        Assert.Equal(ManagedPolicyValueKind.List, value.Kind);
        Assert.Empty(value.Items!);
        Assert.Empty(snapshot.Issues);
    }

    [Fact]
    public void List_DeduplicatesTrimsAndKeepsOrder()
    {
        var snapshot = Validate(Allowlist(ManagedPolicyKeys.AllowedTranscriptionProviders, 1, " b ", "a", "b", "a "));

        Assert.Equal(["b", "a"], snapshot.Enforced(ManagedPolicyKeys.AllowedTranscriptionProviders)!.Items);
        Assert.Empty(snapshot.Issues);
    }

    [Fact]
    public void List_RejectsMalformedEntriesAndInvalidMarkers()
    {
        var malformed = Validate(Section(
            [(ManagedPolicyKeys.AllowedPlugins + "Configured", Number(1))],
            [(ManagedPolicyKeys.AllowedPlugins, new ManagedPolicyRawList(["a"], Malformed: true))]));
        var emptyEntry = Validate(Allowlist(ManagedPolicyKeys.AllowedPlugins, 1, "a", " "));
        var markerTwo = Validate(Allowlist(ManagedPolicyKeys.AllowedPlugins, 2, "a"));
        var markerText = Validate(Section(
            [(ManagedPolicyKeys.AllowedPlugins + "Configured", Text("1"))],
            [(ManagedPolicyKeys.AllowedPlugins, new ManagedPolicyRawList(["a"]))]));

        foreach (var snapshot in new[] { malformed, emptyEntry, markerTwo, markerText })
        {
            Assert.Empty(snapshot.EnforcedValues);
            Assert.True(snapshot.IsBlocked(ManagedPolicyKeys.AllowedPlugins));
            var issue = Assert.Single(snapshot.Issues);
            Assert.Equal(ManagedPolicyKeys.AllowedPlugins, issue.Key);
        }
        Assert.Contains("numbered REG_SZ", malformed.Issues[0].Message);
        Assert.Contains("AllowedPluginsConfigured must be", markerTwo.Issues[0].Message);
    }

    [Fact]
    public void List_RejectsMoreThanMaximumEntries()
    {
        var limit = Enumerable.Range(1, ManagedPolicyValidator.MaximumListEntries).Select(i => $"id{i}").ToArray();
        var accepted = Validate(Allowlist(ManagedPolicyKeys.AllowedLlmProviders, 1, limit));
        var rejected = Validate(Allowlist(ManagedPolicyKeys.AllowedLlmProviders, 1, [.. limit, "one-more"]));
        var duplicatesWithinLimit = Validate(Allowlist(ManagedPolicyKeys.AllowedLlmProviders, 1, [.. limit, "id1"]));

        Assert.Equal(ManagedPolicyValidator.MaximumListEntries, accepted.Enforced(ManagedPolicyKeys.AllowedLlmProviders)!.Items!.Count);
        Assert.True(rejected.IsBlocked(ManagedPolicyKeys.AllowedLlmProviders));
        Assert.Contains("256 entries", Assert.Single(rejected.Issues).Message);
        Assert.Equal(ManagedPolicyValidator.MaximumListEntries, duplicatesWithinLimit.Enforced(ManagedPolicyKeys.AllowedLlmProviders)!.Items!.Count);
    }

    [Fact]
    public void Restrictions_UnderRecommended_AreIgnoredNotPromoted()
    {
        var recommended = Section(
            [
                (ManagedPolicyKeys.AllowCloudTranscription, Number(0)),
                (ManagedPolicyKeys.SkipOnboardingWhenReady, Number(1)),
                (ManagedPolicyKeys.UpdateMode, Number(0)),
                (ManagedPolicyKeys.AllowedPlugins + "Configured", Number(1))
            ],
            [(ManagedPolicyKeys.AllowedPlugins, new ManagedPolicyRawList(["a"]))]);

        var snapshot = Validate(recommended: recommended);

        Assert.Equal(ManagedPolicyReadStatus.Present, snapshot.Status);
        Assert.Empty(snapshot.EnforcedValues);
        Assert.Empty(snapshot.RecommendedValues);
        Assert.Empty(snapshot.InvalidEnforcedKeys);
        Assert.False(snapshot.IsManaged);
        Assert.Equal(4, snapshot.Issues.Count);
        Assert.All(snapshot.Issues, issue => Assert.Equal(ManagedPolicyScope.Recommended, issue.Scope));
        Assert.All(snapshot.Issues, issue => Assert.Contains("cannot be recommended", issue.Message));
    }

    [Fact]
    public void Recommendations_AreAcceptedForTheAllowedKeys()
    {
        var snapshot = Validate(recommended: Section(
            (ManagedPolicyKeys.UpdateChannel, Number(1)),
            (ManagedPolicyKeys.LaunchAtLogin, Number(1)),
            (ManagedPolicyKeys.SuppressOptionalPrompts, Number(0)),
            (ManagedPolicyKeys.LlmProviderId, Text("com.typewhisper.local-llm-llamacpp")),
            (ManagedPolicyKeys.LlmModelId, Text("gemma"))));

        Assert.Empty(snapshot.EnforcedValues);
        Assert.Equal(5, snapshot.RecommendedValues.Count);
        Assert.Equal(1, snapshot.Recommended(ManagedPolicyKeys.UpdateChannel)!.Number);
        Assert.False(snapshot.Recommended(ManagedPolicyKeys.SuppressOptionalPrompts)!.IsTrue);
        Assert.True(snapshot.IsManaged);
        Assert.False(snapshot.IsEnforced(ManagedPolicyKeys.LaunchAtLogin));
        Assert.Empty(snapshot.Issues);
    }

    [Fact]
    public void InvalidRecommendation_IsReportedWithoutBlocking()
    {
        var snapshot = Validate(recommended: Section((ManagedPolicyKeys.UpdateChannel, Number(9))));

        Assert.Empty(snapshot.RecommendedValues);
        Assert.Empty(snapshot.InvalidEnforcedKeys);
        Assert.False(snapshot.IsBlocked(ManagedPolicyKeys.UpdateChannel));
        var issue = Assert.Single(snapshot.Issues);
        Assert.Equal(ManagedPolicyScope.Recommended, issue.Scope);
    }

    [Fact]
    public void UnknownNames_AreReportedAndIgnored()
    {
        var snapshot = Validate(Section(
            [("Telemetry", Number(0)), (ManagedPolicyKeys.LaunchAtLogin, Number(1))],
            [("Extra", new ManagedPolicyRawList(["x"])), ("Recommended", new ManagedPolicyRawList([]))]));

        Assert.True(snapshot.Enforced(ManagedPolicyKeys.LaunchAtLogin)!.IsTrue);
        Assert.Equal(2, snapshot.Issues.Count);
        Assert.Contains(snapshot.Issues, issue => issue.Key == "Telemetry" && issue.Message.Contains("Unknown value"));
        Assert.Contains(snapshot.Issues, issue => issue.Key == "Extra" && issue.Message.Contains("Unknown subkey"));
        Assert.Empty(snapshot.InvalidEnforcedKeys);
    }

    [Fact]
    public void Names_AreMatchedCaseInsensitivelyAndReportedCanonically()
    {
        var snapshot = Validate(Section(
            [("allowcloudtranscription", Number(0)), ("ALLOWEDPLUGINSCONFIGURED", Number(1))],
            [("allowedplugins", new ManagedPolicyRawList(["a"]))]));

        Assert.False(snapshot.Enforced(ManagedPolicyKeys.AllowCloudTranscription)!.IsTrue);
        Assert.Equal(["a"], snapshot.Enforced(ManagedPolicyKeys.AllowedPlugins)!.Items);
        Assert.Equal([ManagedPolicyKeys.AllowCloudTranscription, ManagedPolicyKeys.AllowedPlugins], snapshot.EnforcedValues.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(snapshot.Issues);
    }

    [Fact]
    public void Resolve_FollowsPrecedenceAndChecksKinds()
    {
        var snapshot = Validate(
            Section((ManagedPolicyKeys.LaunchAtLogin, Number(1)), (ManagedPolicyKeys.UpdateMode, Number(2))),
            Section((ManagedPolicyKeys.SuppressOptionalPrompts, Number(1)), (ManagedPolicyKeys.LaunchAtLogin, Number(0)),
                (ManagedPolicyKeys.UpdateChannel, Number(1))));

        var enforced = snapshot.ResolveBoolean(ManagedPolicyKeys.LaunchAtLogin, false, userPreference: false);
        Assert.True(enforced.Value);
        Assert.Equal(ManagedSettingSource.EnforcedPolicy, enforced.Source);
        Assert.True(enforced.IsEnforced);

        var recommended = snapshot.ResolveBoolean(ManagedPolicyKeys.SuppressOptionalPrompts, false);
        Assert.True(recommended.Value);
        Assert.Equal(ManagedSettingSource.RecommendedPolicy, recommended.Source);

        var user = snapshot.ResolveBoolean(ManagedPolicyKeys.SuppressOptionalPrompts, false, userPreference: false);
        Assert.False(user.Value);
        Assert.Equal(ManagedSettingSource.UserPreference, user.Source);

        var fallback = snapshot.ResolveBoolean(ManagedPolicyKeys.AllowCloudTranscription, true);
        Assert.True(fallback.Value);
        Assert.Equal(ManagedSettingSource.ApplicationDefault, fallback.Source);

        Assert.Equal(2, snapshot.ResolveEnumeration(ManagedPolicyKeys.UpdateMode, 0, userPreference: 1).Value);
        Assert.Equal(1, snapshot.ResolveEnumeration(ManagedPolicyKeys.UpdateChannel, 0).Value);
        Assert.Equal("default", snapshot.ResolveText(ManagedPolicyKeys.TranscriptionProviderId, "default").Value);

        Assert.Throws<ArgumentException>(() => snapshot.ResolveText(ManagedPolicyKeys.LaunchAtLogin, "x"));
        Assert.Throws<ArgumentException>(() => snapshot.ResolveBoolean("NoSuchKey", true));
    }

    [Fact]
    public void WrongRegistryShape_BlocksTheSupportedKeyInsteadOfIgnoringIt()
    {
        // A scalar written as a subkey and a list written as a plain value.
        var snapshot = Validate(Section(
            [("AllowedPlugins", Text("com.typewhisper.sherpa-onnx")), ("UpdateMode", Number(1))],
            [("AllowCloudTranscription", new ManagedPolicyRawList(["0"]))]));

        Assert.True(snapshot.IsBlocked("AllowCloudTranscription"));
        Assert.True(snapshot.IsBlocked("AllowedPlugins"));
        Assert.False(snapshot.IsBlocked("UpdateMode"));
        Assert.Equal(1, snapshot.Enforced("UpdateMode")!.Number);
        Assert.Null(snapshot.Enforced("AllowCloudTranscription"));
        Assert.Null(snapshot.Enforced("AllowedPlugins"));
        Assert.Contains(snapshot.Issues, issue => issue.Key == "AllowCloudTranscription" && issue.Message.Contains("must be a value"));
        Assert.Contains(snapshot.Issues, issue => issue.Key == "AllowedPlugins" && issue.Message.Contains("must be a subkey"));
        Assert.DoesNotContain(snapshot.Issues, issue => issue.Message.StartsWith("Unknown", StringComparison.Ordinal));

        // A configured marker written as a subkey blocks its list as well.
        var marker = Validate(Section([], [("AllowedPluginsConfigured", new ManagedPolicyRawList(["1"]))]));
        Assert.True(marker.IsBlocked("AllowedPlugins"));
        Assert.Null(marker.Enforced("AllowedPlugins"));
        Assert.Contains(marker.Issues, issue => issue.Key == "AllowedPlugins" && issue.Message.Contains("AllowedPluginsConfigured must be a REG_DWORD value"));
        Assert.DoesNotContain(marker.Issues, issue => issue.Message.StartsWith("Unknown", StringComparison.Ordinal));

        // A value and a subkey of the same name: the malformed shape wins and the value is not applied, in either scope.
        var both = Validate(
            Section([("LaunchAtLogin", Number(1))], [("LaunchAtLogin", new ManagedPolicyRawList(["1"]))]),
            Section([("LaunchAtLogin", Number(1))], [("LaunchAtLogin", new ManagedPolicyRawList(["1"]))]));
        Assert.True(both.IsBlocked("LaunchAtLogin"));
        Assert.Null(both.Enforced("LaunchAtLogin"));
        Assert.Null(both.Recommended("LaunchAtLogin"));
        Assert.Equal(2, both.Issues.Count(issue => issue.Key == "LaunchAtLogin" && issue.Message.Contains("must be a value")));

        // The same shapes under Recommended are reported but block nothing.
        var recommended = Validate(recommended: Section(
            [("TranscriptionProviderId", Text("x"))],
            [("UpdateChannel", new ManagedPolicyRawList(["0"]))]));
        Assert.False(recommended.IsBlocked("UpdateChannel"));
        Assert.Contains(recommended.Issues, issue => issue.Key == "UpdateChannel" && issue.Message.Contains("must be a value"));
    }

    [Fact]
    public void PublishedCollectionsCannotBeMutatedThroughADowncast()
    {
        var snapshot = Validate(Section(("AllowCloudTranscription", Number(0)), ("UpdateMode", Number(9))));

        Assert.IsNotType<Dictionary<string, ManagedPolicyValue>>(snapshot.EnforcedValues);
        Assert.IsNotType<Dictionary<string, ManagedPolicyValue>>(snapshot.RecommendedValues);
        Assert.IsNotType<HashSet<string>>(snapshot.InvalidEnforcedKeys);
        Assert.IsNotType<List<ManagedPolicyIssue>>(snapshot.Issues);
        Assert.IsNotType<HashSet<string>>(ManagedPolicySnapshot.Empty.InvalidEnforcedKeys);
        Assert.IsNotType<string[]>(ManagedPolicyValue.ForList(["a"]).Items);
        var unreadable = ManagedPolicyValidator.Validate(ManagedPolicyReading.Unreadable("IOException"), 1, ReadAt);
        Assert.IsNotType<HashSet<string>>(unreadable.InvalidEnforcedKeys);
        Assert.IsNotType<List<ManagedPolicyIssue>>(unreadable.Issues);
    }

    [Fact]
    public void ContentEquals_IgnoresRevisionAndReadTime()
    {
        var section = Section((ManagedPolicyKeys.AllowModelDownloads, Number(0)));
        var first = Validate(section, revision: 1);
        var second = ManagedPolicyValidator.Validate(new(true, null, section, ManagedPolicyRawSection.Missing), 2, ReadAt.AddMinutes(1));
        var different = Validate(Section((ManagedPolicyKeys.AllowModelDownloads, Number(1))));
        var withIssue = Validate(Section((ManagedPolicyKeys.AllowModelDownloads, Number(0)), ("Unknown", Number(0))));

        Assert.True(first.ContentEquals(second));
        Assert.False(first.ContentEquals(different));
        Assert.False(first.ContentEquals(withIssue));
        Assert.False(first.ContentEquals(null));
        Assert.False(first.ContentEquals(ManagedPolicySnapshot.Empty));
    }

    [Fact]
    public void RetainingValuesFrom_KeepsEarlierValuesWithTheNewStatus()
    {
        var readable = Validate(Section((ManagedPolicyKeys.AllowModelDownloads, Number(0)), (ManagedPolicyKeys.UpdateMode, Number(5))));
        var unreadable = ManagedPolicyValidator.Validate(ManagedPolicyReading.Unreadable("IOException"), 8, ReadAt);

        var retained = unreadable.RetainingValuesFrom(readable);

        Assert.Equal(8, retained.Revision);
        Assert.Equal(ManagedPolicyReadStatus.Unreadable, retained.Status);
        Assert.False(retained.Enforced(ManagedPolicyKeys.AllowModelDownloads)!.IsTrue);
        Assert.Contains(ManagedPolicyKeys.UpdateMode, retained.InvalidEnforcedKeys);
        Assert.True(retained.IsBlocked(ManagedPolicyKeys.AllowModelDownloads));
        Assert.Equal(unreadable.Issues, retained.Issues);
    }

    [Fact]
    public void Catalog_ListsEveryKeyFromTheContractOnce()
    {
        string[] expected =
        [
            "TranscriptionProviderId", "TranscriptionModelId", "LlmProviderId", "LlmModelId",
            "AllowedTranscriptionProviders", "AllowedLlmProviders", "AllowedPlugins",
            "AllowCloudTranscription", "AllowCloudTextProcessing", "AllowPluginDownloads", "AllowModelDownloads",
            "UpdateMode", "UpdateChannel", "AllowedUpdateChannels", "LaunchAtLogin", "SkipOnboardingWhenReady", "SuppressOptionalPrompts"
        ];

        Assert.Equal(expected, ManagedPolicyKeys.All.Select(definition => definition.Name));
        Assert.All(ManagedPolicyKeys.All, definition => Assert.Same(definition, ManagedPolicyKeys.Find(definition.Name)));
        Assert.Null(ManagedPolicyKeys.Find("transcriptionproviderid"));
        Assert.Equal(
            ["LaunchAtLogin", "LlmModelId", "LlmProviderId", "SuppressOptionalPrompts", "TranscriptionModelId", "TranscriptionProviderId", "UpdateChannel"],
            ManagedPolicyKeys.All.Where(definition => definition.AllowsRecommendation).Select(definition => definition.Name).Order(StringComparer.Ordinal));
        Assert.All(ManagedPolicyKeys.All.Where(definition => definition.PairedWith is not null),
            definition => Assert.Equal(definition.Name, ManagedPolicyKeys.Find(definition.PairedWith!)!.PairedWith));
    }
}
