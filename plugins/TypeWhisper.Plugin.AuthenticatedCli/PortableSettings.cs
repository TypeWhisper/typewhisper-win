using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AuthenticatedCli;

public sealed partial class AuthenticatedCliPlugin : IPluginProfileSettings, IPluginSettingsActions, IPluginConnectionSettings
{
    internal const string ProfilesSetting = "cliProfiles.v1";
    private List<CliProfile> _profiles = [];
    private string _settingsProfileId = "codex";
    private readonly Dictionary<string, CliProfile> _draftProfiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CliAvailabilitySnapshot> _draftSnapshots = new(StringComparer.Ordinal);

    private string L(string english, string german) =>
        Localization?.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? german : english;

    /// <inheritdoc />
    public string ProfileSelectorId => "profile";
    /// <inheritdoc />
    public string AddProfileActionId => "add";
    /// <inheritdoc />
    public string? RemoveProfileActionId { get { lock (_stateLock) return _profiles.Count > 1 ? _settingsProfileId + "/delete" : null; } }
    /// <inheritdoc />
    public bool ShowApiKeySettings => false;
    /// <inheritdoc />
    public string ConnectionIdentity { get { lock (_stateLock) return _settingsProfileId; } }

    private IReadOnlyList<CliProviderDescriptor> ProfileDescriptors
    {
        get { lock (_stateLock) return _profiles.Select(p => p.Descriptor).ToArray(); }
    }

    private CliProviderDescriptor CurrentDescriptor(string id)
    {
        lock (_stateLock)
            return _profiles.SingleOrDefault(p => p.Id == id)?.Descriptor
                ?? throw new ArgumentException("This CLI profile no longer exists.");
    }

    private string GetProfileName(string id, string fallback)
    {
        lock (_stateLock) return _profiles.SingleOrDefault(p => p.Id == id)?.Name ?? GetString(fallback);
    }

    private void RestoreProfiles(IPluginHostServices host)
    {
        var stored = host.GetSetting<List<CliProfile>>(ProfilesSetting);
        var profiles = stored ?? CliProviderDescriptor.All.Select(d => new CliProfile
        {
            Id = d.Key, Provider = d.Key, Name = GetString(d.DisplayKey),
            Executable = _selectedExecutables.GetValueOrDefault(d.Key) ?? "",
            Model = _selectedModels.GetValueOrDefault(d.Key, "default"),
            Models = d.Kind == CliProviderKind.Codex ? _codexModels.ToList() : []
        }).ToList();
        if (profiles.Count is < 1 or > 32 || profiles.Any(p => p is null ||
                !System.Text.RegularExpressions.Regex.IsMatch(p.Id, "^[a-z0-9-]{1,64}$") ||
                !CliProviderDescriptor.All.Any(d => d.Key == p.Provider)) ||
            profiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Count)
            throw new InvalidOperationException("The saved CLI profiles are invalid.");
        foreach (var profile in profiles) ValidateEnvironment(profile.Descriptor, profile.Environment, requireExistingDirectory: false);
        lock (_stateLock)
        {
            _settingsProfileId = profiles[0].Id;
            PublishProfiles(profiles);
        }
    }

    // The host write is the single commit point; failed writes never publish partial values.
    private void CommitProfiles(List<CliProfile> profiles)
    {
        _host?.SetSetting(ProfilesSetting, profiles);
        lock (_stateLock) PublishProfiles(profiles);
    }

    private void PublishProfiles(List<CliProfile> profiles)
    {
        foreach (var profile in profiles)
        {
            var previous = _profiles.SingleOrDefault(p => p.Id == profile.Id);
            if (previous is null || !SameConnection(previous, profile) || previous.OpenCodeCatalog != profile.OpenCodeCatalog)
            {
                _snapshots[profile.Id] = CliAvailabilitySnapshot.Initial;
                _openCodeCatalogs.Remove(profile.Id);
            }
            _selectedExecutables[profile.Id] = string.IsNullOrEmpty(profile.Executable) ? null : profile.Executable;
            _selectedModels[profile.Id] = profile.Model;
        }
        foreach (var id in _snapshots.Keys.Except(profiles.Select(p => p.Id)).ToArray())
        {
            _snapshots.Remove(id);
            _openCodeCatalogs.Remove(id);
            _selectedExecutables.Remove(id);
            _selectedModels.Remove(id);
        }
        _profiles = profiles;
        _roles = profiles.Select(p => (ILlmProviderPlugin)new AuthenticatedCliProviderRole(this, p.Descriptor)).ToArray();
    }

