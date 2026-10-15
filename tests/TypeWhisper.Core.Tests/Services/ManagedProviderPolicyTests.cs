using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class ManagedProviderPolicyTests
{
    private static ManagedPolicyRawValue Number(long value) => ManagedPolicyRawValue.FromNumber(value);

    private static ManagedPolicySnapshot Snapshot(
        IEnumerable<(string Name, ManagedPolicyRawValue Value)>? values = null,
        IEnumerable<(string Name, ManagedPolicyRawList List)>? lists = null)
    {
        var section = values is null && lists is null ? ManagedPolicyRawSection.Missing : ManagedPolicyRawSection.Of(
            (values ?? []).Select(entry => KeyValuePair.Create(entry.Name, entry.Value)),
            (lists ?? []).Select(entry => KeyValuePair.Create(entry.Name, entry.List)));
        return ManagedPolicyValidator.Validate(new(true, null, section, ManagedPolicyRawSection.Missing), 1, DateTimeOffset.UtcNow);
    }

    private static ManagedPolicySnapshot Allowlist(string name, params string[] items) =>
        Snapshot([(name + "Configured", Number(1))], [(name, new ManagedPolicyRawList(items))]);

    [Fact]
    public void From_ReadsEverythingAndNoneIsUnmanaged()
    {
        var snapshot = Snapshot(
            [
                (ManagedPolicyKeys.AllowCloudTranscription, Number(0)), (ManagedPolicyKeys.AllowCloudTextProcessing, Number(1)),
                (ManagedPolicyKeys.AllowPluginDownloads, Number(0)), (ManagedPolicyKeys.AllowModelDownloads, Number(1)),
                (ManagedPolicyKeys.AllowedPlugins + "Configured", Number(1)), (ManagedPolicyKeys.AllowedLlmProviders + "Configured", Number(1))
            ],
            [
                (ManagedPolicyKeys.AllowedPlugins, new ManagedPolicyRawList(["com.typewhisper.sherpa-onnx", "COM.TYPEWHISPER.GROQ"])),
                (ManagedPolicyKeys.AllowedLlmProviders, new ManagedPolicyRawList([]))
            ]);

        var policy = ManagedProviderPolicy.From(snapshot);

        Assert.False(policy.CloudTranscriptionAllowed);
        Assert.True(policy.CloudTextProcessingAllowed);
        Assert.False(policy.PluginDownloadsAllowed);
        Assert.True(policy.ModelDownloadsAllowed);
        Assert.Null(policy.AllowedTranscriptionProviders);
        Assert.Empty(policy.AllowedLlmProviders!);
        Assert.Contains("com.typewhisper.groq", policy.AllowedPlugins!);
        Assert.Empty(policy.BlockedKeys);
        Assert.True(policy.IsManaged);
        var unmanaged = ManagedProviderPolicy.From(Snapshot());
        Assert.False(unmanaged.IsManaged);
        Assert.Empty(unmanaged.BlockedKeys);
        Assert.Null(unmanaged.AllowedPlugins);
    }

    [Fact]
    public void None_AllowsEverything()
    {
        var policy = ManagedProviderPolicy.None;

        Assert.Null(policy.PluginRefusal("com.typewhisper.openai"));
        Assert.Null(policy.TranscriptionRefusal("com.typewhisper.openai", "com.typewhisper.openai", ContentLocality.Cloud));
        Assert.Null(policy.LlmRefusal("com.typewhisper.openai", "com.typewhisper.openai", ContentLocality.Configurable));
        Assert.Null(policy.TextTransferRefusal("com.typewhisper.webhook", ContentLocality.Unknown));
        Assert.Null(policy.PluginDownloadRefusal());
        Assert.Null(policy.ModelDownloadRefusal());
    }

    [Theory]
    [InlineData(ContentLocality.Cloud, true)]
    [InlineData(ContentLocality.Configurable, true)]
    [InlineData(ContentLocality.Unknown, true)]
    [InlineData(ContentLocality.Local, false)]
    public void CloudTranscriptionOff_RefusesEverythingThatIsNotLocal(ContentLocality locality, bool refused)
    {
        var policy = ManagedProviderPolicy.From(Snapshot([(ManagedPolicyKeys.AllowCloudTranscription, Number(0))]));

        var refusal = policy.TranscriptionRefusal("com.typewhisper.x", "com.typewhisper.x", locality);

        Assert.Equal(refused, refusal is not null);
        if (refused) Assert.Contains("Cloud transcription is turned off", refusal);
        Assert.Null(policy.LlmRefusal("com.typewhisper.x", "com.typewhisper.x", locality));
    }

    [Theory]
    [InlineData(ContentLocality.Cloud, true)]
    [InlineData(ContentLocality.Configurable, true)]
    [InlineData(ContentLocality.Local, false)]
    public void CloudTextOff_RefusesLlmAndTextTransfersThatAreNotLocal(ContentLocality locality, bool refused)
    {
        var policy = ManagedProviderPolicy.From(Snapshot([(ManagedPolicyKeys.AllowCloudTextProcessing, Number(0))]));

        Assert.Equal(refused, policy.LlmRefusal("com.typewhisper.x", "com.typewhisper.x", locality) is not null);
        Assert.Equal(refused, policy.TextTransferRefusal("com.typewhisper.x", locality) is not null);
        Assert.Null(policy.TranscriptionRefusal("com.typewhisper.x", "com.typewhisper.x", locality));
    }

    [Fact]
    public void Allowlists_CompareCaseInsensitivelyAndEmptyDeniesAll()
    {
        var providers = ManagedProviderPolicy.From(Allowlist(ManagedPolicyKeys.AllowedTranscriptionProviders, "OpenAI-Compatible-abc"));
        Assert.Null(providers.TranscriptionRefusal("openai-compatible-abc", "com.typewhisper.openai-compatible", ContentLocality.Configurable));
        Assert.Contains("not allowed", providers.TranscriptionRefusal("com.typewhisper.groq", "com.typewhisper.groq", ContentLocality.Cloud));

        var llm = ManagedProviderPolicy.From(Allowlist(ManagedPolicyKeys.AllowedLlmProviders));
        Assert.Contains("not allowed", llm.LlmRefusal("com.typewhisper.claude", "com.typewhisper.claude", ContentLocality.Local));

        var plugins = ManagedProviderPolicy.From(Allowlist(ManagedPolicyKeys.AllowedPlugins, "com.typewhisper.sherpa-onnx"));
        Assert.Null(plugins.PluginRefusal("com.typewhisper.sherpa-onnx"));
        Assert.Contains("not allowed", plugins.PluginRefusal("com.typewhisper.webhook"));
        Assert.Contains("not allowed", plugins.TextTransferRefusal("com.typewhisper.webhook", ContentLocality.Local));
        Assert.Contains("not allowed", plugins.TranscriptionRefusal("com.typewhisper.groq", "com.typewhisper.groq", ContentLocality.Local));
    }

    [Fact]
    public void Downloads_FollowTheirKeys()
    {
        var policy = ManagedProviderPolicy.From(Snapshot([(ManagedPolicyKeys.AllowPluginDownloads, Number(0)), (ManagedPolicyKeys.AllowModelDownloads, Number(0))]));

        Assert.Contains("Plugin downloads are turned off", policy.PluginDownloadRefusal());
        Assert.Contains("Model downloads are turned off", policy.ModelDownloadRefusal());
        Assert.Null(policy.PluginRefusal("com.typewhisper.groq"));
    }

    [Fact]
    public void InvalidOrUnreadableKeys_RefuseOnlyTheirOperations()
    {
        var invalid = ManagedProviderPolicy.From(Snapshot([(ManagedPolicyKeys.AllowCloudTranscription, Number(5))]));
        Assert.Equal([ManagedPolicyKeys.AllowCloudTranscription], invalid.BlockedKeys);
        Assert.Contains("could not be read", invalid.TranscriptionRefusal("x", "x", ContentLocality.Local));
        Assert.Null(invalid.LlmRefusal("x", "x", ContentLocality.Cloud));
        Assert.Null(invalid.PluginDownloadRefusal());

        var unreadable = ManagedProviderPolicy.From(ManagedPolicyValidator.Validate(ManagedPolicyReading.Unreadable("IOException"), 1, DateTimeOffset.UtcNow));
        Assert.Equal(ManagedProviderPolicy.Keys.Count, unreadable.BlockedKeys.Count);
        Assert.NotNull(unreadable.PluginRefusal("x"));
        Assert.NotNull(unreadable.ModelDownloadRefusal());
        Assert.True(unreadable.IsManaged);
    }
}
