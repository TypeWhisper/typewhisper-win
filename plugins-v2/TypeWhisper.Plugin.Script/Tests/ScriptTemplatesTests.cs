using TypeWhisper.Plugin.Script;
using TypeWhisper.PluginSDK.Models;
namespace PortableMigration.Tests;

public sealed class ScriptTemplatesTests
{
    public static TheoryData<string, string, string> Cases => new()
    {
        { "uppercase", "Äpfel & Öl", "ÄPFEL & ÖL" },
        { "lowercase", "ÄPFEL & ÖL", "äpfel & öl" },
        { "trim", " \tÄpfel\r\n ", "Äpfel" },
        { "clean-spacing", "  Äpfel\t  und  Öl \n\n nächste   Zeile  ", "Äpfel und Öl\r\n\r\nnächste Zeile" },
        { "bullets", " Äpfel \n\n- Öl\nBrot", "- Äpfel\r\n- Öl\r\n- Brot" },
        { "checklist", " Äpfel \n- Öl\n- [x] Brot", "- [ ] Äpfel\r\n- [ ] Öl\r\n- [x] Brot" },
        { "quote", "Äpfel\n\n> Öl", "> Äpfel\r\n> \r\n> Öl" },
        { "json", "Er sagt: \"Öl\"\nPfad\\Datei", "\"Er sagt: \\\"Öl\\\"\\nPfad\\\\Datei\"" },
        { "url", "Äpfel & Öl", "%C3%84pfel%20%26%20%C3%96l" }
    };

    [WindowsTheory]
    [MemberData(nameof(Cases))]
    public async Task TemplatesTransformUnicodeAndPreserveTheirDocumentedStructure(string id, string input, string expected)
    {
        var template = ScriptTemplates.All.Single(t => t.Id == id);
        // Functional assertions should tolerate cold PowerShell startup on shared CI runners.
        var script = template.Create(false) with { TimeoutSeconds = 30 };
        Assert.False(script.IsEnabled);
        var result = await new ScriptProcessRunner().RunAsync(script, input, new(), default);
        Assert.True(result.IsSuccess, result.Status.ToString());
        Assert.Equal(expected, result.Output);
    }

    [Fact]
    public async Task AllTemplatesCanBeAddedDisabledWithoutChangingExistingScripts()
    {
        using var f = new PortableFixture(); using var p = new ScriptPlugin(); await p.ActivateAsync(f.Host);
        foreach (var template in ScriptTemplates.All)
        {
            await p.ExecuteProfileActionAsync(p.ConnectionIdentity, "add-template", new Dictionary<string, string> { ["template"] = template.Id }, null, default);
            Assert.All(p.Service!.Scripts, s => Assert.False(s.IsEnabled));
        }
        Assert.Equal(9, p.Service!.Scripts.Count);
        Assert.Equal(9, p.Service.Scripts.Select(s => s.Id).Distinct().Count());
        Assert.Equal("unchanged", await p.ProcessAsync("unchanged", new(), default));
        await p.DeactivateAsync(); await p.ActivateAsync(f.Host);
        Assert.Equal(9, p.Service!.Scripts.Count);
    }

    [Fact]
    public async Task InvalidBatchCannotPartiallySaveAndStaleProfilesCannotModifyAnotherScript()
    {
        using var f = new PortableFixture(); using var p = new ScriptPlugin(); await p.ActivateAsync(f.Host);
        await p.ExecuteSettingsActionAsync("add", default);
        var first = Assert.Single(p.Service!.Scripts);
        var path = Path.Combine(f.Host.PluginDataDirectory, "scripts.json");
        var before = await File.ReadAllTextAsync(path);
        await Assert.ThrowsAsync<ArgumentException>(() => p.SaveProfileSettingsAsync(first.Id.ToString(),
            new Dictionary<string, string> { [first.Id + ":name"] = "Changed", [first.Id + ":timeout"] = "301" }, null, default));
        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.Equal(first, Assert.Single(p.Service.Scripts));
        await p.ExecuteSettingsActionAsync("add", default);
        await Assert.ThrowsAsync<ArgumentException>(() => p.SaveProfileSettingsAsync(first.Id.ToString(),
            new Dictionary<string, string> { [first.Id + ":name"] = "Wrong" }, null, default));
        Assert.Equal(first, p.Service.Scripts[0]);
    }

