using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GitHubCopilot;

internal interface ICopilotTransport
{
    Task<IReadOnlyList<CopilotAccount>> GetAccountsAsync(string dataDirectory, CancellationToken ct);
    Task<IReadOnlyList<PluginModelInfo>> GetModelsAsync(string dataDirectory, CopilotAccount account, CancellationToken ct);
    Task<string> ProcessAsync(string dataDirectory, CopilotAccount account, string systemPrompt, string userText, string model, CancellationToken ct);
    /// <summary>Discards cached account and model checks after a settings change.</summary>
    void InvalidateCache();
    void SetCatalogLifetime(TimeSpan lifetime);
}

// Only public account identity is retained. SDK account tokens and opaque selection IDs
// are never persisted, logged, or exposed through plugin settings.
internal sealed record CopilotAccount(string Host, string Login)
{
    internal string Key => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Host.ToLowerInvariant().TrimEnd('/') + "\n" + Login.ToLowerInvariant())));
    internal string DisplayName => Login + " · " + new Uri(Host).Host;
    internal bool Matches(CopilotAccount other) => Key == other.Key;
}

internal sealed class CopilotSignInRequiredException : Exception;
internal sealed class CopilotModelUnavailableException : Exception;

internal sealed class CopilotTransport : ICopilotTransport
{
    internal static readonly TimeSpan CatalogLifetime = TimeSpan.FromMinutes(10);
    private readonly Func<CopilotClientOptions, CopilotClient> _createClient;
    private readonly TimeProvider _time;
    private TimeSpan _catalogLifetime = CatalogLifetime;
    // Per-turn account resolution and model listing are only pre-checks: every turn still binds and
    // verifies the account on its session and switches with requireAvailable before sending text.
    // Only public model metadata is cached in memory; selection IDs are never retained.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (IReadOnlyList<PluginModelInfo> Models, DateTimeOffset Expires)> _catalogs = new();

    internal CopilotTransport(Func<CopilotClientOptions, CopilotClient>? createClient = null, TimeProvider? time = null)
    { _createClient = createClient ?? (options => new CopilotClient(options)); _time = time ?? TimeProvider.System; }

    /// <inheritdoc />
    public void InvalidateCache() => _catalogs.Clear();
    public void SetCatalogLifetime(TimeSpan lifetime)
    {
        if (lifetime == _catalogLifetime) return;
        _catalogLifetime = lifetime;
        InvalidateCache();
    }
    private static string CacheKey(string dataDirectory, CopilotAccount account) => dataDirectory + "\n" + account.Key;
    private bool IsCached(string key, string model) => _catalogs.TryGetValue(key, out var entry) &&
        entry.Expires > _time.GetUtcNow() && entry.Models.Any(m => m.Id == model);
    private IReadOnlyList<PluginModelInfo> Cache(string key, IReadOnlyList<PluginModelInfo> models)
    { _catalogs[key] = (models, _time.GetUtcNow() + _catalogLifetime); return models; }

    internal static CopilotClientOptions CreateClientOptions(string dataDirectory, Func<string, string?>? readEnvironment = null)
    {
        // Preserve the user's normal credential-store identity, but do not inherit token,
        // endpoint, runtime-path, Node injection, telemetry or agent configuration overrides.
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readEnvironment ??= Environment.GetEnvironmentVariable;
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "USERPROFILE", "HOME", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY" })
            if (readEnvironment(name) is { } value) environment[name] = value;
        var runtimeDirectory = Path.Combine(Path.GetDirectoryName(typeof(CopilotTransport).Assembly.Location)!,
            "runtimes", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "native");
        var executable = Path.Combine(runtimeDirectory, OperatingSystem.IsWindows() ? "copilot-runtime.exe" : "copilot-runtime");
        return new CopilotClientOptions
        {
            // Empty mode disables the user's keychain. Keep login compatibility and explicitly
            // disable ambient agent features on every session instead.
            Mode = CopilotClientMode.CopilotCli,
            Connection = RuntimeConnection.ForStdio(executable),
            WorkingDirectory = Path.Combine(dataDirectory, "workspace"),
            Environment = environment,
            UseLoggedInUser = true,
            LogLevel = CopilotLogLevel.None,
            EnableRemoteSessions = false,
            BuiltinPluginDirectories = []
        };
    }

