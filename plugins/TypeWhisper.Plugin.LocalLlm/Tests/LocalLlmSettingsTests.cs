using TypeWhisper.Plugin.LocalLlm;
namespace PortableMigration.Tests;
public sealed class LocalLlmSettingsTests
{
    [Fact]
    public async Task ModelSelection_PersistsWithoutImplicitDownloadOrLoad()
    {
        using var f=new PortableFixture();using var p=new LocalLlmPlugin();await p.ActivateAsync(f.Host);
        Assert.Equal(["gemma-4-e2b-it-q4","gemma-4-e4b-it-q4","gemma-4-e4b-it-q8","gemma-4-26b-a4b-it-q4","qwen3.5-2b-q4","lfm2.5-2.6b-q4"],p.LocalModels.Select(m=>m.Model.Id));Assert.Empty(p.SupportedModels);
        await p.SaveTextSettingAsync("model","qwen3.5-2b-q4",default);Assert.False(p.IsAvailable);await p.DeactivateAsync();await p.ActivateAsync(f.Host);
        Assert.Equal("qwen3.5-2b-q4",p.SelectedModelId);Assert.False(p.IsAvailable);
        await Assert.ThrowsAsync<FileNotFoundException>(()=>p.LoadModelAsync("qwen3.5-2b-q4",default));await p.DeactivateAsync();
    }
    [Theory]
    [InlineData("../escape")][InlineData("")][InlineData("unknown")]
    public async Task InvalidModel_CannotChangeAssetPath(string value)
    {
        using var f=new PortableFixture();using var p=new LocalLlmPlugin();await p.ActivateAsync(f.Host);
        await Assert.ThrowsAsync<ArgumentException>(()=>p.SaveTextSettingAsync("model",value,default));await p.DeactivateAsync();
    }
}
