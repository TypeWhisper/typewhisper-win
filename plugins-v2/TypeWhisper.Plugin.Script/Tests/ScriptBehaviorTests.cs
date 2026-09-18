using TypeWhisper.Plugin.Script;
using TypeWhisper.PluginSDK.Models;
namespace PortableMigration.Tests;
public sealed class ScriptBehaviorTests
{
    [Fact]
    public async Task HostSettings_CreateDisabledScriptThenExecuteConfiguredChain()
    {
        using var f=new PortableFixture();using var p=new ScriptPlugin();await p.ActivateAsync(f.Host);
        await p.ExecuteSettingsActionAsync("add",default);var script=Assert.Single(p.Service!.Scripts);
        Assert.False(script.IsEnabled);
        await p.SaveTextSettingAsync(script.Id+":shell","powershell",default);
        await p.SaveTextSettingAsync(script.Id+":command","[Console]::Out.Write([Console]::In.ReadToEnd().ToUpperInvariant())",default);
        Assert.Equal("hello",await p.ProcessAsync("hello",new(),default));
        await p.SaveTextSettingAsync(script.Id+":enabled","true",default);
        Assert.Equal("HELLO",await p.ProcessAsync("hello",new(),default));
        await p.DeactivateAsync();await p.ActivateAsync(f.Host);Assert.True(Assert.Single(p.Service!.Scripts).IsEnabled);
        await p.ExecuteSettingsActionAsync("remove:"+script.Id,default);Assert.Empty(p.Service.Scripts);await p.DeactivateAsync();
    }
    [Fact]
    public async Task FailedScript_PreservesInputAndContinues()
    {
        using var f=new PortableFixture();using var p=new ScriptPlugin();await p.ActivateAsync(f.Host);
        p.Service!.AddScript(new(){Name="fail",Shell="cmd",Command="exit /b 7",IsEnabled=true});
        p.Service.AddScript(new(){Name="uppercase",Shell="powershell",Command="[Console]::Out.Write([Console]::In.ReadToEnd().ToUpperInvariant())",IsEnabled=true});
        Assert.Equal("HELLO",await p.ProcessAsync("hello",new(),default));await p.DeactivateAsync();
    }
    [Fact]
    public async Task Cancellation_StopsProcess()
    {
        var runner=new ScriptProcessRunner();using var cancel=new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>runner.RunAsync(new(){Name="wait",Shell="powershell",Command="Start-Sleep -Seconds 30",TimeoutSeconds=40},"input",new(),cancel.Token));
    }
    [Fact]
    public async Task Timeout_IsBoundedAndDoesNotReplaceInput()
    {
        var runner=new ScriptProcessRunner();var result=await runner.RunAsync(new(){Name="wait",Shell="powershell",Command="Start-Sleep -Seconds 30",TimeoutSeconds=1},"input",new(),default);
        Assert.False(result.IsSuccess);
    }
    [Fact]
    public async Task CorruptConfiguration_BlocksMutation()
    {
        using var f=new PortableFixture();Directory.CreateDirectory(f.Host.PluginDataDirectory);var path=Path.Combine(f.Host.PluginDataDirectory,"scripts.json");await File.WriteAllTextAsync(path,"broken");
        using var p=new ScriptPlugin();await p.ActivateAsync(f.Host);
        await Assert.ThrowsAnyAsync<Exception>(()=>p.ExecuteSettingsActionAsync("add",default));Assert.Equal("broken",await File.ReadAllTextAsync(path));await p.DeactivateAsync();
    }
}
