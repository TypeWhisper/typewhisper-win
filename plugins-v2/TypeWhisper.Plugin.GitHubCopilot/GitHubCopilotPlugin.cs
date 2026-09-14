using System.Globalization;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GitHubCopilot;

/// <summary>Workflow text processing through explicitly selected GitHub Copilot accounts.</summary>
public sealed partial class GitHubCopilotPlugin : ILlmProviderPlugin, IAdditionalLlmProvidersProvider,
    IPluginProfileSettings, IPluginSettingsActions, IPluginConnectionSettings
{
    private const string DefaultProfileId = "github-copilot";
    private const string ConfigurationKey = "accountProfilesV1";
    private readonly ICopilotTransport _transport;
    private readonly TimeSpan _activationTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _lifetime = new();
    private IPluginHostServices? _host;
    private CopilotConfiguration _configuration = new([new(DefaultProfileId, "GitHub Copilot")], DefaultProfileId);
    private IReadOnlyList<CopilotAccount> _accounts = [];
    private readonly Dictionary<string, IReadOnlyList<PluginModelInfo>> _catalogs = [];
    private readonly Dictionary<string, DraftCatalog> _draftCatalogs = [];
    private bool _disposed;

    /// <summary>Creates a provider backed by the official GitHub Copilot SDK.</summary>
    public GitHubCopilotPlugin() : this(new CopilotTransport()) { }
    internal GitHubCopilotPlugin(ICopilotTransport transport, TimeSpan? activationTimeout = null)
    { _transport = transport; _activationTimeout = activationTimeout ?? TimeSpan.FromSeconds(30); }
    /// <inheritdoc />
    public string PluginId => "com.typewhisper.github-copilot";
    /// <inheritdoc />
    public string PluginName => "GitHub Copilot";
    /// <inheritdoc />
    public string PluginVersion => "1.1.0";
    /// <inheritdoc />
    public string ProviderName => RequireProfile(DefaultProfileId).Name;
    /// <inheritdoc />
    public bool IsAvailable => IsProfileAvailable(DefaultProfileId);
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels => ModelsFor(DefaultProfileId);
    /// <inheritdoc />
    public IReadOnlyList<ILlmProviderPlugin> AdditionalLlmProviders => _configuration.Profiles
        .Where(p => p.Id != DefaultProfileId).Select(p => (ILlmProviderPlugin)new AccountProfileRole(this, p.Id)).ToArray();

    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host) => ActivateAsync(host, CancellationToken.None);
    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_lifetime.IsCancellationRequested) { _lifetime.Dispose(); _lifetime = new(); }
        _host = host;
        var saved = host.GetSetting<string>(ConfigurationKey);
        _configuration = saved is null
            ? new([new(DefaultProfileId, PluginName, Model: host.GetSetting<string>("selectedModel"), Connected: host.GetSetting<bool>("connected"))], DefaultProfileId)
            : JsonSerializer.Deserialize<CopilotConfiguration>(saved) ?? throw new InvalidDataException("Invalid Copilot profiles.");
        ValidateConfiguration(_configuration);
        // Preserve old installations by binding a sole stored OAuth account once. With
        // multiple accounts, require an explicit choice instead of guessing ownership.
        if (saved is null && RequireProfile(DefaultProfileId).Connected)
        {
            try { await ExecuteSettingsActionAsync("refresh", cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { host.Log(PluginLogLevel.Warning, "Select a GitHub account and refresh its models in Copilot settings."); }
            return;
        }
        using var activation = Link(cancellationToken);
        activation.CancelAfter(_activationTimeout);
        foreach (var account in _configuration.Profiles.Where(p => p.Connected && p.Account is not null)
            .Select(p => p.Account!).DistinctBy(a => a.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (activation.IsCancellationRequested) break;
            try { _catalogs[account.Key] = await _transport.GetModelsAsync(host.PluginDataDirectory, account, activation.Token); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (activation.IsCancellationRequested) { break; }
            catch (Exception) { host.Log(PluginLogLevel.Warning, "A Copilot account is unavailable. Check its profile connection."); }
        }
    }

    /// <inheritdoc />
    public async Task DeactivateAsync()
    {
        await _lifetime.CancelAsync();
        await _gate.WaitAsync();
        try { _catalogs.Clear(); _draftCatalogs.Clear(); _accounts = []; _host = null; }
        finally { _gate.Release(); }
    }

    private CopilotProfile RequireProfile(string id) => _configuration.Profiles.FirstOrDefault(p => p.Id == id)
        ?? throw new ArgumentException(L("Select the profile again before continuing."));
    private IReadOnlyList<PluginModelInfo> ModelsFor(string id) =>
        _configuration.Profiles.FirstOrDefault(p => p.Id == id) is { Connected: true, Account: { } account }
            ? _catalogs.GetValueOrDefault(account.Key) ?? [] : [];
    private bool IsProfileAvailable(string id) => _host is not null && !_lifetime.IsCancellationRequested &&
        _configuration.Profiles.FirstOrDefault(p => p.Id == id) is { Connected: true, Account: not null, Model: { } model }
        && ModelsFor(id).Any(m => m.Id == model);

    /// <inheritdoc />
    public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) =>
        ProcessProfileAsync(DefaultProfileId, systemPrompt, userText, model, ct);

    private async Task<string> ProcessProfileAsync(string id, string systemPrompt, string userText, string model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(userText);
        using var linked = Link(ct);
        linked.CancelAfter(TimeSpan.FromMinutes(2));
        await _gate.WaitAsync(linked.Token);
        try
        {
            var profile = RequireProfile(id);
            if (!IsProfileAvailable(id)) throw new InvalidOperationException(L("Connect this GitHub account in plugin settings first."));
            var selected = string.IsNullOrWhiteSpace(model) ? profile.Model : model;
            if (!ModelsFor(id).Any(m => m.Id == selected)) throw new ArgumentException(L("Choose a model from the current Copilot model list."));
            try
            {
                var result = await _transport.ProcessAsync(Host.PluginDataDirectory, profile.Account!, systemPrompt, userText, selected!, linked.Token);
                if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException();
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _lifetime.IsCancellationRequested) { throw; }
            catch (CopilotModelUnavailableException)
            {
                var accountKey = profile.Account!.Key;
                _catalogs[accountKey] = ModelsFor(id).Where(m => m.Id != selected).ToArray();
                foreach (var draftId in _draftCatalogs.Keys.Where(key => _draftCatalogs[key].Account.Key == accountKey).ToArray())
                {
                    var draft = _draftCatalogs[draftId];
                    _draftCatalogs[draftId] = draft with { Models = draft.Models.Where(m => m.Id != selected).ToArray() };
                }
                Host.NotifyCapabilitiesChanged();
                throw new InvalidOperationException(L("Choose a model from the current Copilot model list."));
            }
            catch (CopilotSignInRequiredException)
            {
                _catalogs.Remove(profile.Account!.Key);
                Host.NotifyCapabilitiesChanged();
                throw new InvalidOperationException(L("The selected GitHub account is signed out or unavailable. Sign in to that account in Copilot CLI and refresh this profile."));
            }
            catch (Exception)
            {
                // Never forward SDK errors: they may contain text or credentials.
                throw new InvalidOperationException(L("Copilot could not process the text. Check your connection, model access and plan limits, then retry."));
            }
        }
        finally { _gate.Release(); }
    }

    private void Commit(CopilotConfiguration configuration)
    {
        ValidateConfiguration(configuration);
        Host.SetSetting(ConfigurationKey, JsonSerializer.Serialize(configuration));
        _configuration = configuration;
        Host.NotifyCapabilitiesChanged();
    }
    private void ReplaceProfile(CopilotProfile profile) => Commit(_configuration with
        { Profiles = _configuration.Profiles.Select(p => p.Id == profile.Id ? profile : p).ToList() });
    private static void ValidateConfiguration(CopilotConfiguration configuration)
    {
        if (configuration.Profiles is null || configuration.Profiles.Count is < 1 or > 16 ||
            configuration.Profiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 100 || string.IsNullOrWhiteSpace(p.Id) ||
                p.Id != DefaultProfileId && (!p.Id.StartsWith(DefaultProfileId + "-", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(p.Id[(DefaultProfileId.Length + 1)..], "N", out _))) ||
            configuration.Profiles.Count(p => p.Id == DefaultProfileId) != 1 ||
            configuration.Profiles.Select(p => p.Id).Distinct().Count() != configuration.Profiles.Count ||
            !configuration.Profiles.Any(p => p.Id == configuration.EditorProfileId) ||
            configuration.Profiles.Any(p => p.Account is { } account && !ValidAccount(account)))
            throw new InvalidDataException("Invalid Copilot profile configuration.");
    }
    private static bool ValidAccount(CopilotAccount account) => !string.IsNullOrWhiteSpace(account.Login) && account.Login.Length <= 100 &&
        Uri.TryCreate(account.Host, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    private IPluginHostServices Host => _host ?? throw new InvalidOperationException("Plugin is inactive.");
    private CancellationTokenSource Link(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
    }
    private static readonly Lazy<IReadOnlyDictionary<string, string>> GermanStrings = new(() =>
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(GitHubCopilotPlugin).Assembly.Location)!, "Localization", "de.json");
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new Dictionary<string, string>(); }
    });
    private static string L(string text) => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de"
        && GermanStrings.Value.TryGetValue(text, out var translated) ? translated : text;
    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _catalogs.Clear(); _draftCatalogs.Clear(); _accounts = []; _host = null;
        _lifetime.Dispose(); _gate.Dispose();
    }

    private sealed class AccountProfileRole(GitHubCopilotPlugin owner, string id) : ILlmProviderPlugin, ILlmProviderSelectionIdentity
    {
        public string PluginId => owner.PluginId;
        public string PluginName => owner.PluginName;
        public string PluginVersion => owner.PluginVersion;
        public string LlmSelectionId => id;
        public string ProviderName => owner.RequireProfile(id).Name;
        public bool IsAvailable => owner.IsProfileAvailable(id);
        public IReadOnlyList<PluginModelInfo> SupportedModels => owner.ModelsFor(id);
        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) => owner.ProcessProfileAsync(id, systemPrompt, userText, model, ct);
        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}

internal sealed record CopilotProfile(string Id, string Name, CopilotAccount? Account = null, string? Model = null, bool Connected = false);
internal sealed record CopilotConfiguration(List<CopilotProfile> Profiles, string EditorProfileId);
internal sealed record DraftCatalog(CopilotAccount Account, IReadOnlyList<PluginModelInfo> Models);
