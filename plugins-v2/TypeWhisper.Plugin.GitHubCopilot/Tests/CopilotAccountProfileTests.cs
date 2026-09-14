using TypeWhisper.PluginSDK;
using static TypeWhisper.Plugin.GitHubCopilot.Tests.CopilotPluginTests;

namespace TypeWhisper.Plugin.GitHubCopilot.Tests;

public sealed class CopilotAccountProfileTests
{
    [Fact]
    public async Task CancelingDraftRefreshPreservesBothProfilesUsingTheAccount()
    {
        var transport = new FakeTransport();
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(new TestHost());
        await Configure(plugin, FakeTransport.Personal, "model-a", "Primary");
        await plugin.ExecuteSettingsActionAsync("add", default);
        await Configure(plugin, FakeTransport.Personal, "model-a", "Secondary");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.LoadModels = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return []; };
        using var cancel = new CancellationTokenSource();
        var refresh = plugin.ExecuteProfileActionAsync(plugin.ConnectionIdentity, plugin.ConnectionIdentity + "/refresh",
            Draft(plugin, FakeTransport.Personal, "model-a"), null, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.True(plugin.IsAvailable);
        Assert.True(Assert.Single(plugin.AdditionalLlmProviders).IsAvailable);
        await plugin.ProcessAsync("s", "u", "", default);
        await Assert.Single(plugin.AdditionalLlmProviders).ProcessAsync("s", "u", "", default);
        await plugin.DeactivateAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupUsesOneDeadlineForAllAccountsAndPropagatesCallerCancellation(bool cancelCaller)
    {
        var host = new TestHost();
        host.SetSetting("accountProfilesV1", System.Text.Json.JsonSerializer.Serialize(new CopilotConfiguration(
            [new("github-copilot", "Primary", FakeTransport.Personal, "model-a", true),
             new("github-copilot-" + Guid.NewGuid().ToString("N"), "Secondary", FakeTransport.Work, "model-b", true)], "github-copilot")));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport { Accounts = [FakeTransport.Personal, FakeTransport.Work],
            LoadModels = async ct => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return []; } };
        using var plugin = new GitHubCopilotPlugin(transport, cancelCaller ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(200));
        using var cancel = new CancellationTokenSource();
        var activation = plugin.ActivateAsync(host, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation);
        }
        else await activation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, transport.Refreshes);
        Assert.False(plugin.IsAvailable);
        Assert.False(Assert.Single(plugin.AdditionalLlmProviders).IsAvailable);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task SeparateAccountsKeepModelsAndWorkflowIdentityAcrossEditorSwitchAndRestart()
    {
        var host = new TestHost();
        var transport = new FakeTransport { Accounts = [FakeTransport.Personal, FakeTransport.Work] };
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(host);
        await Configure(plugin, FakeTransport.Personal, "model-a", "Personal");
        await plugin.ExecuteSettingsActionAsync("add", default);
        var workId = plugin.ConnectionIdentity;
        await Configure(plugin, FakeTransport.Work, "model-b", "Work");
        var work = Assert.Single(plugin.AdditionalLlmProviders);
        Assert.Equal(workId, ((ILlmProviderSelectionIdentity)work).LlmSelectionId);
        Assert.Equal("model-a", Assert.Single(plugin.SupportedModels).Id);
        Assert.Equal("model-b", Assert.Single(work.SupportedModels).Id);
        await plugin.ProcessAsync("s", "personal text", "", default);
        Assert.Equal(FakeTransport.Personal, transport.RequestedAccount);
        await work.ProcessAsync("s", "work text", "", default);
        Assert.Equal(FakeTransport.Work, transport.RequestedAccount);
        await Assert.ThrowsAsync<ArgumentException>(() => work.ProcessAsync("s", "u", "model-a", default));
        await plugin.SaveTextSettingAsync(plugin.ProfileSelectorId, "github-copilot", default);
        await work.ProcessAsync("s", "still work", "", default);
        Assert.Equal(FakeTransport.Work, transport.RequestedAccount);
        await plugin.DeactivateAsync();
        transport.Accounts.Reverse(); // Discovery order and active editor must not change routing.
        await plugin.ActivateAsync(host);
        var restored = Assert.Single(plugin.AdditionalLlmProviders);
        Assert.Equal(workId, ((ILlmProviderSelectionIdentity)restored).LlmSelectionId);
        Assert.Equal("Personal", plugin.ProviderName);
        Assert.Equal("Work", restored.ProviderName);
        Assert.True(plugin.IsAvailable);
        Assert.True(restored.IsAvailable);
        await restored.ProcessAsync("s", "after restart", "", default);
        Assert.Equal(FakeTransport.Work, transport.RequestedAccount);
        Assert.Equal(0, host.SecretAccesses);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task AccountDraftDoesNotAffectRequestsUntilAtomicSaveAndFailedSaveKeepsAllFields()
    {
        var host = new TestHost();
        var transport = new FakeTransport { Accounts = [FakeTransport.Personal, FakeTransport.Work] };
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(host);
        await Configure(plugin, FakeTransport.Personal, "model-a", "Personal");
        var before = host.GetSetting<string>("accountProfilesV1");
        var draft = Draft(plugin, FakeTransport.Work, "model-b", "Work");
        await plugin.ExecuteProfileActionAsync(plugin.ConnectionIdentity, plugin.ConnectionIdentity + "/refresh", draft, null, default);
        await plugin.ProcessAsync("s", "u", "", default);
        Assert.Equal(FakeTransport.Personal, transport.RequestedAccount);
        host.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveProfileSettingsAsync(plugin.ConnectionIdentity, draft, null, default));
        Assert.Equal(before, host.GetSetting<string>("accountProfilesV1"));
        Assert.Equal("Personal", plugin.ProviderName);
        await plugin.ProcessAsync("s", "u", "", default);
        Assert.Equal(FakeTransport.Personal, transport.RequestedAccount);
        host.FailWrites = false;
        await plugin.SaveProfileSettingsAsync(plugin.ConnectionIdentity, draft, null, default);
        await plugin.ProcessAsync("s", "u", "", default);
        Assert.Equal(FakeTransport.Work, transport.RequestedAccount);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task SignedOutAccountNeverFallsBackToAnotherAndOnlyItsReadinessIsCleared()
    {
        var transport = new FakeTransport { Accounts = [FakeTransport.Personal, FakeTransport.Work] };
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(new TestHost());
        await Configure(plugin, FakeTransport.Personal, "model-a", "Personal");
        await plugin.ExecuteSettingsActionAsync("add", default);
        await Configure(plugin, FakeTransport.Work, "model-b", "Work");
        var work = Assert.Single(plugin.AdditionalLlmProviders);
        transport.Accounts.Remove(FakeTransport.Personal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.ProcessAsync("s", "personal", "", default));
        Assert.Null(transport.RequestedAccount);
        Assert.False(plugin.IsAvailable);
        Assert.True(work.IsAvailable);
        await work.ProcessAsync("s", "work", "", default);
        Assert.Equal(FakeTransport.Work, transport.RequestedAccount);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task RemovingProfileInvalidatesItsRoleButKeepsOtherProfileAndExternalAccounts()
    {
        var host = new TestHost();
        var transport = new FakeTransport { Accounts = [FakeTransport.Personal, FakeTransport.Work] };
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(host);
        await Configure(plugin, FakeTransport.Personal, "model-a", "Personal");
        await plugin.ExecuteSettingsActionAsync("add", default);
        var workId = plugin.ConnectionIdentity;
        await Configure(plugin, FakeTransport.Work, "model-b", "Work");
        var staleRole = Assert.Single(plugin.AdditionalLlmProviders);
        await plugin.ExecuteSettingsActionAsync(workId + "/delete", default);
        Assert.Empty(plugin.AdditionalLlmProviders);
        Assert.True(plugin.IsAvailable);
        Assert.False(staleRole.IsAvailable);
        await Assert.ThrowsAsync<ArgumentException>(() => staleRole.ProcessAsync("s", "u", "", default));
        Assert.Equal(2, transport.Accounts.Count);
        Assert.Equal(0, host.SecretAccesses);
        Assert.Single(Saved(host).Profiles);
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task StaleEditorAndAccountChangesRequireMatchingDraftCatalog()
    {
        var transport = new FakeTransport { Accounts = [FakeTransport.Personal, FakeTransport.Work] };
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(new TestHost());
        await Configure(plugin, FakeTransport.Personal, "model-a", "Personal");
        var stale = Draft(plugin, FakeTransport.Work, "model-b");
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveProfileSettingsAsync("github-copilot", stale, null, default));
        await plugin.ExecuteSettingsActionAsync("add", default);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveProfileSettingsAsync("github-copilot", stale, null, default));
        await plugin.DeactivateAsync();
    }

    [Fact]
    public async Task LegacyMigrationBindsOnlyASoleAccountAndNeverGuessesBetweenAccounts()
    {
        var host = new TestHost(); host.SetSetting("connected", true); host.SetSetting("selectedModel", "model-a");
        var transport = new FakeTransport { Accounts = [FakeTransport.Personal, FakeTransport.Work] };
        using var plugin = new GitHubCopilotPlugin(transport);
        await plugin.ActivateAsync(host);
        Assert.False(plugin.IsAvailable);
        Assert.Null(host.GetSetting<string>("accountProfilesV1"));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.ExecuteSettingsActionAsync("refresh", default));
        transport.Accounts.Remove(FakeTransport.Work);
        await plugin.ExecuteSettingsActionAsync("refresh", default);
        Assert.Equal(FakeTransport.Personal, Saved(host).Profiles.Single().Account);
        await plugin.DeactivateAsync();
        transport.Accounts = [FakeTransport.Work];
        await plugin.ActivateAsync(host);
        Assert.False(plugin.IsAvailable);
        Assert.Equal(FakeTransport.Personal, Saved(host).Profiles.Single().Account);
        await plugin.DeactivateAsync();
    }

    private static async Task Configure(GitHubCopilotPlugin plugin, CopilotAccount account, string model, string name)
    {
        var draft = Draft(plugin, account, model, name);
        await plugin.ExecuteProfileActionAsync(plugin.ConnectionIdentity, plugin.ConnectionIdentity + "/refresh", draft, null, default);
        await plugin.SaveProfileSettingsAsync(plugin.ConnectionIdentity, draft, null, default);
    }
}
