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
    public async Task LargeUnicodePowerShellSourceDoesNotExpandTheCommandLine()
    {
        var command = "#" + new string('界', 24000) + "\n[Console]::Out.Write([Console]::In.ReadToEnd())";
        var result = await new ScriptProcessRunner().RunAsync(new() { Shell="powershell", Command=command, TimeoutSeconds=30 }, "Äpfel & Öl.", new(), default);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("Äpfel & Öl.", result.Output);
    }

    [WindowsTheory]
    [InlineData("cmd")]
    [InlineData("legacy-unknown-shell")]
    public async Task CmdHandshakePreservesScriptInput(string shell)
    {
        var result = await new ScriptProcessRunner().RunAsync(new() { Shell=shell, Command="findstr .", TimeoutSeconds=30 }, "first line\r\nsecond line\r\n", new(), default);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("first line\r\nsecond line\r\n", result.Output);
    }

    [WindowsFact]
    public async Task DescendantCannotOutliveExitedShell()
    {
        using var fixture = new PortableFixture();
        var marker = Path.Combine(fixture.Root, "child-pid.txt");
        var escaped = marker.Replace("'", "''");
        var command = "$p = Start-Process cmd.exe -ArgumentList '/d /c ping -n 40 127.0.0.1 >nul' -NoNewWindow -PassThru; [IO.File]::WriteAllText('" + escaped + "', [string]$p.Id)";
        var result = await new ScriptProcessRunner().RunAsync(new() { Shell="powershell", Command=command, TimeoutSeconds=30 }, "", new(), default);
        Assert.True(result.IsSuccess, result.Error);
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
            if (culture == "ja-JP")
            {
                await plugin.ExecuteSettingsActionAsync("add", default);
                Assert.Contains(plugin.TextSettings, field => field.Id.EndsWith(":timeout") && field.Title == "タイムアウト（秒）");
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [WindowsFact]
    public async Task CmdBoundaryLeavesRoomForItsStartupWrapper()
    {
        const string prefix = "echo OK & rem ";
        var command = prefix + new string('x', ScriptDefaults.MaximumCmdCommandLength - prefix.Length);
        var result = await new ScriptProcessRunner().RunAsync(new() { Shell="cmd", Command=command, TimeoutSeconds=30 }, "", new(), default);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("OK", result.Output.Trim());
    }

    [Fact]
    public async Task OversizedCmdDraftIsRejectedBeforeSaveOrLaunch()
    {
        using var fixture = new PortableFixture(); using var plugin = new ScriptPlugin();
        await plugin.ActivateAsync(fixture.Host);
        await plugin.ExecuteSettingsActionAsync("add", default);
        var script = Assert.Single(plugin.Service!.Scripts);
        var command = "rem " + new string('x', 32764);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveProfileSettingsAsync(script.Id.ToString(),
            new Dictionary<string,string> { [script.Id + ":shell"] = "cmd", [script.Id + ":command"] = command }, null, default));
        Assert.Equal(script, Assert.Single(plugin.Service.Scripts));
        var result = await new ScriptProcessRunner().RunAsync(script with { Shell="cmd", Command=command }, "keep", new(), default);
        Assert.Equal(ScriptExecutionStatus.Failed, result.Status);
        Assert.Contains("7900", result.Error);
    }

    [Fact]
    public async Task IncompleteOrExplicitlyEnabledBlankEntriesCannotEraseDictation()
    {
        using var fixture = new PortableFixture();
        Directory.CreateDirectory(fixture.Host.PluginDataDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.Host.PluginDataDirectory, "scripts.json"),
            "[{\"name\":\"incomplete\"},{\"name\":\"blank\",\"isEnabled\":true,\"command\":\" \"}]");
        using var plugin = new ScriptPlugin(); await plugin.ActivateAsync(fixture.Host);
        Assert.Equal(2, plugin.Service!.Scripts.Count);
        Assert.All(plugin.Service.Scripts, script => Assert.False(script.IsEnabled));
        Assert.Equal("keep this dictation", await plugin.ProcessAsync("keep this dictation", new(), default));
    }

    [WindowsFact]
    public async Task FailedDraftTestIncludesBoundedDiagnosticsAndExitCode()
    {
        using var fixture = new PortableFixture(); using var plugin = new ScriptPlugin();
        await plugin.ActivateAsync(fixture.Host); await plugin.ExecuteSettingsActionAsync("add", default);
        var script = Assert.Single(plugin.Service!.Scripts);
        var result = await plugin.ExecuteProfileActionAsync(script.Id.ToString(), "test:" + script.Id,
            new Dictionary<string,string> { [script.Id + ":shell"] = "powershell", [script.Id + ":timeout"] = "30",
                [script.Id + ":command"] = "[Console]::Error.Write(('diagnostic' * 300)); exit 7" }, null, default);
        Assert.Contains("7", result.Message);
        Assert.Contains("diagnostic", result.Message);
        Assert.InRange(result.Message.Length, 2000, 2150);
        Assert.Equal(script, Assert.Single(plugin.Service.Scripts));
    }

    [WindowsTheory]
    [InlineData("cmd")]
    [InlineData("legacy-unknown-shell")]
    public async Task CommandPromptPreservesUnicodeOutputAndDiagnostics(string shell)
    {
        var result = await new ScriptProcessRunner().RunAsync(new() { Shell=shell,
            Command="echo Äpfel ^& Öl & >&2 echo Grüße", TimeoutSeconds=30 }, "", new(), default);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("Äpfel & Öl", result.Output.Trim());
        Assert.Equal("Grüße", result.Error.Trim());
    }

    [WindowsFact]
    public async Task CommandPromptPreservesQuotedOperatorsAndLiteralExclamationMarks()
    {
        var result = await new ScriptProcessRunner().RunAsync(new() { Shell="cmd",
            Command="echo \"Äpfel & Öl\" & echo !literal!", TimeoutSeconds=30 }, "", new(), default);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(new[] { "\"Äpfel & Öl\"", "!literal!" }, result.Output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()));
    }

    [Fact]
    public async Task RecognizedPersistedShellNamesAreCanonicalized()
    {
        using var fixture = new PortableFixture();
        Directory.CreateDirectory(fixture.Host.PluginDataDirectory);
        var path = Path.Combine(fixture.Host.PluginDataDirectory, "scripts.json");
        await File.WriteAllTextAsync(path, """[{"shell":"PowerShell","command":"echo test"},{"shell":"CMD","command":"echo test"},{"shell":"PwSh","command":"echo test"}]""");
        using var plugin = new ScriptPlugin(); await plugin.ActivateAsync(fixture.Host);
        Assert.Equal(new[] { "powershell", "cmd", "pwsh" }, plugin.Service!.Scripts.Select(script => script.Shell));
    }

    [Fact]
    public async Task DuplicateIdsDisableConfigurationWithoutOverwritingIt()
    {
        using var fixture = new PortableFixture();
        Directory.CreateDirectory(fixture.Host.PluginDataDirectory);
        var path = Path.Combine(fixture.Host.PluginDataDirectory, "scripts.json");
        var id = Guid.NewGuid();
        var json = System.Text.Json.JsonSerializer.Serialize(new[] {
            new { id, name = "first", command = "echo changed", isEnabled = true },
            new { id, name = "copy", command = "echo changed", isEnabled = true }
        });
        await File.WriteAllTextAsync(path, json);
        using var plugin = new ScriptPlugin(); await plugin.ActivateAsync(fixture.Host);
        Assert.True(plugin.Service!.IsReadOnly);
        Assert.Empty(plugin.Service.Scripts);
        Assert.Contains("Duplicate", plugin.Service.LoadError);
        Assert.Null(plugin.AddProfileActionId);
        Assert.Contains(plugin.TextSettings, field => field.Id == "configuration_error");
        Assert.Equal("original dictation", await plugin.ProcessAsync("original dictation", new(), default));
        Assert.Equal(json, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CorruptConfiguration_BlocksMutation()
    {
        using var f=new PortableFixture();Directory.CreateDirectory(f.Host.PluginDataDirectory);var path=Path.Combine(f.Host.PluginDataDirectory,"scripts.json");await File.WriteAllTextAsync(path,"broken");
        using var p=new ScriptPlugin();await p.ActivateAsync(f.Host);
        Assert.Null(p.AddProfileActionId); Assert.Null(p.RemoveProfileActionId); Assert.Empty(p.SettingsActions);
        Assert.Contains(p.TextSettings, field => field.Id == "configuration_error" && field.Value == "readonly");
        await Assert.ThrowsAnyAsync<Exception>(()=>p.ExecuteSettingsActionAsync("add",default));Assert.Equal("broken",await File.ReadAllTextAsync(path));await p.DeactivateAsync();
    }
}
