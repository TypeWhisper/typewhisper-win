using TypeWhisper.Plugin.Script;
using TypeWhisper.PluginSDK.Models;
namespace PortableMigration.Tests;
public sealed class ScriptBehaviorTests
{
    [WindowsFact]
    public async Task HostSettings_CreateDisabledScriptThenExecuteConfiguredChain()
    {
        using var f=new PortableFixture();using var p=new ScriptPlugin();await p.ActivateAsync(f.Host);
        await p.ExecuteSettingsActionAsync("add",default);var script=Assert.Single(p.Service!.Scripts);
        Assert.False(script.IsEnabled);
        await p.SaveTextSettingAsync(script.Id+":shell","powershell",default);
        await p.SaveTextSettingAsync(script.Id+":timeout","30",default);
        await p.SaveTextSettingAsync(script.Id+":command","[Console]::Out.Write([Console]::In.ReadToEnd().ToUpperInvariant())",default);
        Assert.Equal("hello",await p.ProcessAsync("hello",new(),default));
        await p.SaveTextSettingAsync(script.Id+":enabled","true",default);
        Assert.Equal("HELLO",await p.ProcessAsync("hello",new(),default));
        await p.DeactivateAsync();await p.ActivateAsync(f.Host);Assert.True(Assert.Single(p.Service!.Scripts).IsEnabled);
        await p.ExecuteSettingsActionAsync("remove:"+script.Id,default);Assert.Empty(p.Service.Scripts);await p.DeactivateAsync();
    }
    [WindowsFact]
    public async Task FailedScript_PreservesInputAndContinues()
    {
        using var f=new PortableFixture();using var p=new ScriptPlugin();await p.ActivateAsync(f.Host);
        p.Service!.AddScript(new(){Name="fail",Shell="cmd",Command="exit /b 7",IsEnabled=true});
        p.Service.AddScript(new(){Name="uppercase",Shell="powershell",TimeoutSeconds=30,Command="[Console]::Out.Write([Console]::In.ReadToEnd().ToUpperInvariant())",IsEnabled=true});
        Assert.Equal("HELLO",await p.ProcessAsync("hello",new(),default));await p.DeactivateAsync();
    }
    [WindowsFact]
    public async Task Cancellation_StopsProcess()
    {
        var runner=new ScriptProcessRunner();using var cancel=new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>runner.RunAsync(new(){Name="wait",Shell="powershell",Command="Start-Sleep -Seconds 30",TimeoutSeconds=40},"input",new(),cancel.Token));
    }
    [WindowsFact]
    public async Task Timeout_IsBoundedAndDoesNotReplaceInput()
    {
        var runner=new ScriptProcessRunner();var result=await runner.RunAsync(new(){Name="wait",Shell="powershell",Command="Start-Sleep -Seconds 30",TimeoutSeconds=1},"input",new(),default);
        Assert.False(result.IsSuccess);
    }
    [WindowsTheory]
    [InlineData("param([string]$Prefix = 'prefix:'); [Console]::Out.Write($Prefix + [Console]::In.ReadToEnd())", "prefix:Äpfel & Öl.")]
    [InlineData("using namespace System\n[Console]::Out.Write([Console]::In.ReadToEnd())", "Äpfel & Öl.")]
    public async Task PowerShellLeadingDeclarationsRemainValid(string command, string expected)
    {
        var result = await new ScriptProcessRunner().RunAsync(new() { Shell="powershell", Command=command, TimeoutSeconds=30 }, "Äpfel & Öl.", new(), default);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, result.Output);
    }

    [WindowsFact]
    public async Task CmdHandshakePreservesScriptInput()
    {
        var result = await new ScriptProcessRunner().RunAsync(new() { Shell="cmd", Command="findstr .", TimeoutSeconds=30 }, "first line\r\nsecond line\r\n", new(), default);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("first line\r\nsecond line\r\n", result.Output);
    }

    [WindowsFact]
    public async Task DescendantCannotOutliveExitedShell()
    {
        using var fixture = new PortableFixture();
        var marker = Path.Combine(fixture.Root, "child-pid.txt");
        var escaped = marker.Replace("'", "''");
        var command = "$p = [Diagnostics.Process]::Start('ping.exe', '-n 40 127.0.0.1'); [IO.File]::WriteAllText('" + escaped + "', [string]$p.Id)";
        await new ScriptProcessRunner().RunAsync(new() { Shell="powershell", Command=command, TimeoutSeconds=30 }, "", new(), default);
        Assert.True(File.Exists(marker));
        var id = int.Parse(File.ReadAllText(marker));
        try
        {
            using var child = System.Diagnostics.Process.GetProcessById(id);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(child.HasExited);
        }
        catch (ArgumentException) { /* The terminated child has already been reaped. */ }
    }

    [Theory]
    [InlineData("de-DE", "Skripte")]
    [InlineData("ja-JP", "スクリプト")]
    public async Task PortableHostWithoutLocalizationUsesPackagedCulture(string culture, string expected)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new(culture);
            using var fixture = new PortableFixture(); using var plugin = new ScriptPlugin();
            await plugin.ActivateAsync(fixture.Host);
            Assert.Equal(expected, plugin.TextSettings[0].Title);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task CorruptConfiguration_BlocksMutation()
    {
        using var f=new PortableFixture();Directory.CreateDirectory(f.Host.PluginDataDirectory);var path=Path.Combine(f.Host.PluginDataDirectory,"scripts.json");await File.WriteAllTextAsync(path,"broken");
        using var p=new ScriptPlugin();await p.ActivateAsync(f.Host);
        await Assert.ThrowsAnyAsync<Exception>(()=>p.ExecuteSettingsActionAsync("add",default));Assert.Equal("broken",await File.ReadAllTextAsync(path));await p.DeactivateAsync();
    }
}
