using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSDK.Portable.Tests;

public sealed class PortablePluginSettingsWriterTests
{
    [Fact]
    public async Task SavesChangedFieldsAndKeyTogetherWithoutRewritingUnchangedValues()
    {
        using var plugin = new Fixture();
        var result = await PortablePluginSettingsWriter.SaveAsync(plugin,
            new Dictionary<string,string> { ["first"]="new", ["second"]="old" }, "replacement", default);
        Assert.Null(result.Error); Assert.True(result.ApiKeySaved);
        Assert.Equal(["first"],result.SavedFields);
        Assert.Equal(["first:new","key:replacement"],plugin.Writes);
    }

    [Fact]
    public async Task InvalidChoicePreventsEveryWriteIncludingCredentialReplacement()
    {
        using var plugin = new Fixture();
        var result = await PortablePluginSettingsWriter.SaveAsync(plugin,
            new Dictionary<string,string> { ["first"]="new", ["second"]="invalid" }, "replacement", default);
        Assert.NotNull(result.Error); Assert.Empty(plugin.Writes); Assert.Empty(result.SavedFields); Assert.False(result.ApiKeySaved);
    }

    [Fact]
    public async Task PartialFailureIdentifiesSavedFieldsAndLeavesTheKeyUntouched()
    {
        using var plugin = new Fixture { FailField="second" };
        var result = await PortablePluginSettingsWriter.SaveAsync(plugin,
            new Dictionary<string,string> { ["first"]="new", ["second"]="new" }, "replacement", default);
        Assert.Contains("Some changes were saved",result.Error);
        Assert.Equal(["first"],result.SavedFields); Assert.False(result.ApiKeySaved);
        Assert.Equal(["first:new"],plugin.Writes);
    }

    [Fact]
    public async Task BlankKeyKeepsExistingCredentialAndCancellationDoesNotWrite()
    {
        using var plugin = new Fixture();
        var result = await PortablePluginSettingsWriter.SaveAsync(plugin,new Dictionary<string,string>(),"  ",default);
        Assert.Null(result.Error); Assert.False(result.ApiKeySaved); Assert.Empty(plugin.Writes);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        result = await PortablePluginSettingsWriter.SaveAsync(plugin,new Dictionary<string,string> { ["first"]="new" },"replacement",cancelled.Token);
        Assert.NotNull(result.Error); Assert.Empty(plugin.Writes);
    }

    [Fact]
    public async Task FailedKeyWriteDoesNotExposeCredentialOrExceptionDetails()
    {
        using var plugin = new Fixture { FailKey=true };
        var result = await PortablePluginSettingsWriter.SaveAsync(plugin,
            new Dictionary<string,string> { ["first"]="new" },"private-value",default);
        Assert.Equal(["first"],result.SavedFields); Assert.False(result.ApiKeySaved);
        Assert.Contains("API key",result.Error); Assert.DoesNotContain("private-value",result.Error);
    }

    private sealed class Fixture : ITypeWhisperPlugin, IPluginTextSettings, IApiKeyPlugin
    {
        public List<string> Writes { get; } = [];
        public string? FailField { get; init; }
        public bool FailKey { get; init; }
        public string PluginId => "fixture";
        public string PluginName => "Fixture";
        public string PluginVersion => "1.0.0";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginTextSetting> TextSettings => [new("first","First","","old",20),new("second","Second","","old") { Choices=[new("old","Old"),new("new","New")] }];
        public Task SaveTextSettingAsync(string id,string value,CancellationToken ct)
        { if(id==FailField) throw new IOException("Fixture write failure"); Writes.Add(id+":"+value); return Task.CompletedTask; }
        public Task SetApiKeyAsync(string key)
        { if(FailKey) throw new IOException(key); Writes.Add("key:"+key); return Task.CompletedTask; }
        public Task ValidateConfigurationAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
