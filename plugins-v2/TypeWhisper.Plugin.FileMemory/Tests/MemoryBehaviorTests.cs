using System.Net;
using System.Text;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Plugin.FileMemory;
namespace PortableMigration.Tests;
public sealed class MemoryBehaviorTests
{
    [Fact]
    public async Task StoreSearchDedupDeleteAndRestart_UseOnlyPluginDirectory()
    {
        using var f=new PortableFixture();
        using(var plugin=new FileMemoryPlugin())
        {
            await plugin.ActivateAsync(f.Host);
            await plugin.StoreAsync("Marco likes tea",default); await plugin.StoreAsync("Marco likes tea",default);
            await plugin.StoreAsync("Travel plans",default);
            Assert.Equal(2,await plugin.CountAsync(default));
            Assert.Contains("Marco likes tea",await plugin.SearchAsync("tea",5));
            await plugin.DeactivateAsync();
        }
        using var restart=new FileMemoryPlugin(); await restart.ActivateAsync(f.Host);
        Assert.Equal(2,await restart.CountAsync(default));
        await restart.DeleteAsync("Travel plans",default); Assert.Single(await restart.GetAllAsync(default));
        await restart.ClearAllAsync(default); Assert.Equal(0,await restart.CountAsync(default));
        await restart.DeactivateAsync();
    }
    [Fact]
    public async Task CorruptFile_IsNotSilentlyOverwritten()
    {
        using var f=new PortableFixture(); Directory.CreateDirectory(f.Host.PluginDataDirectory);
        var file=Path.Combine(f.Host.PluginDataDirectory,"memories.json"); await File.WriteAllTextAsync(file,"broken json");
        using var plugin=new FileMemoryPlugin(); await plugin.ActivateAsync(f.Host);
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(()=>plugin.StoreAsync("entry",default));
        Assert.Equal("broken json",await File.ReadAllTextAsync(file)); await plugin.DeactivateAsync();
    }
    [Fact]
    public async Task WorkflowActionAndHostSettings_StoreAndReadMemory()
    {
        using var f=new PortableFixture(); using var plugin=new FileMemoryPlugin(); await plugin.ActivateAsync(f.Host);
        IActionPlugin action=plugin;
        Assert.True((await action.ExecuteAsync("workflow memory",new(null,null,null,null,null),default)).Success);
        await plugin.SaveTextSettingAsync("query","workflow",default);
        Assert.Contains("workflow memory",await plugin.ExecuteSettingsActionAsync("search",default));
        await plugin.SaveTextSettingAsync("entry","workflow memory",default);
        await plugin.ExecuteSettingsActionAsync("delete",default); Assert.Equal(0,await plugin.CountAsync(default));
        await plugin.DeactivateAsync();
    }
    [Fact]
    public async Task CancelledStore_DoesNotWrite()
    {
        using var f=new PortableFixture();using var plugin=new FileMemoryPlugin();await plugin.ActivateAsync(f.Host);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>plugin.StoreAsync("cancelled",new CancellationToken(true)));
        Assert.Equal(0,await plugin.CountAsync(default));await plugin.DeactivateAsync();
    }
    private sealed class EmbeddingTransport:HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal("https://api.openai.com/v1/embeddings",request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer fixture",request.Headers.Authorization!.ToString());
            var body=await request.Content!.ReadAsStringAsync(ct); Assert.Contains("text-embedding-3-small",body);
            return new(HttpStatusCode.OK){Content=new StringContent("{\"data\":[{\"embedding\":[1,0,0]}]}",Encoding.UTF8,"application/json")};
        }
    }
}