    private static bool SameConnection(CliProfile a, CliProfile b) => a.Provider == b.Provider &&
        string.Equals(a.Executable, b.Executable, StringComparison.OrdinalIgnoreCase) &&
        a.Environment.Count == b.Environment.Count && a.Environment.All(pair =>
            b.Environment.TryGetValue(pair.Key, out var value) && pair.Value == value);

    private IReadOnlyList<PluginModelInfo> GetModels(CliProviderDescriptor descriptor)
    {
        lock (_stateLock)
        {
            var profile = _profiles.SingleOrDefault(p => p.Id == descriptor.Key);
            if (profile is null) return [];
            if (profile.Provider == "opencode")
                return GetOpenCodeFreeModels(descriptor.Key).OrderByDescending(m => m.Id == profile?.Model)
                    .Select((m, i) => new PluginModelInfo(m.Id, m.DisplayName) { IsRecommended = i == 0 }).ToArray();
            return ModelsFor(profile);
        }
    }

    private IReadOnlyList<PluginModelInfo> ModelsFor(CliProfile profile) => profile.Provider switch
    {
        "codex" => [new("default", GetString("Model.Default")), .. profile.Models],
        "claude" => [new("default", GetString("Model.Default")), new("sonnet", "Sonnet (CLI alias)"), new("opus", "Opus (CLI alias)"), new("haiku", "Haiku (CLI alias)")],
        "opencode" => (profile.Models.Count > 0 ? profile.Models : _profiles.Any(p => p.Id == profile.Id && SameConnection(p, profile))
            ? GetOpenCodeFreeModels(profile.Id).Select(m => new PluginModelInfo(m.Id, m.DisplayName)).ToList() : []),
        _ => [new("default", GetString("Model.Default"))]
    };

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings
    {
        get
        {
            lock (_stateLock)
            {
                var profile = _profiles.Single(p => p.Id == _settingsProfileId);
                string Id(string name) => profile.Id + "/" + name;
                var fields = new List<PluginTextSetting>
                {
                    new("profile", L("CLI profiles", "CLI-Profile"), "", profile.Id)
                    {
                        SaveChoiceOnChange = true, Section = PluginSettingsSection.Connection,
                        Choices = _profiles.Select(p => new PluginSettingChoice(p.Id, p.Name)).ToArray()
                    },
                    new(Id("name"), L("Profile name", "Profilname"), L("Shown in workflow provider selections.", "Wird in der Provider-Auswahl der Workflows angezeigt."), profile.Name, 100) { Section = PluginSettingsSection.Connection },
                    new(Id("provider"), L("CLI", "CLI"), L("You can add several profiles for the same CLI.", "Du kannst mehrere Profile für dieselbe CLI hinzufügen."), profile.Provider)
                    {
                        Section = PluginSettingsSection.Connection,
                        Choices = CliProviderDescriptor.All.Select(d => new PluginSettingChoice(d.Key, GetString(d.DisplayKey))).ToArray()
                    },
                    new(Id("environment"), L("Environment variables", "Umgebungsvariablen"),
                        L("One NAME=VALUE per line. Supported session directories: CODEX_HOME (Codex), CLAUDE_CONFIG_DIR (Claude), XDG_DATA_HOME (OpenCode). Leave empty to inherit the CLI session.",
                          "Eine Zeile pro NAME=WERT. Unterstützte Sitzungsverzeichnisse: CODEX_HOME (Codex), CLAUDE_CONFIG_DIR (Claude), XDG_DATA_HOME (OpenCode). Leer lassen, um die CLI-Sitzung zu übernehmen."),
                        string.Join("\n", profile.Environment.Select(p => p.Key + "=" + p.Value)), 8192)
                    { Section = PluginSettingsSection.Connection, IsMultiline = true }
                };
                foreach (var provider in CliProviderDescriptor.All)
                {
                    var sameProvider = profile.Provider == provider.Key;
                    var view = sameProvider ? profile : new CliProfile { Id = profile.Id, Provider = provider.Key };
                    if (_draftProfiles.TryGetValue(profile.Id, out var draft) && draft.Provider == provider.Key) view = draft;
                    var paths = _discovery.FindCandidates(provider.ExecutableName);
                    var snapshot = GetSnapshot(profile.Descriptor);
                    var status = sameProvider ? " · " + GetString("State." + snapshot.State) : "";
                    if (sameProvider && profile.Executable.Length == 0 && snapshot.ExecutablePath is { } detected) status += " · " + detected;
                    fields.Add(new(Id(provider.Key + "/executable"), L("Executable path", "Programmpfad") + (sameProvider && profile.Executable.Length == 0 ? L(" (automatic)", " (automatisch)") : ""),
                        L("Leave empty for automatic detection", "Für automatische Erkennung leer lassen") + status,
                        sameProvider ? profile.Executable : "", 2048)
                    {
                        Section = PluginSettingsSection.Connection, Suggestions = paths,
                        VisibleWhen = new(Id("provider"), [provider.Key])
                    });
                    if (provider.Kind == CliProviderKind.Antigravity) continue;
                    var models = ModelsFor(view);
                    var choices = models.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToList();
                    var model = sameProvider ? profile.Model : "default";
                    if (model == "default" && provider.Kind == CliProviderKind.OpenCode && choices.Count > 0) model = choices[0].Value;
                    if (!choices.Any(c => c.Value == model)) choices.Add(new(model, model == "default" ? GetString("Model.Default") : L("Unavailable: ", "Nicht verfügbar: ") + model));
                    fields.Add(new(Id(provider.Key + "/model"), L("Model", "Modell"),
                        provider.Kind == CliProviderKind.Claude
                            ? L("CLI aliases; availability depends on the selected session.", "CLI-Aliase; die Verfügbarkeit hängt von der gewählten Sitzung ab.")
                            : L("Refresh models with the entered path and environment, then save the profile.", "Modelle mit dem eingegebenen Pfad und den Umgebungsvariablen aktualisieren, danach das Profil speichern."), model)
                    {
                        Section = PluginSettingsSection.TextProcessing, Choices = choices,
                        VisibleWhen = new(Id("provider"), [provider.Key])
                    });
                }
                return fields.OrderBy(f => f.Section).ToArray();
            }
        }
    }

