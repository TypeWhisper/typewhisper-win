using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GitHubCopilot;

public sealed partial class GitHubCopilotPlugin
{
    private CopilotProfile Editor => RequireProfile(_configuration.EditorProfileId);
    /// <inheritdoc />
    public string ProfileSelectorId => "configurationProfile";
    /// <inheritdoc />
    public string? AddProfileActionId => "add";
    /// <inheritdoc />
    public string? RemoveProfileActionId => Editor.Id == DefaultProfileId ? null : Editor.Id + "/delete";
    /// <inheritdoc />
    public bool ShowApiKeySettings => false;
    /// <inheritdoc />
    public string ConnectionIdentity => Editor.Id;
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings
    {
        get
        {
            var profile = Editor;
            var draft = _draftCatalogs.GetValueOrDefault(profile.Id);
            var account = draft?.Account ?? profile.Account;
            var accounts = _accounts.Concat(account is null ? [] : new[] { account }).DistinctBy(a => a.Key).ToArray();
            var choices = accounts.Select(a => new PluginSettingChoice(a.Key, a.DisplayName)).ToList();
            choices.Insert(0, new("", L("Choose a signed-in GitHub account")));
            var models = draft?.Models ?? ModelsFor(profile.Id);
            string Field(string key) => profile.Id + "/" + key;
            return
            [
                new(ProfileSelectorId, L("GitHub profiles"), L("Each profile uses its own GitHub account and model selection."), profile.Id)
                { Section = PluginSettingsSection.Connection, SaveChoiceOnChange = true, Choices = _configuration.Profiles.Select(p => new PluginSettingChoice(p.Id, p.Name)).ToArray() },
                new(Field("name"), L("Profile name"), L("Shown in workflow provider selections."), profile.Name, 100)
                { Section = PluginSettingsSection.Connection },
                new(Field("account"), L("GitHub account"), L("Sign in to additional accounts in Copilot CLI, then refresh accounts here. Tokens stay in the CLI credential store."), account?.Key ?? "")
                { Section = PluginSettingsSection.Connection, Choices = choices },
                new(Field("model"), L("Text model"), L("Refresh models for the chosen account, then save this profile."), profile.Model ?? models.FirstOrDefault()?.Id ?? "")
                { Section = PluginSettingsSection.TextProcessing, Choices = models.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToArray() }
            ];
        }
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [
        new("add", L("Add GitHub profile"), L("Create a profile with its own account and model selection.")) { Section = PluginSettingsSection.Connection },
        new("refreshAccounts", L("Refresh GitHub accounts"), L("Load accounts already signed in through the official Copilot CLI. This does not change the active CLI account.")) { Section = PluginSettingsSection.Connection },
        new(Editor.Id + "/refresh", L("Refresh models"), L("Check the chosen account and load its models. Save profile to apply the account and model together.")) { Section = PluginSettingsSection.TextProcessing },
        .. Editor.Connected ? new[] { new PluginSettingsAction(Editor.Id + "/disconnect", L("Disconnect profile"), L("Disable this profile without signing out of GitHub or affecting other profiles.")) { Section = PluginSettingsSection.Connection } } : [],
        .. RemoveProfileActionId is { } remove ? new[] { new PluginSettingsAction(remove, L("Remove profile"), L("Remove this TypeWhisper profile. The GitHub account remains signed in.")) } : []
    ];

    /// <inheritdoc />
    public async Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        using var linked = Link(cancellationToken);
        await _gate.WaitAsync(linked.Token);
        try
        {
            if (id == ProfileSelectorId)
            {
                _ = RequireProfile(value);
                Commit(_configuration with { EditorProfileId = value });
                return;
            }
            // Compatibility for the previous single-account provider's configuration API.
            if (id != "selectedModel") throw new ArgumentException("Use the profile save action for account settings.");
            var profile = RequireProfile(DefaultProfileId);
            if (!ModelsFor(profile.Id).Any(m => m.Id == value)) throw new ArgumentException(L("Choose a model from the current Copilot model list."));
            ReplaceProfile(profile with { Model = value });
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task SaveProfileSettingsAsync(string profileId, IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken cancellationToken)
    {
        using var linked = Link(cancellationToken);
        await _gate.WaitAsync(linked.Token);
        try
        {
            ValidateEditor(profileId, apiKey);
            var profile = Editor;
            var allowed = new[] { profileId + "/name", profileId + "/account", profileId + "/model" };
            if (values.Count != allowed.Length || values.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException(L("Select the profile again before continuing."));
            var name = values[allowed[0]].Trim();
            if (name.Length is < 1 or > 100) throw new ArgumentException(L("Enter a profile name of up to 100 characters."));
            var key = values[allowed[1]];
            var draft = _draftCatalogs.GetValueOrDefault(profileId);
            var account = _accounts.FirstOrDefault(a => a.Key == key) ?? (profile.Account?.Key == key ? profile.Account : null);
            if (account is null) throw new ArgumentException(L("Choose a signed-in GitHub account"));
            var models = draft?.Account.Key == key ? draft.Models : profile.Account?.Key == key ? ModelsFor(profileId) : [];
            var model = values[allowed[2]];
            if (!models.Any(m => m.Id == model)) throw new ArgumentException(L("Refresh models for the chosen account, then save this profile."));
            linked.Token.ThrowIfCancellationRequested();
            // One settings write is the commit point for name, account, model and enablement.
            ReplaceProfile(profile with { Name = name, Account = account, Model = model, Connected = true });
            _catalogs[account.Key] = models;
            _draftCatalogs.Remove(profileId);
            Host.NotifyCapabilitiesChanged();
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<PluginProfileActionResult> ExecuteProfileActionAsync(string profileId, string actionId,
        IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken cancellationToken)
    {
        using var linked = Link(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        await _gate.WaitAsync(linked.Token);
        try
        {
            ValidateEditor(profileId, apiKey);
            if (actionId == "refreshAccounts")
            {
                await RefreshAccountsAsync(linked.Token);
                return new(L("GitHub accounts refreshed. Choose an account and refresh its models."));
            }
            if (actionId == profileId + "/disconnect")
            {
                ReplaceProfile(Editor with { Connected = false });
                _draftCatalogs.Remove(profileId);
                return new(L("Disconnected. Your GitHub sign-in is kept."));
            }
            if (actionId != profileId + "/refresh") throw new ArgumentException("Unknown profile action.");
            if (!values.TryGetValue(profileId + "/account", out var key)) throw new ArgumentException(L("Choose a signed-in GitHub account"));
            await RefreshAccountsAsync(linked.Token);
            var account = _accounts.FirstOrDefault(a => a.Key == key)
                ?? throw new ArgumentException(L("The selected GitHub account is signed out or unavailable. Sign in to that account in Copilot CLI and refresh this profile."));
            var models = await LoadModelsAsync(account, linked.Token);
            _draftCatalogs[profileId] = new(account, models);
            return new(L("Models loaded for this account. Save profile to apply your changes."), HasPendingChanges: true);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        using var linked = Link(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        await _gate.WaitAsync(linked.Token);
        try
        {
            if (id == "add")
            {
                if (_configuration.Profiles.Count >= 16) throw new ArgumentException(L("Remove a profile before adding another one."));
                var profile = new CopilotProfile(DefaultProfileId + "-" + Guid.NewGuid().ToString("N"), L("GitHub account"));
                Commit(new([.. _configuration.Profiles, profile], profile.Id));
                return L("Profile added. Refresh accounts, choose an account and load its models.");
            }
            if (id == RemoveProfileActionId)
            {
                var removed = Editor.Id;
                Commit(new(_configuration.Profiles.Where(p => p.Id != removed).ToList(), DefaultProfileId));
                _draftCatalogs.Remove(removed);
                return L("Profile removed. Your GitHub sign-in is kept.");
            }
            if (id is "disconnect" || id == Editor.Id + "/disconnect")
            {
                var profile = id == "disconnect" ? RequireProfile(DefaultProfileId) : Editor;
                ReplaceProfile(profile with { Connected = false });
                _draftCatalogs.Remove(profile.Id);
                return L("Disconnected. Your GitHub sign-in is kept.");
            }
            if (id == "refreshAccounts")
            {
                await RefreshAccountsAsync(linked.Token);
                return L("GitHub accounts refreshed. Choose an account and refresh its models.");
            }
            if (id != "refresh") throw new ArgumentException("Unknown settings action.");
            // Preserve the original explicit connect action for existing automation. It
            // may bind a sole account, but must never choose between multiple accounts.
            await RefreshAccountsAsync(linked.Token);
            var profileToConnect = RequireProfile(DefaultProfileId);
            var accountToConnect = profileToConnect.Account is { } savedAccount
                ? _accounts.FirstOrDefault(a => a.Matches(savedAccount)) : _accounts.Count == 1 ? _accounts[0] : null;
            if (accountToConnect is null) throw new ArgumentException(L("Choose a signed-in GitHub account"));
            var available = await LoadModelsAsync(accountToConnect, linked.Token);
            var selected = profileToConnect.Model ?? available[0].Id;
            if (!available.Any(m => m.Id == selected)) throw new ArgumentException(L("Choose a model from the current Copilot model list."));
            linked.Token.ThrowIfCancellationRequested();
            ReplaceProfile(profileToConnect with { Account = accountToConnect, Model = selected, Connected = true });
            _catalogs[accountToConnect.Key] = available;
            Host.NotifyCapabilitiesChanged();
            return L("Connected. Copilot models refreshed.");
        }
        finally { _gate.Release(); }
    }

    private void ValidateEditor(string profileId, string? apiKey)
    {
        _ = Host;
        if (profileId != _configuration.EditorProfileId) throw new ArgumentException(L("Select the profile again before continuing."));
        if (!string.IsNullOrEmpty(apiKey)) throw new ArgumentException("Copilot uses the existing GitHub sign-in.", nameof(apiKey));
    }
    private async Task RefreshAccountsAsync(CancellationToken ct)
    {
        try
        {
            var accounts = (await _transport.GetAccountsAsync(Host.PluginDataDirectory, ct)).Where(ValidAccount).DistinctBy(a => a.Key).ToArray();
            ct.ThrowIfCancellationRequested();
            _accounts = accounts;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new InvalidOperationException(L("Could not load GitHub accounts. Sign in through Copilot CLI and try again.")); }
        // Missing accounts invalidate readiness without rebinding or deleting profiles.
        var removed = _catalogs.Keys.Where(key => !_accounts.Any(a => a.Key == key)).ToArray();
        foreach (var key in removed) _catalogs.Remove(key);
        foreach (var key in _draftCatalogs.Keys.Where(id => !_accounts.Any(a => a.Key == _draftCatalogs[id].Account.Key)).ToArray()) _draftCatalogs.Remove(key);
        Host.NotifyCapabilitiesChanged();
    }
    private async Task<IReadOnlyList<PluginModelInfo>> LoadModelsAsync(CopilotAccount account, CancellationToken ct)
    {
        try
        {
            var models = await _transport.GetModelsAsync(Host.PluginDataDirectory, account, ct);
            if (models.Count == 0) throw new InvalidOperationException();
            return models;
        }
        // Cancellation is not evidence that the saved account or its models became invalid.
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            InvalidateAccountModels(account.Key);
            Host.NotifyCapabilitiesChanged();
            throw new InvalidOperationException(L("Could not load Copilot models. Check your sign-in, Copilot access and connection, then retry."));
        }
    }

    private void InvalidateAccountModels(string accountKey)
    {
        _catalogs.Remove(accountKey);
        foreach (var id in _draftCatalogs.Keys.Where(id => _draftCatalogs[id].Account.Key == accountKey).ToArray()) _draftCatalogs.Remove(id);
    }
}
