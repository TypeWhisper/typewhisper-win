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
    public async Task RecallRanksRelatedFactsWithoutReturningUnrelatedEntries()
    {
        using var f = new PortableFixture(); using var plugin = new FileMemoryPlugin(); await plugin.ActivateAsync(f.Host);
        await plugin.StoreAsync("Projekt Aurora verwendet britisches Englisch.");
        await plugin.StoreAsync("Der Urlaub beginnt im Oktober.");
        Assert.Equal("Projekt Aurora verwendet britisches Englisch.", Assert.Single(await plugin.SearchAsync("Bitte schreibe einen Text über Aurora.")));
        Assert.Empty(await plugin.SearchAsync("Zebras"));
        Assert.Empty(await plugin.SearchAsync("the and"));
        await plugin.DeactivateAsync();
    }
    [Theory]
    [InlineData("Hi")]
    [InlineData("ok")]
    [InlineData("a")]
    [InlineData("the")]
    [InlineData(".")]
    public async Task ShortOrUnindexedQueries_DoNotMatchUnrelatedSubstrings(string query)
    {
        using var f = new PortableFixture(); using var plugin = new FileMemoryPlugin(); await plugin.ActivateAsync(f.Host);
        await plugin.StoreAsync("This project uses the okay template.");
        Assert.Empty(await plugin.SearchAsync(query));
        await plugin.DeactivateAsync();
    }
    [Fact]
    public async Task ShortQueries_StillMatchWholeIndexedTerms()
    {
        using var f = new PortableFixture(); using var plugin = new FileMemoryPlugin(); await plugin.ActivateAsync(f.Host);
        await plugin.StoreAsync("UK spelling for Aurora.");
        Assert.Equal("UK spelling for Aurora.", Assert.Single(await plugin.SearchAsync(" UK ")));
        await plugin.DeactivateAsync();
    }
    [Fact]
    public async Task CorruptFile_IsNotSilentlyOverwritten()
    {
        using var f=new PortableFixture(); Directory.CreateDirectory(f.Host.PluginDataDirectory);
        var file=Path.Combine(f.Host.PluginDataDirectory,"memories.json"); await File.WriteAllTextAsync(file,"broken json");
        using var plugin=new FileMemoryPlugin(); await plugin.ActivateAsync(f.Host);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>plugin.StoreAsync("entry",default));
        Assert.Equal("broken json",await File.ReadAllTextAsync(file)); await plugin.DeactivateAsync();
    }
    [Fact]
    public async Task WorkflowActionAndHostSettings_StoreAndReadMemory()
    {
        using var f=new PortableFixture(); using var plugin=new FileMemoryPlugin(); await plugin.ActivateAsync(f.Host);
        IActionPlugin action=plugin;
        Assert.True((await action.ExecuteAsync("workflow memory",new(null,null,null,null,null),default)).Success);
        var entry = plugin.TextSettings.Single(f => f.Id == plugin.ProfileSelectorId).Choices.Single(c => c.Title == "workflow memory");
        await plugin.SaveTextSettingAsync(plugin.ProfileSelectorId, entry.Value, default);
        var result = await plugin.ExecuteProfileActionAsync(entry.Value, "search", new Dictionary<string,string> { ["query"] = "workflow" }, null, default);
        Assert.Contains("workflow memory", result.Message);
        await plugin.ExecuteSettingsActionAsync(plugin.RemoveProfileActionId!,default); Assert.Equal(0,await plugin.CountAsync(default));
        await plugin.DeactivateAsync();
    }
    [Fact]
    public async Task CancelledStore_DoesNotWrite()
    {
        using var f=new PortableFixture();using var plugin=new FileMemoryPlugin();await plugin.ActivateAsync(f.Host);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>plugin.StoreAsync("cancelled",new CancellationToken(true)));
        Assert.Equal(0,await plugin.CountAsync(default));await plugin.DeactivateAsync();
    }
}