    [WindowsFact]
    public async Task DraftTestDoesNotSaveOrEnableAndBatchSaveSurvivesRestart()
    {
        using var f = new PortableFixture(); using var p = new ScriptPlugin(); await p.ActivateAsync(f.Host);
        await p.ExecuteSettingsActionAsync("add", default);
        var script = Assert.Single(p.Service!.Scripts);
        var path = Path.Combine(f.Host.PluginDataDirectory, "scripts.json");
        var before = await File.ReadAllTextAsync(path);
        var draft = new Dictionary<string, string>
        {
            [script.Id + ":name"] = "Unicode test",
            [script.Id + ":command"] = ScriptTemplates.All.Single(t => t.Id == "uppercase").Command,
            [script.Id + ":shell"] = "powershell",
            [script.Id + ":timeout"] = "30",
            [script.Id + ":enabled"] = "false"
        };
        var result = await p.ExecuteProfileActionAsync(script.Id.ToString(), "test:" + script.Id, draft, null, default);
        Assert.Contains("ÄPFEL & ÖL", result.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.Equal(script, Assert.Single(p.Service.Scripts));
        await p.SaveProfileSettingsAsync(script.Id.ToString(), draft, null, default);
        await p.DeactivateAsync(); await p.ActivateAsync(f.Host);
        var saved = Assert.Single(p.Service!.Scripts);
        Assert.Equal("Unicode test", saved.Name); Assert.Equal(30, saved.TimeoutSeconds); Assert.False(saved.IsEnabled);
    }

    [Theory]
    [InlineData("ja-JP", "Markdown\u30c1\u30a7\u30c3\u30af\u30ea\u30b9\u30c8")]
    [InlineData("ru-RU", "\u0421\u043f\u0438\u0441\u043e\u043a \u0437\u0430\u0434\u0430\u0447 Markdown")]
    [InlineData("zh-CN", "Markdown \u4efb\u52a1\u5217\u8868")]
    public async Task TemplatesUsePackagedNamesForChoicesAndBothAddActions(string locale, string expected)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new(locale);
            using var fixture = new PortableFixture(); using var plugin = new ScriptPlugin();
            await plugin.ActivateAsync(fixture.Host);
            var choices = plugin.TextSettings.Single(f => f.Id == "template").Choices;
            Assert.All(choices, choice => Assert.NotEqual(ScriptTemplates.All.Single(t => t.Id == choice.Value).EnglishName, choice.Title));
            Assert.Equal(expected, choices.Single(c => c.Value == "checklist").Title);
            await plugin.SaveProfileSettingsAsync("none", new Dictionary<string,string> { ["template"] = "checklist" }, null, default);
            await plugin.ExecuteSettingsActionAsync("add-template", default);
            Assert.Equal(expected, Assert.Single(plugin.Service!.Scripts).Name);
            await plugin.ExecuteProfileActionAsync(plugin.ConnectionIdentity, "add-template", new Dictionary<string,string> { ["template"] = "checklist" }, null, default);
            Assert.Equal(2, plugin.Service.Scripts.Count);
            Assert.All(plugin.Service.Scripts, s => Assert.Equal(expected, s.Name));
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task TemplateSelectionWithoutScriptsCanBeSavedAndAdded()
    {
        using var f = new PortableFixture(); using var p = new ScriptPlugin(); await p.ActivateAsync(f.Host);
        await p.SaveProfileSettingsAsync("none", new Dictionary<string, string> { ["template"] = "checklist" }, null, default);
        Assert.Empty(p.Service!.Scripts);
        await p.ExecuteSettingsActionAsync("add-template", default);
        Assert.Equal("Markdown checklist", Assert.Single(p.Service.Scripts).Name);
    }
}

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires a Windows shell."; }
}
internal sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires a Windows shell."; }
}

