using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GitHubCopilot.Tests;

public sealed class CopilotPluginTests
{
    [Fact]
    public async Task ConnectDiscoversModelsAndSendsExactWorkflowText()
    {
        var transport = new FakeTransport();
        var host = new TestHost();
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(host);
        Assert.False(plugin.IsAvailable);
        Assert.Equal(0, transport.Refreshes);
        await plugin.ExecuteSettingsActionAsync("refresh", default);
        Assert.True(plugin.IsAvailable);
        Assert.Equal("model-a", Assert.Single(plugin.SupportedModels).Id);
        var result = await plugin.ProcessAsync("Translate into German.", "Hello\n世界", "model-a", default);
        Assert.Equal("Translated text", result);
        Assert.Equal(("Translate into German.", "Hello\n世界", "model-a"), transport.Request);
        Assert.True(Saved(host).Profiles.Single().Connected);
        Assert.True(host.Notifications > 0);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task SavedSelectionSurvivesRestartAndDisconnectKeepsExternalLogin()
    {
        var host = new TestHost();
        var transport = new FakeTransport();
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(host);
        await plugin.ExecuteSettingsActionAsync("refresh", default);
        var invalidations = transport.Invalidations;
        await plugin.SaveTextSettingAsync("selectedModel", "model-a", default);
        Assert.True(transport.Invalidations > invalidations, "Saved settings must discard cached turn checks.");
        await plugin.DeactivateAsync();
        await plugin.ActivateAsync(host);
        Assert.True(plugin.IsAvailable);
        Assert.Equal(2, transport.Refreshes);
        Assert.Equal("model-a", plugin.TextSettings.Single(f => f.Id.EndsWith("/model", StringComparison.Ordinal)).Value);
        await plugin.ExecuteSettingsActionAsync("disconnect", default);
        Assert.False(plugin.IsAvailable);
        Assert.Empty(plugin.SupportedModels);
        Assert.False(Saved(host).Profiles.Single().Connected);
        Assert.Equal(0, host.SecretAccesses);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task ProfileSavePersistsModelButConnectionActionsKeepDraftUnsaved()
    {
        var host = new TestHost();
        using var plugin = new GitHubCopilotPlugin(new FakeTransport());
        await plugin.ActivateAsync(host);
        var profile = plugin.TextSettings.Single(f => f.Id == plugin.ProfileSelectorId).Value;
        var draft = Draft(plugin, FakeTransport.Personal, "model-a");
        var result = await plugin.ExecuteProfileActionAsync(profile, profile + "/refresh", draft, null, default);
        Assert.True(result.HasPendingChanges);
        Assert.Null(host.GetSetting<string>("accountProfilesV1"));
        Assert.False(plugin.IsAvailable);
        await plugin.SaveProfileSettingsAsync(profile, draft, null, default);
        Assert.Equal("model-a", Saved(host).Profiles.Single().Model);
        Assert.Equal(FakeTransport.Personal, Saved(host).Profiles.Single().Account);
        Assert.False(plugin.ShowApiKeySettings);
        await plugin.DeactivateAsync();
        await plugin.ActivateAsync(host);
        Assert.Equal("model-a", plugin.TextSettings.Single(f => f.Id.EndsWith("/model", StringComparison.Ordinal)).Value);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task CacheDurationIsSharedAcrossProfilesAndSurvivesRestart()
    {
        var host = new TestHost();
        var transport = new FakeTransport();
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(host);
        await plugin.ExecuteSettingsActionAsync("refresh", default);
        var profileId = plugin.ConnectionIdentity;
        var values = Draft(plugin, FakeTransport.Personal, "model-a");
        values[profileId + "/catalogCacheMinutes"] = "120";
        await plugin.SaveProfileSettingsAsync(profileId, values, null, default);
        Assert.Equal(120, Saved(host).CacheMinutes);
        Assert.Equal(TimeSpan.FromHours(2), transport.CatalogLifetime);

        await plugin.ExecuteSettingsActionAsync("add", default);
        Assert.Equal(120, Saved(host).CacheMinutes);
        var secondProfileId = plugin.ConnectionIdentity;
        Assert.DoesNotContain(plugin.TextSettings, f => f.Id == profileId + "/catalogCacheMinutes");
        Assert.Equal("120", plugin.TextSettings.Single(f => f.Id == secondProfileId + "/catalogCacheMinutes").Value);
        var secondValues = Draft(plugin, FakeTransport.Personal, "model-a");
        secondValues[secondProfileId + "/catalogCacheMinutes"] = "60";
        await plugin.ExecuteProfileActionAsync(secondProfileId, secondProfileId + "/refresh", secondValues, null, default);
        await plugin.SaveProfileSettingsAsync(secondProfileId, secondValues, null, default);
        Assert.Equal(60, Saved(host).CacheMinutes);
        Assert.Equal(TimeSpan.FromHours(1), transport.CatalogLifetime);
        await plugin.ExecuteSettingsActionAsync(plugin.RemoveProfileActionId!, default);
        Assert.Equal(60, Saved(host).CacheMinutes);
        await plugin.DeactivateAsync();
        await plugin.ActivateAsync(host);
        Assert.Equal("60", plugin.TextSettings.Single(f => f.Id == profileId + "/catalogCacheMinutes").Value);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task ExistingConfigurationWithoutCacheDurationUsesTenMinutes()
    {
        var host = new TestHost();
        host.SetSetting("accountProfilesV1", """
            {"Profiles":[{"Id":"github-copilot","Name":"GitHub Copilot","Connected":false}],"EditorProfileId":"github-copilot"}
            """);
        var transport = new FakeTransport();
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(host);
        Assert.Equal("10", plugin.TextSettings.Single(f => f.Id == "github-copilot/catalogCacheMinutes").Value);
        Assert.Equal(TimeSpan.FromMinutes(10), transport.CatalogLifetime);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task InvalidCacheDurationDoesNotChangeSavedConfiguration()
    {
        var host = new TestHost();
        using var plugin = new GitHubCopilotPlugin(new FakeTransport());
        await plugin.ActivateAsync(host);
        await plugin.ExecuteSettingsActionAsync("refresh", default);
        var values = Draft(plugin, FakeTransport.Personal, "model-a");
        values[plugin.ConnectionIdentity + "/catalogCacheMinutes"] = "999";
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveProfileSettingsAsync(plugin.ConnectionIdentity, values, null, default));
        Assert.Equal(10, Saved(host).CacheMinutes);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task ProfileSaveRejectsStaleAndInvalidDraftsAndRetainsSelectionOnWriteFailure()
    {
        var host = new TestHost();
        host.SetSetting("selectedModel", "previous-model");
        using var plugin = new GitHubCopilotPlugin(new FakeTransport());
        await plugin.ActivateAsync(host);
        var profile = plugin.TextSettings.Single(f => f.Id == plugin.ProfileSelectorId).Value;
        var draft = Draft(plugin, FakeTransport.Personal, "model-a");
        await plugin.ExecuteProfileActionAsync(profile, profile + "/refresh", draft, null, default);
        await Assert.ThrowsAsync<ArgumentException>(async () => await plugin.SaveProfileSettingsAsync("stale", draft, null, default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await plugin.SaveProfileSettingsAsync(profile, draft, "unexpected-key", default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await plugin.SaveProfileSettingsAsync(profile, new Dictionary<string, string> { ["selectedModel"] = "unknown" }, null, default));
        host.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveProfileSettingsAsync(profile, draft, null, default));
        Assert.Equal("previous-model", plugin.TextSettings.Single(f => f.Id.EndsWith("/model", StringComparison.Ordinal)).Value);
        Assert.Equal("previous-model", host.GetSetting<string>("selectedModel"));
        Assert.Equal(0, host.SecretAccesses);
        await plugin.DeactivateAsync();
    }

    internal static Dictionary<string, string> Draft(GitHubCopilotPlugin plugin, CopilotAccount account, string model, string? name = null)
    {
        var fields = plugin.TextSettings.Where(f => f.Id != plugin.ProfileSelectorId).ToDictionary(f => f.Id, f => f.Value);
        var profile = plugin.ConnectionIdentity;
        fields[profile + "/account"] = account.Key;
        fields[profile + "/model"] = model;
        if (name is not null) fields[profile + "/name"] = name;
        return fields;
    }
    internal static CopilotConfiguration Saved(TestHost host) => System.Text.Json.JsonSerializer.Deserialize<CopilotConfiguration>(host.GetSetting<string>("accountProfilesV1")!)!;

    [Fact]
    public async Task FailedSettingsWriteDoesNotPublishSelectionOrDisconnect()
    {
        var host = new TestHost();
        using var plugin = new GitHubCopilotPlugin(new FakeTransport());
        await plugin.ActivateAsync(host);
        await plugin.ExecuteSettingsActionAsync("refresh", default);
        host.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.ExecuteSettingsActionAsync("disconnect", default));
        Assert.True(plugin.IsAvailable);
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveTextSettingAsync("selectedModel", "model-a", default));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task MissingModelNeverFallsBackToAnotherModel()
    {
        var transport = new FakeTransport();
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(new TestHost());
        await plugin.ExecuteSettingsActionAsync("refresh", default);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.ProcessAsync("s", "u", "removed-model", default));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("selectedModel", "invented", default));
        Assert.Null(transport.Request);
        await plugin.DeactivateAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRefreshClearsReadyStateAndDoesNotExposeSdkErrors(bool signedOut)
    {
        var transport = new FakeTransport();
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(new TestHost());
        await plugin.ExecuteSettingsActionAsync("refresh", default);
        transport.Error = signedOut ? new CopilotSignInRequiredException() : new IOException("secret token and private text");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.ExecuteSettingsActionAsync("refresh", default));
        Assert.DoesNotContain("secret", error.ToString());
        Assert.False(plugin.IsAvailable);
        Assert.Empty(plugin.SupportedModels);
        await plugin.DeactivateAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public async Task EmptyResponseAndProviderErrorsAreSanitized(string? response)
    {
        var transport = new FakeTransport { Response = response ?? "unused" };
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(new TestHost());
        await plugin.ExecuteSettingsActionAsync("refresh", default);
        if (response is null) transport.Error = new IOException("private user text and token");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.ProcessAsync("s", "u", "model-a", default));
        Assert.DoesNotContain("private", error.ToString());
        await plugin.DeactivateAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndDeactivationDrainInFlightRequest(bool deactivate)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport { Process = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return "unreachable"; } };
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(new TestHost());
        await plugin.ExecuteSettingsActionAsync("refresh", default);
        using var cancel = new CancellationTokenSource();
        var request = plugin.ProcessAsync("s", "u", "model-a", cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (deactivate) await plugin.DeactivateAsync().WaitAsync(TimeSpan.FromSeconds(5));
        else cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        if (!deactivate) await plugin.DeactivateAsync();
        Assert.False(plugin.IsAvailable);
    }
}

internal sealed class FakeTransport : ICopilotTransport
{
    internal static readonly CopilotAccount Personal = new("https://github.com", "personal");
    internal static readonly CopilotAccount Work = new("https://github.com", "work");
    internal List<CopilotAccount> Accounts = [Personal];
    internal CopilotAccount? RequestedAccount;
    internal readonly Dictionary<string, IReadOnlyList<PluginModelInfo>> Catalogs = new() { [Personal.Key] = [new("model-a", "Model A")], [Work.Key] = [new("model-b", "Model B")] };
    internal int Refreshes;
    internal Exception? Error;
    internal string Response = "Translated text";
    internal (string, string, string)? Request;
    internal Func<CancellationToken, Task<string>>? Process;
    internal Func<CancellationToken, Task<IReadOnlyList<PluginModelInfo>>>? LoadModels;
    internal Func<CancellationToken, Task<IReadOnlyList<CopilotAccount>>>? LoadAccounts;
    public Task<IReadOnlyList<CopilotAccount>> GetAccountsAsync(string dataDirectory, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return LoadAccounts?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<CopilotAccount>>(Accounts.ToArray()); }
    public Task<IReadOnlyList<PluginModelInfo>> GetModelsAsync(string dataDirectory, CopilotAccount account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Refreshes++;
        if (Error is not null) throw Error;
        if (!Accounts.Any(a => a.Matches(account))) throw new CopilotSignInRequiredException();
        return LoadModels?.Invoke(ct) ?? Task.FromResult(Catalogs[account.Key]);
    }
    public Task<string> ProcessAsync(string dataDirectory, CopilotAccount account, string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!Accounts.Any(a => a.Matches(account))) throw new CopilotSignInRequiredException();
        RequestedAccount = account;
        Request = (systemPrompt, userText, model);
        if (Error is not null) throw Error;
        return Process?.Invoke(ct) ?? Task.FromResult(Response);
    }
    internal int Invalidations;
    public void InvalidateCache() => Invalidations++;
    internal TimeSpan CatalogLifetime = CopilotTransport.CatalogLifetime;
    public void SetCatalogLifetime(TimeSpan lifetime)
    {
        if (CatalogLifetime == lifetime) return;
        CatalogLifetime = lifetime;
        InvalidateCache();
    }
}

internal sealed class TestHost : IPluginHostServices
{
    private readonly Dictionary<string, object?> _settings = [];
    public string PluginDataDirectory { get; } = Path.Combine(Path.GetTempPath(), "copilot-test-" + Guid.NewGuid().ToString("N"));
    public string? ActiveAppProcessName => null;
    public string? ActiveAppName => null;
    public IPluginEventBus EventBus => throw new NotSupportedException();
    public IReadOnlyList<string> AvailableProfileNames => [];
    public IPluginLocalization Localization { get; } = new TestLocalization();
    public bool AllowLegacyDataMigration => false;
    internal int SecretAccesses;
    internal int Notifications;
    internal bool FailWrites;
    public Task StoreSecretAsync(string key, string value) { SecretAccesses++; throw new NotSupportedException(); }
    public Task<string?> LoadSecretAsync(string key) { SecretAccesses++; throw new NotSupportedException(); }
    public Task DeleteSecretAsync(string key) { SecretAccesses++; throw new NotSupportedException(); }
    public T? GetSetting<T>(string key) => _settings.TryGetValue(key, out var value) ? (T?)value : default;
    public void SetSetting<T>(string key, T value) { if (FailWrites) throw new IOException(); _settings[key] = value; }
    public void Log(PluginLogLevel level, string message) { }
    public void NotifyCapabilitiesChanged() => Notifications++;
}

internal sealed class TestLocalization : IPluginLocalization
{
    public string CurrentLanguage => "en";
    public IReadOnlyList<string> AvailableLanguages => ["en"];
    public string GetString(string key) => key;
    public string GetString(string key, params object[] args) => string.Format(key, args);
}
