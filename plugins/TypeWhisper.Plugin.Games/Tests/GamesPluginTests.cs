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
    public async Task TargetProcessNames_ListEveryGameThatIsOn()
    {
        var plugin = await ActiveAsync();
        Assert.Contains("Wow", plugin.TargetProcessNames);
        Assert.Contains("WowClassic", plugin.TargetProcessNames);

        await plugin.SaveProfileSettingsAsync("wow", new Dictionary<string, string> { ["wow:enabled"] = "false" }, null, default);

        // Turned off, World of Warcraft is like any other app: TypeWhisper no longer hands its dictations to the plugin.
        Assert.DoesNotContain("Wow", plugin.TargetProcessNames);
        Assert.Equal(1, _host.CapabilityChanges);
        Assert.Null(await plugin.PrepareDictationAsync(new(Guid.NewGuid(), "Wow", null), default));
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
    public async Task List_StartsWithWorldOfWarcraft_AndShowsItsSettings()
    {
        Version("_retail_", addOn: true);
        var plugin = await ActiveAsync(folder => folder is null ? WowAddOn.VersionFolders(_root) : []);

        var settings = plugin.TextSettings;

        var list = settings[0];
        Assert.Equal(plugin.ProfileSelectorId, list.Id);
        Assert.Equal("wow", list.Value);
        Assert.Equal([("wow", "World of Warcraft")], list.Choices.Select(choice => (choice.Value, choice.Title)));
        Assert.Contains("Add-on installed for Retail.", list.Description);
        Assert.Equal(["wow:enabled", "wow:folder"], settings.Skip(1).Select(field => field.Id));
        Assert.Equal(GamesPlugin.AddGameActionId, plugin.AddProfileActionId);
        Assert.Null(plugin.RemoveProfileActionId);
        Assert.Equal([GamesPlugin.AddGameActionId, WorldOfWarcraftGame.InstallActionId], plugin.SettingsActions.Select(action => action.Id));
    }

    [Fact]
    public async Task AddedGame_IsChosen_SavedAndHandedItsDictations()
    {
        var plugin = await ActiveAsync(runningPrograms: () => ["Albion-Online", "Discord"]);

        Assert.StartsWith("Game added.", await plugin.ExecuteSettingsActionAsync(GamesPlugin.AddGameActionId, default));
        var game = plugin.TextSettings[0].Value;
        Assert.NotEqual("wow", game);
        Assert.Equal(["Albion-Online", "Discord"], plugin.TextSettings.Single(field => field.Id == game + ":process").Suggestions);
        await plugin.SaveProfileSettingsAsync(game, new Dictionary<string, string>
        {
            [game + ":name"] = " Albion ", [game + ":process"] = "Albion-Online.exe", [game + ":chatKey"] = "t",
            [game + ":send"] = "true", [game + ":limit"] = "255", [game + ":translate"] = "German"
        }, null, default);

        Assert.Equal([("wow", "World of Warcraft"), (game, "Albion")], plugin.TextSettings[0].Choices.Select(choice => (choice.Value, choice.Title)));
        Assert.Contains("Albion-Online", plugin.TargetProcessNames);
        var id = Guid.NewGuid();
        var plan = await plugin.PrepareDictationAsync(new(id, "albion-online", null), default);
        Assert.Equal(new DictationTargetPlan { AppName = "Albion", TranslateTo = "German" }, plan);
        var input = new FakeInput();
        // Without an add-on the game shows nothing, so TypeWhisper keeps its own indicator.
        Assert.Null(plugin.OnDictationPhaseChanged(id, DictationTargetPhase.Recording, input));
        Assert.Equal(new DictationDeliveryResult(1, 1), await plugin.DeliverDictationAsync(id, "hi all", input, default));
        Assert.Equal(["press:54", "paste:hi all", "press:0D"], input.Calls);
    }

    [Fact]
    public async Task AddedGame_RefusesValuesItCannotUse()
    {
        var plugin = await ActiveAsync(runningPrograms: () => []);
        await plugin.ExecuteSettingsActionAsync(GamesPlugin.AddGameActionId, default);
        var game = plugin.TextSettings[0].Value;

        Task Save(string field, string value) => plugin.SaveProfileSettingsAsync(game, new Dictionary<string, string> { [game + ":" + field] = value }, null, default);

        Assert.Equal("Enter a name for the game.", (await Assert.ThrowsAsync<ArgumentException>(() => Save("name", " "))).Message);
        Assert.StartsWith("Enter up to five program names", (await Assert.ThrowsAsync<ArgumentException>(() => Save("process", @"C:\Games\Albion.exe"))).Message);
        Assert.Equal("TypeWhisper cannot dictate into itself.", (await Assert.ThrowsAsync<ArgumentException>(() => Save("process", "TypeWhisper"))).Message);
        Assert.Equal("Wow already belongs to World of Warcraft.", (await Assert.ThrowsAsync<ArgumentException>(() => Save("process", "Wow"))).Message);
        await Assert.ThrowsAsync<ArgumentException>(() => Save("chatKey", "F12"));
        await Assert.ThrowsAsync<ArgumentException>(() => Save("limit", "999"));
        await Assert.ThrowsAsync<ArgumentException>(() => Save("translate", "Klingon"));
        Assert.DoesNotContain("TypeWhisper", plugin.TargetProcessNames);
    }

    [Fact]
    public async Task RemovedGame_LeavesTheList_AndItsDictations()
    {
        var plugin = await ActiveAsync(runningPrograms: () => []);
        await plugin.ExecuteSettingsActionAsync(GamesPlugin.AddGameActionId, default);
        var game = plugin.TextSettings[0].Value;
        await plugin.SaveProfileSettingsAsync(game, new Dictionary<string, string> { [game + ":process"] = "Wardogs" }, null, default);
        Assert.Equal("remove:" + game, plugin.RemoveProfileActionId);

        Assert.Equal("Removed.", await plugin.ExecuteSettingsActionAsync("remove:" + game, default));

        Assert.Equal("wow", plugin.TextSettings[0].Value);
        Assert.Single(plugin.TextSettings[0].Choices);
        Assert.DoesNotContain("Wardogs", plugin.TargetProcessNames);
    }

    [Fact]
    public async Task Games_SurviveARestart_AndInvalidEntriesAreLeftOut()
    {
        var plugin = await ActiveAsync(runningPrograms: () => []);
        await plugin.ExecuteSettingsActionAsync(GamesPlugin.AddGameActionId, default);
        var game = plugin.TextSettings[0].Value;
        await plugin.SaveProfileSettingsAsync(game, new Dictionary<string, string> { [game + ":process"] = "Wardogs", [game + ":chatKey"] = "y" }, null, default);
        var saved = _host.GetSetting<GamesSettings>(GamesPlugin.SettingsKey)!;
        _host.SetSetting(GamesPlugin.SettingsKey, saved with
        {
            CustomGames = [.. saved.CustomGames, new CustomGameSettings { Id = "x", Name = "Broken" }, new CustomGameSettings { Id = "c" + new string('a', 32), Name = "" }]
        });

        var restarted = await ActiveAsync(runningPrograms: () => []);

        Assert.Equal(["wow", game], restarted.TextSettings[0].Choices.Select(choice => choice.Value));
        Assert.Contains("Wardogs", restarted.TargetProcessNames);
    }

    [Fact]
    public async Task InstallAction_InstallsReinstallsAndUpdates()
    {
        Version("_retail_", addOn: false);
        var plugin = await ActiveAsync(folder => WowAddOn.VersionFolders(_root));

        Assert.Equal("Install add-on", InstallAction(plugin).Title);
        var message = (await plugin.ExecuteProfileActionAsync("wow", WorldOfWarcraftGame.InstallActionId, new Dictionary<string, string>(), null, default)).Message;

        Assert.StartsWith("Installed for Retail.", message);
        Assert.Equal("Reinstall add-on", InstallAction(plugin).Title);
        var toc = Path.Join(_root, "_retail_", "Interface", "AddOns", "TypeWhisper", "TypeWhisper.toc");
        File.WriteAllText(toc, File.ReadAllText(toc).Replace("## Version: " + WowAddOn.BundledVersion, "## Version: 0.9.0"));
        Assert.Equal("Update add-on", InstallAction(plugin).Title);
    }

    [Fact]
    public async Task InstallAction_UsesTheFolderOnThePage_AndAsksForOneWithoutTheGame()
    {
        var empty = Path.Join(_root, "empty");
        Directory.CreateDirectory(empty);
        var plugin = await ActiveAsync(folder => folder is null ? [] : WowAddOn.FindInstallRoots(folder, detect: false).SelectMany(WowAddOn.VersionFolders).ToArray());

        Assert.StartsWith("World of Warcraft was not found.",
            (await plugin.ExecuteProfileActionAsync("wow", WorldOfWarcraftGame.InstallActionId, new Dictionary<string, string>(), null, default)).Message);
        // A folder just chosen counts before it is saved.
        Assert.StartsWith("This folder contains no World of Warcraft version.", (await plugin.ExecuteProfileActionAsync("wow", WorldOfWarcraftGame.InstallActionId,
            new Dictionary<string, string> { ["wow:folder"] = empty }, null, default)).Message);
        Version("_retail_", addOn: false);
        Assert.StartsWith("Installed for Retail.", (await plugin.ExecuteProfileActionAsync("wow", WorldOfWarcraftGame.InstallActionId,
            new Dictionary<string, string> { ["wow:folder"] = _root }, null, default)).Message);
    }

    [Fact]
    public async Task InstallAction_WarnsWhenTheChosenTranslationCannotRun()
    {
        Version("_retail_", addOn: false, saved: "TypeWhisperDB = {\n[\"translateTo\"] = \"German\",\n}");
        var plugin = await ActiveAsync(folder => WowAddOn.VersionFolders(_root));

        Assert.Contains("default LLM is missing or unavailable", await plugin.ExecuteSettingsActionAsync(WorldOfWarcraftGame.InstallActionId, default));
        Assert.Contains("default LLM is missing or unavailable", plugin.TextSettings[0].Description);
        _host.Llm = new("Groq · Llama 3.3", true);
        Assert.DoesNotContain("default LLM", await plugin.ExecuteSettingsActionAsync(WorldOfWarcraftGame.InstallActionId, default));
    }

    [Fact]
    public async Task FolderSetting_OffersThePicker_AndSaysWhereTheGameWasFound()
    {
        Version("_retail_", addOn: false);
        var found = (await ActiveAsync(folder => WowAddOn.VersionFolders(_root))).TextSettings.Single(field => field.Id == "wow:folder");
        var missing = (await ActiveAsync(_ => [])).TextSettings.Single(field => field.Id == "wow:folder");

        Assert.Equal(PluginTextSettingKind.Folder, found.Kind);
        Assert.Equal("", found.Value);
        Assert.Equal(_root + " (found automatically)", found.Placeholder);
        Assert.Equal("Not found automatically. Choose the World of Warcraft folder.", missing.Placeholder);
    }

    [Fact]
    public async Task FolderSetting_TakesAFullPathOrNothing()
    {
        var plugin = await ActiveAsync(_ => []);

        Task Save(string value) => plugin.SaveProfileSettingsAsync("wow", new Dictionary<string, string> { ["wow:folder"] = value }, null, default);

        await Assert.ThrowsAsync<ArgumentException>(() => Save("World of Warcraft"));
        await Save(" " + _root + " ");
        Assert.Equal(_root, plugin.TextSettings.Single(field => field.Id == "wow:folder").Value);
        await Save("");
        Assert.Equal("", plugin.TextSettings.Single(field => field.Id == "wow:folder").Value);
    }

    private static PluginSettingsAction InstallAction(GamesPlugin plugin) =>
        plugin.SettingsActions.Single(action => action.Id == WorldOfWarcraftGame.InstallActionId);

    private async Task<GamesPlugin> ActiveAsync(Func<string?, IReadOnlyList<WowVersionFolder>>? versions = null,
        Func<IReadOnlyList<string>>? runningPrograms = null)
    {
        var plugin = new GamesPlugin(versions ?? (folder => folder is null ? [] : WowAddOn.FindInstallRoots(folder, detect: false).SelectMany(WowAddOn.VersionFolders).ToArray()),
            runningPrograms ?? (() => []));
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
    public int CapabilityChanges { get; private set; }
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
    public void NotifyCapabilitiesChanged() => CapabilityChanges++;
    public IPluginLocalization Localization { get; } = new English();

    private sealed class English : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.InvariantCulture, key, args);
    }
}
