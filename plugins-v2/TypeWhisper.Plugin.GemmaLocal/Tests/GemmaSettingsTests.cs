using TypeWhisper.Plugin.GemmaLocal;
namespace PortableMigration.Tests;
public sealed class GemmaSettingsTests
{
    [Fact]
    public async Task ModelSelection_PersistsWithoutImplicitDownloadOrLoad()
    {
        using var f=new PortableFixture();using var p=new GemmaLocalPlugin();await p.ActivateAsync(f.Host);
        Assert.All(p.SupportedModels,m=>Assert.StartsWith("gemma3-",m.Id));
        await p.SaveTextSettingAsync("model","gemma3-12b-q4",default);Assert.False(p.IsAvailable);await p.DeactivateAsync();await p.ActivateAsync(f.Host);
        Assert.Equal("gemma3-12b-q4",p.SelectedModelId);Assert.False(p.IsAvailable);
        await Assert.ThrowsAsync<FileNotFoundException>(()=>p.LoadModelAsync("gemma3-12b-q4",default));await p.DeactivateAsync();
    }
    [Theory]
    [InlineData("../escape")][InlineData("")][InlineData("unknown")]
    public async Task InvalidModel_CannotChangeAssetPath(string value)
    {
        using var f=new PortableFixture();using var p=new GemmaLocalPlugin();await p.ActivateAsync(f.Host);
        await Assert.ThrowsAsync<ArgumentException>(()=>p.SaveTextSettingAsync("model",value,default));await p.DeactivateAsync();
    }
}