    /// <inheritdoc />
    public async Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id == ProfileSelectorId)
        {
            lock (_stateLock)
            {
                if (!_profiles.Any(p => p.Id == value)) throw new ArgumentException("Unknown profile.");
                _settingsProfileId = value;
            }
            _host?.NotifyCapabilitiesChanged();
            return;
        }
        await SaveProfileSettingsAsync(ConnectionIdentity, new Dictionary<string, string> { [id] = value }, null, ct);
    }

    private CliProfile ReadDraft(string profileId, IReadOnlyDictionary<string, string> values)
    {
        if (profileId != _settingsProfileId) throw new ArgumentException("Select the profile again before saving.");
        var current = _profiles.Single(p => p.Id == profileId);
        string Value(string name, string fallback) => values.GetValueOrDefault(profileId + "/" + name, fallback).Trim();
        foreach (var (id, value) in values)
        {
            var field = TextSettings.SingleOrDefault(f => f.Id == id && id != ProfileSelectorId)
                ?? throw new ArgumentException("The setting is no longer available.");
            if (value.Length > field.MaxLength) throw new ArgumentException("The setting is too long.");
        }
        var provider = Value("provider", current.Provider);
        var descriptor = CliProviderDescriptor.All.SingleOrDefault(d => d.Key == provider) ?? throw new ArgumentException("Unknown CLI.");
        var name = Value("name", current.Name);
        if (name.Length == 0) throw new ArgumentException("Enter a profile name.");
        var executable = Value(provider + "/executable", provider == current.Provider ? current.Executable : "");
        if (executable.Length > 0 && CliExecutableDiscovery.ResolveNativeExecutable(executable, descriptor.ExecutableName) is null)
            throw new ArgumentException("Select an installed native CLI executable.");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawEnvironment = Value("environment", string.Join("\n", current.Environment.Select(p => p.Key + "=" + p.Value)));
        foreach (var line in rawEnvironment.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator < 1 || !environment.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim()))
                throw new ArgumentException("Enter each environment variable once as NAME=VALUE.");
        }
        ValidateEnvironment(descriptor, environment);
        var result = current with { Name = name, Provider = provider, Executable = executable, Environment = environment,
            Model = Value(provider + "/model", provider == current.Provider ? current.Model : "default") };
        if (!SameConnection(current, result)) result = result with { Models = [], OpenCodeCatalog = null };
        if (_draftProfiles.TryGetValue(profileId, out var draft) && SameConnection(draft, result)) result = result with { Models = draft.Models, OpenCodeCatalog = draft.OpenCodeCatalog };
        return result;
    }

    private static void ValidateEnvironment(CliProviderDescriptor descriptor, IReadOnlyDictionary<string, string> environment, bool requireExistingDirectory = true)
    {
        foreach (var (name, value) in environment)
            if (!descriptor.ProviderEnvironmentVariables.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                !Path.IsPathFullyQualified(value) || CliPathSafety.IsNetworkOrDevicePath(value) || value.Contains('\0') ||
                requireExistingDirectory && !CliPathSafety.IsSafeLocalDirectory(value))
                throw new ArgumentException("Use a supported session-directory variable with an existing absolute local directory.");
    }

    private static IReadOnlyDictionary<string, string> CreateProfileEnvironment(CliProviderDescriptor descriptor, string directory)
    {
        try { ValidateEnvironment(descriptor, descriptor.EnvironmentOverrides); }
        catch (ArgumentException ex) { throw new PluginRequestException("The CLI session directory is unavailable.", PluginRequestFailureKind.InvalidRequest, isTransient: false, innerException: ex); }
        var environment = new Dictionary<string, string>(descriptor.EnvironmentOverrides, StringComparer.OrdinalIgnoreCase);
        if (descriptor.Kind == CliProviderKind.OpenCode)
            foreach (var (name, value) in CreateOpenCodeEnvironmentOverrides(directory)) environment[name] = value;
        return environment;
    }

    /// <inheritdoc />
    public async Task SaveProfileSettingsAsync(string profileId, IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        var promotedDraft = false;
        try
        {
            lock (_stateLock)
            {
                var profile = ReadDraft(profileId, values);
                if (profile.Model != "default" && !ModelsFor(profile).Any(m => m.Id == profile.Model))
                    throw new ArgumentException("Refresh the models for this CLI session and select an available model.");
                ct.ThrowIfCancellationRequested();
                var verified = _draftProfiles.TryGetValue(profileId, out var draft) && SameConnection(draft, profile)
                    && _draftSnapshots.TryGetValue(profileId, out var draftSnapshot) ? draftSnapshot : null;
                CommitProfiles(_profiles.Select(p => p.Id == profileId ? profile : p).ToList());
                if (verified is not null)
                {
                    if (profile.OpenCodeCatalog is not null)
                    {
                        PrepareOpenCodeCatalog(profile.Descriptor, verified.ExecutablePath!, profile.OpenCodeCatalog);
                        verified = verified with { CatalogRevision = GetOpenCodeCatalogRevision(profileId) };
                    }
                    StoreSnapshot(profile.Descriptor, verified);
                    promotedDraft = true;
                }
                _draftProfiles.Remove(profileId);
                _draftSnapshots.Remove(profileId);
            }
        }
        finally { _refreshGate.Release(); }
        _host?.NotifyCapabilitiesChanged();
        // The draft already verified this exact connection; do not require another catalog fetch to save it.
        if (promotedDraft) return;
        // Persistence has succeeded. A failed or cancelled probe must not report a failed save.
        try { await RefreshOneAsync(CurrentDescriptor(profileId), true, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { _host?.Log(PluginLogLevel.Warning, "event=saved-cli-profile-probe-failed type=" + ex.GetType().Name); }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [
        new("add", L("Add CLI profile", "CLI-Profil hinzufügen"), L("Create another CLI configuration.", "Eine weitere CLI-Konfiguration anlegen.")) { Section = PluginSettingsSection.Connection },
        new(ConnectionIdentity + "/refresh", L("Refresh models", "Modelle aktualisieren"), L("Check the entered CLI session and fetch models before saving.", "Eingegebene CLI-Sitzung prüfen und Modelle vor dem Speichern laden.")) { Section = PluginSettingsSection.TextProcessing },
        .. (RemoveProfileActionId is { } remove ? new[] { new PluginSettingsAction(remove, L("Remove this profile", "Dieses Profil entfernen"), L("Workflows using this profile will need another provider.", "Zugehörige Workflows benötigen danach einen anderen Provider.")) { Section = PluginSettingsSection.Connection } } : [])
    ];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        if (id.EndsWith("/refresh", StringComparison.Ordinal))
            return (await ExecuteProfileActionAsync(ConnectionIdentity, id, new Dictionary<string, string>(), null, ct)).Message;
        await _refreshGate.WaitAsync(ct);
        try
        {
            lock (_stateLock)
            {
                ct.ThrowIfCancellationRequested();
                var profiles = _profiles.ToList();
                if (id == "add")
                {
                    if (profiles.Count >= 32) throw new ArgumentException("At most 32 CLI profiles are supported.");
                    var profile = new CliProfile { Id = Guid.NewGuid().ToString("N"), Name = L("New CLI", "Neue CLI") };
                    profiles.Add(profile);
                    CommitProfiles(profiles);
                    _settingsProfileId = profile.Id;
                }
                else if (id == RemoveProfileActionId && profiles.Count > 1)
                {
                    var removed = _settingsProfileId;
                    profiles.RemoveAll(p => p.Id == removed);
                    CommitProfiles(profiles);
                    _settingsProfileId = profiles[0].Id;
                    _draftProfiles.Remove(removed);
                    _draftSnapshots.Remove(removed);
                }
                else throw new ArgumentException("Unknown action.");
            }
        }
        finally { _refreshGate.Release(); }
        _host?.NotifyCapabilitiesChanged();
        return id == "add" ? L("Profile added.", "Profil hinzugefügt.") : L("Profile removed.", "Profil entfernt.");
    }

    /// <inheritdoc />
    public async Task<PluginProfileActionResult> ExecuteProfileActionAsync(string profileId, string actionId,
        IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        var directory = "";
        var refreshed = false;
        try
        {
            CliProfile profile;
            lock (_stateLock)
            {
                if (actionId != profileId + "/refresh") throw new ArgumentException("Unknown action.");
                profile = ReadDraft(profileId, values);
            }
            var descriptor = profile.Descriptor;
            var candidates = _discovery.FindCandidates(descriptor.ExecutableName);
            var executable = profile.Executable.Length > 0 ? CliExecutableDiscovery.ResolveNativeExecutable(profile.Executable, descriptor.ExecutableName)
                : candidates.Count == 1 ? candidates[0] : null;
            if (executable is null) throw new ArgumentException("Select an installed CLI executable.");
            directory = CreateTempDirectory();
            var version = await RunProbeAsync(descriptor, executable, descriptor.VersionArguments, directory, ct);
            var help = await RunProbeAsync(descriptor, executable, descriptor.HelpArguments, directory, ct);
            if (version.ExitCode != 0 || descriptor.ParseVersion(version.StandardOutput + version.StandardError) is null ||
                help.ExitCode != 0 || !descriptor.HasRequiredCapabilities(help.StandardOutput + help.StandardError) || !descriptor.SafetyControlsAvailable)
                throw new ArgumentException("This CLI version does not support isolated text processing.");
            var auth = await RunProbeAsync(descriptor, executable, descriptor.AuthenticationArguments, directory, ct);
            if (!descriptor.IsAuthenticated(auth.ExitCode, auth.StandardOutput + auth.StandardError))
                throw new ArgumentException("Sign in to this CLI session first.");
            OpenCodeProfileCatalogCache? openCodeCatalog = null;
            IReadOnlyList<PluginModelInfo> models;
            if (descriptor.Kind == CliProviderKind.OpenCode)
            {
                var catalog = await _openCodeCatalogLoader.LoadAsync(executable, directory, CreateProfileEnvironment(descriptor, directory), ct);
                openCodeCatalog = new(OpenCodeConnectionKey(descriptor, executable), CacheOpenCodeCatalog(catalog));
                models = catalog.Models.Where(m => m.IsFree).Select(m => new PluginModelInfo(m.Id, m.DisplayName)).ToArray();
            }
            else models = descriptor.Kind == CliProviderKind.Codex
                ? await _codexCatalogLoader(executable, directory, ct, descriptor.EnvironmentOverrides)
                : ModelsFor(profile);
            ct.ThrowIfCancellationRequested();
            lock (_stateLock)
            {
                if (profileId != _settingsProfileId) throw new ArgumentException("Select the profile again before refreshing.");
                _draftProfiles[profileId] = profile with { Models = models.ToList(), OpenCodeCatalog = openCodeCatalog };
                _draftSnapshots[profileId] = new CliAvailabilitySnapshot(
                    descriptor.Kind == CliProviderKind.OpenCode && models.Count == 0 ? CliAvailabilityState.NoFreeModels : CliAvailabilityState.Ready,
                    executable, descriptor.ParseVersion(version.StandardOutput + version.StandardError), candidates, DateTimeOffset.UtcNow);
            }
            refreshed = true;
            return new(L("CLI session verified. Save profile to keep the model list.", "CLI-Sitzung bestätigt. Profil speichern übernimmt die Modellliste."), true);
        }
        finally
        {
            if (directory.Length > 0) await DeleteTempDirectoryAsync(directory);
            _refreshGate.Release();
            if (refreshed) _host?.NotifyCapabilitiesChanged();
        }
    }
}