    // Safety controls include experimental APIs; changes require review when upgrading the pinned SDK.
#pragma warning disable GHCP001
    internal static SessionConfig CreateSessionConfig(string dataDirectory, string systemPrompt, string model) => new()
    {
        SessionId = "typewhisper-" + Guid.NewGuid().ToString("N"),
        Model = model,
        WorkingDirectory = Path.Combine(dataDirectory, "workspace"),
        ConfigDirectory = Path.Combine(dataDirectory, "config"),
        SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Replace, Content = systemPrompt },
        AvailableTools = [],
        ExcludedTools = new ToolSet().AddBuiltIn("*").AddCustom("*").AddMcp("*"),
        Tools = [],
        McpServers = new Dictionary<string, McpServerConfig>(),
        DisabledMcpServers = ["*"],
        CustomAgents = [],
        CustomAgentsLocalOnly = true,
        IncludedBuiltinSkills = [],
        SkillDirectories = [],
        PluginDirectories = [],
        InstructionDirectories = [],
        AdditionalDirectories = [],
        EnableConfigDiscovery = false,
        EnableOnDemandInstructionDiscovery = false,
        SkipCustomInstructions = true,
        EnableSkills = false,
        EnableFileHooks = false,
        EnableHostGitOperations = false,
        EnableFileChangeTracking = false,
        EnableSessionStore = false,
        EnableSessionTelemetry = false,
        EnableExperimentalMode = false,
        EnableCitations = false,
        SkipEmbeddingRetrieval = true,
        EmbeddingCacheStorage = EmbeddingCacheStorageMode.InMemory,
        Memory = new MemoryConfiguration { Enabled = false },
        ToolSearch = new ToolSearchConfig { Enabled = false },
        McpOAuthTokenStorage = McpOAuthTokenStorageMode.InMemory,
        CoauthorEnabled = false,
        ManageScheduleEnabled = false,
        Streaming = false,
        IncludeSubAgentStreamingEvents = false,
        InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
        OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("TypeWhisper only processes text.")),
        Hooks = new SessionHooks
        {
            OnPreToolUse = (_, _) => Task.FromResult<PreToolUseHookOutput?>(new() { PermissionDecision = "deny" })
        }
    };
    public async Task<IReadOnlyList<CopilotAccount>> GetAccountsAsync(string dataDirectory, CancellationToken ct)
    {
        InvalidateCache(); // Account discovery is a settings refresh; re-verify every account afterwards.
        await using var client = CreateClient(dataDirectory);
        try
        {
            await client.StartAsync(ct);
            return (await client.Rpc.Account.GetAllUsersAsync(ct))
                .Select(Identity).OfType<CopilotAccount>().DistinctBy(a => a.Key)
                .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { await client.ForceStopAsync(); }
    }

    public async Task<IReadOnlyList<PluginModelInfo>> GetModelsAsync(string dataDirectory, CopilotAccount account, CancellationToken ct)
    {
        var key = CacheKey(dataDirectory, account);
        _catalogs.TryRemove(key, out _);
        await using var client = CreateClient(dataDirectory);
        try
        {
            var selected = await ResolveAccountAsync(client, account, ct);
            return Cache(key, await ListModelsAsync(client, selected.SelectionId!, ct));
        }
        finally { await client.ForceStopAsync(); }
    }

    public async Task<string> ProcessAsync(string dataDirectory, CopilotAccount account, string systemPrompt, string userText, string model, CancellationToken ct)
    {
        await using var client = CreateClient(dataDirectory);
        var config = CreateSessionConfig(dataDirectory, systemPrompt, model);
        CopilotSession? session = null;
        var sessionCreationStarted = false;
        var key = CacheKey(dataDirectory, account);
        try
        {
            if (IsCached(key, model)) await client.StartAsync(ct);
            else
            {
                var selected = await ResolveAccountAsync(client, account, ct);
                if (!Cache(key, await ListModelsAsync(client, selected.SelectionId!, ct)).Any(m => m.Id == model))
                    throw new CopilotModelUnavailableException();
            }
            // Bind authentication before choosing the request model or sending any text.
            config.Model = null;
            sessionCreationStarted = true;
            session = await client.CreateSessionAsync(config, ct);
            var binding = await session.Rpc.GitHubAuth.SetCredentialsAsync(new SettableAuthInfoUser
                { Host = account.Host, Login = account.Login }, ct);
            var auth = await session.Rpc.GitHubAuth.GetStatusAsync(ct);
            if (!binding.Success || !auth.IsAuthenticated || auth.AuthType != AuthInfoType.User ||
                string.IsNullOrWhiteSpace(auth.Host) || string.IsNullOrWhiteSpace(auth.Login) ||
                !account.Matches(new(auth.Host, auth.Login))) throw new CopilotSignInRequiredException();
            var switched = await session.Rpc.Model.SwitchToAsync(model, requireAvailable: true, cancellationToken: ct);
            if (switched.Deferred == true || switched.ModelId != model)
                throw new CopilotModelUnavailableException();
            var response = await session.SendAndWaitAsync(new MessageOptions { Prompt = userText },
                timeout: TimeSpan.FromMinutes(2), cancellationToken: ct);
            return response?.Data.Content ?? throw new InvalidOperationException("Copilot returned no text.");
        }
        // Sign-in, credential, model or provider failures re-verify the account on the next turn.
        catch (Exception ex) when (ex is not OperationCanceledException) { _catalogs.TryRemove(key, out _); throw; }
        finally
        {
            // Cancellation of SendAndWait only cancels the local wait. Abort the remote turn
            // before deleting its session, including when session creation lost its response.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (session is not null)
                {
                    try { await session.AbortAsync(cleanup.Token); }
                    catch (Exception) { /* Deletion must still be attempted after an abort failure. */ }
                }
                if (sessionCreationStarted) await client.DeleteSessionAsync(config.SessionId!, cleanup.Token);
            }
            catch (Exception) { /* Stop the owned runtime below even if its RPC connection broke. */ }
            finally { await client.ForceStopAsync(); }
        }
    }

    private CopilotClient CreateClient(string dataDirectory)
    {
        var options = CreateClientOptions(dataDirectory);
        Directory.CreateDirectory(options.WorkingDirectory!);
        Directory.CreateDirectory(Path.Combine(dataDirectory, "config"));
        return _createClient(options);
    }

    private static CopilotAccount? Identity(AccountAllUsers entry) =>
        entry.AuthInfo is AuthInfoUser { Host: { } host, Login: { } login } &&
        !string.IsNullOrWhiteSpace(login) && login.Length <= 100 &&
        Uri.TryCreate(host, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == "/"
            ? new(uri.GetLeftPart(UriPartial.Authority), login) : null;

    private static async Task<AccountAllUsers> ResolveAccountAsync(CopilotClient client, CopilotAccount account, CancellationToken ct)
    {
        await client.StartAsync(ct);
        var matches = (await client.Rpc.Account.GetAllUsersAsync(ct))
            .Where(entry => Identity(entry) is { } identity && account.Matches(identity)).ToArray();
        if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].SelectionId)) throw new CopilotSignInRequiredException();
        return matches[0];
    }

    private static async Task<IReadOnlyList<PluginModelInfo>> ListModelsAsync(CopilotClient client, string selectionId, CancellationToken ct) =>
        (await client.Rpc.Models.ListAsync(selectionId: selectionId, cancellationToken: ct)).Models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id) &&
                (m.Policy is null || m.Policy.State == ModelPolicyState.Enabled))
            .DistinctBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => new PluginModelInfo(m.Id, string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
#pragma warning restore GHCP001
}
