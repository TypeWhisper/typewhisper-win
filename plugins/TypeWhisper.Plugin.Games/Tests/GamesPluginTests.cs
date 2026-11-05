using TypeWhisper.Plugin.Games;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public sealed class GamesPluginTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "typewhisper-games-" + Guid.NewGuid().ToString("N"));
    private readonly TestHost _host;

    public GamesPluginTests()
    {
        Directory.CreateDirectory(_root);
        _host = new(Path.Join(_root, "data"));
    }

    [Fact]
    public void TargetProcessNames_ListWorldOfWarcraft()
    {
        Assert.Contains("Wow", new GamesPlugin().TargetProcessNames);
        Assert.Contains("WowClassic", new GamesPlugin().TargetProcessNames);
    }

    [Fact]
    public async Task Prepare_LeavesOtherAppsToTheHost()
    {
        var plugin = await ActiveAsync();

        Assert.Null(await plugin.PrepareDictationAsync(new(Guid.NewGuid(), "notepad", @"C:\Windows\notepad.exe"), default));
    }

    [Fact]
    public async Task Prepare_ReadsTheAddOnPage_AndNamesTheLlmForIt()
    {
        var retail = Version("_retail_", addOn: true, saved: "TypeWhisperDB = {\n[\"translateTo\"] = \"German\",\n}");
        _host.Llm = new("Groq · Llama 3.3", true);
        var plugin = await ActiveAsync();

        var plan = await plugin.PrepareDictationAsync(new(Guid.NewGuid(), "Wow", Path.Join(retail, "Wow.exe")), default);

        Assert.Equal("World of Warcraft", plan!.AppName);
        Assert.Equal("German", plan.TranslateTo);
        Assert.Contains("llm = \"Groq · Llama 3.3\"", File.ReadAllText(Path.Join(retail, "Interface", "AddOns", "TypeWhisper", "Status.lua")));
    }

    [Fact]
    public async Task Steps_DriveTheCastBar_OnlyWithTheAddOnAndItsBar()
    {
        var plugin = await ActiveAsync();
        var input = new FakeInput();
        var id = Guid.NewGuid();
        await plugin.PrepareDictationAsync(new(id, "Wow", Path.Join(Version("_retail_", addOn: true), "Wow.exe")), default);

        Assert.NotNull(plugin.OnDictationPhaseChanged(id, DictationTargetPhase.Recording, input));
        Assert.Null(plugin.OnDictationPhaseChanged(Guid.NewGuid(), DictationTargetPhase.Transcribing, input));
        Assert.NotNull(plugin.OnDictationPhaseChanged(id, DictationTargetPhase.Transcribing, input));
        Assert.Equal(DateTimeOffset.MaxValue, plugin.OnDictationPhaseChanged(id, DictationTargetPhase.Delivered, input));
        Assert.Equal(["signal:11+10+77", "signal:11+10+78"], input.Calls);

        var withoutBar = new FakeInput();
        var classic = Version("_classic_", addOn: true, saved: "TypeWhisperDB = {\n[\"showBar\"] = false,\n}");
        await plugin.PrepareDictationAsync(new(id, "WowClassic", Path.Join(classic, "Wow.exe")), default);
        Assert.Null(plugin.OnDictationPhaseChanged(id, DictationTargetPhase.Recording, withoutBar));

        await plugin.PrepareDictationAsync(new(id, "Wow", Path.Join(Version("_ptr_", addOn: false), "Wow.exe")), default);
        Assert.Null(plugin.OnDictationPhaseChanged(id, DictationTargetPhase.Recording, withoutBar));
        Assert.Empty(withoutBar.Calls);
    }

    [Fact]
    public async Task Deliver_OpensTheChatWithTheAddOnPastesAndSends()
    {
        var plugin = await ActiveAsync();
        var input = new FakeInput();
        var id = Guid.NewGuid();
        await plugin.PrepareDictationAsync(new(id, "Wow", Path.Join(Version("_retail_", addOn: true), "Wow.exe")), default);

        var result = await plugin.DeliverDictationAsync(id, "gg wp", input, default);

        Assert.Equal(new DictationDeliveryResult(1, 1), result);
        Assert.True(result.Completed);
        Assert.Equal(["press:11+10+7A", "paste:gg wp", "press:0D"], input.Calls);
    }

    [Fact]
    public async Task Deliver_WithoutTheAddOn_OpensTheChatWithEnter()
    {
        var plugin = await ActiveAsync();
        var input = new FakeInput();
        var id = Guid.NewGuid();
        await plugin.PrepareDictationAsync(new(id, "Wow", Path.Join(Version("_retail_", addOn: false), "Wow.exe")), default);

        await plugin.DeliverDictationAsync(id, "gg wp", input, default);

        Assert.Equal(["press:0D", "paste:gg wp", "press:0D"], input.Calls);
    }

    [Fact]
    public async Task Deliver_ReportsWhatStoppedIt()
    {
        var plugin = await ActiveAsync();
        var id = Guid.NewGuid();
        await plugin.PrepareDictationAsync(new(id, "Wow", Path.Join(Version("_retail_", addOn: true, saved: "TypeWhisperDB = {\n[\"send\"] = false,\n}"), "Wow.exe")), default);

        // Without sending, two messages would land in the one chat field, so nothing is inserted.
        Assert.Equal(new DictationDeliveryResult(2, 0, "too-long"), await plugin.DeliverDictationAsync(id, new string('a', 200) + " " + new string('b', 200), new FakeInput(), default));
        Assert.Equal(new DictationDeliveryResult(1, 0, "open"), await plugin.DeliverDictationAsync(id, "hi", new FakeInput { IsReady = false }, default));
        Assert.Equal(new DictationDeliveryResult(0, 0, "unknown"), await plugin.DeliverDictationAsync(Guid.NewGuid(), "hi", new FakeInput(), default));
    }

    [Fact]
    public async Task InstallAction_InstallsReinstallsAndUpdates()
    {
        Version("_retail_", addOn: false);
        var plugin = await ActiveAsync(() => WowAddOn.VersionFolders(_root));

        Assert.Equal("Install add-on", Assert.Single(plugin.SettingsActions).Title);
        var message = await plugin.ExecuteSettingsActionAsync(GamesPlugin.InstallActionId, default);

        Assert.StartsWith("Installed for Retail.", message);
        Assert.Equal("Reinstall add-on", Assert.Single(plugin.SettingsActions).Title);
        var toc = Path.Join(_root, "_retail_", "Interface", "AddOns", "TypeWhisper", "TypeWhisper.toc");
        File.WriteAllText(toc, File.ReadAllText(toc).Replace("## Version: " + WowAddOn.BundledVersion, "## Version: 0.9.0"));
        Assert.Equal("Update add-on", Assert.Single(plugin.SettingsActions).Title);
    }

    [Fact]
    public async Task InstallAction_WithoutTheGame_AsksForItsFolder()
    {
        var plugin = await ActiveAsync(() => []);

        Assert.StartsWith("World of Warcraft was not found.", await plugin.ExecuteSettingsActionAsync(GamesPlugin.InstallActionId, default));
        await plugin.SaveTextSettingAsync(GamesPlugin.FolderSettingId, _root, default);
        Assert.StartsWith("This folder contains no World of Warcraft version.", await plugin.ExecuteSettingsActionAsync(GamesPlugin.InstallActionId, default));
    }

    [Fact]
    public async Task InstallAction_WarnsWhenTheChosenTranslationCannotRun()
    {
        Version("_retail_", addOn: false, saved: "TypeWhisperDB = {\n[\"translateTo\"] = \"German\",\n}");
        var plugin = await ActiveAsync(() => WowAddOn.VersionFolders(_root));

        Assert.Contains("default LLM is missing or unavailable", await plugin.ExecuteSettingsActionAsync(GamesPlugin.InstallActionId, default));
        _host.Llm = new("Groq · Llama 3.3", true);
        Assert.DoesNotContain("default LLM", await plugin.ExecuteSettingsActionAsync(GamesPlugin.InstallActionId, default));
    }

    [Fact]
    public async Task FolderSetting_OffersThePicker_AndSaysWhereTheGameWasFound()
    {
        Version("_retail_", addOn: false);
        var found = Assert.Single((await ActiveAsync(() => WowAddOn.VersionFolders(_root))).TextSettings);
        var missing = Assert.Single((await ActiveAsync(() => [])).TextSettings);

        Assert.Equal(PluginTextSettingKind.Folder, found.Kind);
        Assert.Equal("", found.Value);
        Assert.Equal(_root + " (found automatically)", found.Placeholder);
        Assert.Equal("Not found automatically. Choose the World of Warcraft folder.", missing.Placeholder);
    }

    [Fact]
    public async Task FolderSetting_TakesAFullPathOrNothing()
    {
        var plugin = await ActiveAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync(GamesPlugin.FolderSettingId, @"World of Warcraft", default));
        await plugin.SaveTextSettingAsync(GamesPlugin.FolderSettingId, " " + _root + " ", default);
        Assert.Equal(_root, Assert.Single(plugin.TextSettings).Value);
        await plugin.SaveTextSettingAsync(GamesPlugin.FolderSettingId, "", default);
        Assert.Equal("", Assert.Single(plugin.TextSettings).Value);
    }

    private async Task<GamesPlugin> ActiveAsync(Func<IReadOnlyList<WowVersionFolder>>? versions = null)
    {
        var plugin = versions is null ? new GamesPlugin() : new GamesPlugin(versions);
        await plugin.ActivateAsync(_host);
        return plugin;
    }

    // A game version folder as Battle.net installs it: the executable, and optionally the add-on and its saved settings.
    private string Version(string folder, bool addOn, string? saved = null)
    {
        var path = Path.Join(_root, folder);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Join(path, "Wow.exe"), []);
        if (addOn) Assert.Empty(WowAddOn.Install([new WowVersionFolder(folder, path, null)]));
        if (saved is not null)
        {
            var variables = Path.Join(path, "WTF", "Account", "ACCOUNT", "SavedVariables");
            Directory.CreateDirectory(variables);
            File.WriteAllText(Path.Join(variables, "TypeWhisper.lua"), saved);
        }
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeInput : IDictationTargetInput
    {
        public readonly List<string> Calls = [];
        public bool IsReady { get; init; } = true;
        public bool PressKeys(IReadOnlyList<ushort> virtualKeys) { Calls.Add("press:" + Keys(virtualKeys)); return true; }
        public bool SignalKeys(IReadOnlyList<ushort> virtualKeys) { Calls.Add("signal:" + Keys(virtualKeys)); return true; }
        public Task<bool> PasteAsync(string text, CancellationToken cancellationToken) { Calls.Add("paste:" + text); return Task.FromResult(true); }
        public Task WaitUntilResponsiveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        private static string Keys(IReadOnlyList<ushort> keys) => string.Join("+", keys.Select(key => key.ToString("X2")));
    }
}

/// <summary>A host with English strings and settings in memory.</summary>
internal sealed class TestHost(string directory) : IPluginHostServices
{
    private readonly Dictionary<string, object?> _settings = new(StringComparer.Ordinal);
    public PluginLlmStatus? Llm { get; set; }
    public PluginLlmStatus? DefaultLlm => Llm;
    public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
    public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
    public Task DeleteSecretAsync(string key) => Task.CompletedTask;
    public T? GetSetting<T>(string key) => _settings.TryGetValue(key, out var value) && value is T typed ? typed : default;
    public void SetSetting<T>(string key, T value) => _settings[key] = value;
    public string PluginDataDirectory => directory;
    public string? ActiveAppProcessName => null;
    public string? ActiveAppName => null;
    public IPluginEventBus EventBus => throw new NotSupportedException();
    public IReadOnlyList<string> AvailableProfileNames => [];
    public void Log(PluginLogLevel level, string message) { }
    public void NotifyCapabilitiesChanged() { }
    public IPluginLocalization Localization { get; } = new English();

    private sealed class English : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.InvariantCulture, key, args);
    }
}
