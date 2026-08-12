using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AuthenticatedCli;

/// <summary>
/// Provides prompt processing through existing authenticated provider CLI sessions.
/// </summary>
public sealed class AuthenticatedCliPlugin :
    ITypeWhisperPlugin,
    IAdditionalLlmProvidersProvider,
    IPluginSettingsActivity
{
    internal const int MaximumInputBytes = 512 * 1024;
    internal const int MaximumResultBytes = 512 * 1024;
    internal const int MaximumStandardOutputBytes = 1024 * 1024;
    internal const int MaximumStandardErrorBytes = 64 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan AvailabilityRefreshInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan RequestRefreshAge = TimeSpan.FromMinutes(6);

    private readonly object _stateLock = new();
    private readonly Dictionary<string, CliAvailabilitySnapshot> _snapshots;
    private readonly Dictionary<string, string?> _selectedExecutables = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<ILlmProviderPlugin> _roles;
    private readonly CliExecutableDiscovery _discovery;
    private readonly ICliProcessRunner _runner;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CancellationTokenSource? _lifetimeCancellation;
    private Task? _pollTask;
    private IPluginHostServices? _host;
    private bool _disposed;

    /// <summary>
    /// Initializes a new plugin instance.
    /// </summary>
    public AuthenticatedCliPlugin()
        : this(new CliExecutableDiscovery(), new CliProcessRunner())
    {
    }

    internal AuthenticatedCliPlugin(CliExecutableDiscovery discovery, ICliProcessRunner runner)
    {
        _discovery = discovery;
        _runner = runner;
        _snapshots = CliProviderDescriptor.All.ToDictionary(
            descriptor => descriptor.Key,
            _ => CliAvailabilitySnapshot.Initial,
            StringComparer.OrdinalIgnoreCase);
        _roles = CliProviderDescriptor.All
            .Select(descriptor => (ILlmProviderPlugin)new AuthenticatedCliProviderRole(this, descriptor))
            .ToList();
    }

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.authenticated-cli";

    /// <inheritdoc />
    public string PluginName => "Authenticated Provider CLIs";

    /// <inheritdoc />
    public string PluginVersion => "1.0.0";

    /// <inheritdoc />
    public IReadOnlyList<ILlmProviderPlugin> AdditionalLlmProviders => _roles;

    /// <inheritdoc />
    public event Action<string?>? SettingsActivityChanged;

    internal event EventHandler? AvailabilityChanged;

    /// <inheritdoc />
    public double? SettingsProgress => null;

    internal IPluginLocalization? Localization => _host?.Localization;

    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host = host;
        foreach (var descriptor in CliProviderDescriptor.All)
            _selectedExecutables[descriptor.Key] = host.GetSetting<string>(SelectedExecutableSetting(descriptor));

        var cancellation = new CancellationTokenSource();
        _lifetimeCancellation = cancellation;
        _pollTask = Task.Run(
            () => PollAvailabilityAsync(cancellation.Token),
            CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeactivateAsync()
    {
        var cancellation = _lifetimeCancellation;
        var pollTask = _pollTask;
        _lifetimeCancellation = null;
        _pollTask = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            if (pollTask is not null)
            {
                try
                {
                    await pollTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the plugin is deactivated.
                }
                catch (Exception ex)
                {
                    _host?.Log(
                        PluginLogLevel.Warning,
                        $"event=availability-monitor-stop type={ex.GetType().Name}");
                }
            }
            cancellation.Dispose();
        }

        _host = null;
    }

    /// <inheritdoc />
    public UserControl? CreateSettingsView() => new AuthenticatedCliSettingsView(this);

    internal string GetString(string key, params object[] args)
    {
        var localization = _host?.Localization;
        if (localization is not null)
            return args.Length == 0 ? localization.GetString(key) : localization.GetString(key, args);

        return key switch
        {
            "Provider.Codex" => "Codex CLI",
            "Provider.Claude" => "Claude Code CLI",
            "Provider.Antigravity" => "Antigravity CLI",
            "Model.Default" => "Provider default",
            _ => key
        };
    }

    internal CliAvailabilitySnapshot GetSnapshot(CliProviderDescriptor descriptor)
    {
        lock (_stateLock)
            return _snapshots[descriptor.Key];
    }

    internal async Task RefreshFromSettingsAsync(CancellationToken cancellationToken = default) =>
        await RefreshAllAsync(notifyHost: true, cancellationToken).ConfigureAwait(false);

    internal async Task SelectExecutableAsync(
        CliProviderDescriptor descriptor,
        string? executablePath,
        CancellationToken cancellationToken = default)
    {
        var candidates = _discovery.FindCandidates(descriptor.ExecutableName);
        var selected = candidates.FirstOrDefault(candidate =>
            string.Equals(candidate, executablePath, StringComparison.OrdinalIgnoreCase));
        lock (_stateLock)
            _selectedExecutables[descriptor.Key] = selected;
        _host?.SetSetting(SelectedExecutableSetting(descriptor), selected);
        await RefreshOneAsync(descriptor, notifyHost: true, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> ProcessAsync(
        CliProviderDescriptor descriptor,
        string systemPrompt,
        string userText,
        string model,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(model, "default", StringComparison.Ordinal))
        {
            throw new PluginRequestException(
                "The selected model is not supported by the provider CLI.",
                PluginRequestFailureKind.InvalidRequest,
                isTransient: false);
        }

        var snapshot = GetSnapshot(descriptor);
        if (DateTimeOffset.UtcNow - snapshot.CheckedAt > RequestRefreshAge)
            snapshot = await RefreshOneAsync(descriptor, notifyHost: true, cancellationToken).ConfigureAwait(false);

        if (snapshot.State != CliAvailabilityState.Ready || snapshot.ExecutablePath is null)
            throw CreateAvailabilityFailure(descriptor, snapshot.State);

        var envelope = new CliPromptEnvelope(
            "typewhisper.prompt-processing.v1",
            systemPrompt,
            userText);
        var standardInput = JsonSerializer.Serialize(envelope, CliJsonContext.Default.CliPromptEnvelope);
        if (Encoding.UTF8.GetByteCount(standardInput) > MaximumInputBytes)
        {
            throw new PluginRequestException(
                "The workflow input is too large for provider CLI processing.",
                PluginRequestFailureKind.RequestTooLarge,
                isTransient: false);
        }

        var tempDirectory = CreateTempDirectory();
        try
        {
            var schemaPath = Path.Combine(tempDirectory, "result.schema.json");
            await File.WriteAllTextAsync(
                schemaPath,
                CliProviderDescriptor.ResultSchema,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            var arguments = descriptor.CreateInvocationArguments(tempDirectory, schemaPath);
            var result = await _runner.RunAsync(
                new CliProcessRequest(
                    snapshot.ExecutablePath,
                    arguments,
                    standardInput,
                    tempDirectory,
                    descriptor.ProviderEnvironmentVariables,
                    RequestTimeout,
                    MaximumStandardOutputBytes,
                    MaximumStandardErrorBytes),
                cancellationToken).ConfigureAwait(false);

            LogProcessMetadata(descriptor, result, "request");
            if (result.ExitCode != 0)
            {
                var failure = ClassifyFailure(descriptor, result.StandardOutput, result.StandardError);
                if (failure.FailureKind == PluginRequestFailureKind.Authentication)
                {
                    SetSnapshot(
                        descriptor,
                        snapshot with
                        {
                            State = CliAvailabilityState.SignedOut,
                            CheckedAt = DateTimeOffset.UtcNow
                        },
                        notifyHost: true);
                }
                throw failure;
            }

            try
            {
                return descriptor.ParseSuccessfulOutput(result.StandardOutput);
            }
            catch (Exception ex) when (ex is JsonException or CliProtocolException or FormatException)
            {
                throw new PluginRequestException(
                    "The provider CLI returned an invalid structured result.",
                    PluginRequestFailureKind.Unknown,
                    isTransient: false,
                    innerException: ex);
            }
        }
        finally
        {
            await DeleteTempDirectoryAsync(tempDirectory).ConfigureAwait(false);
        }
    }

    private async Task PollAvailabilityAsync(CancellationToken cancellationToken)
    {
        await RefreshAllAsync(notifyHost: true, cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(AvailabilityRefreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await RefreshAllAsync(notifyHost: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshAllAsync(bool notifyHost, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SettingsActivityChanged?.Invoke(GetString("Settings.Refreshing"));
        try
        {
            foreach (var descriptor in CliProviderDescriptor.All)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await CheckAvailabilityAsync(descriptor, cancellationToken).ConfigureAwait(false);
                SetSnapshot(descriptor, snapshot, notifyHost);
            }
        }
        finally
        {
            _refreshGate.Release();
            SettingsActivityChanged?.Invoke(null);
        }
    }

    private async Task<CliAvailabilitySnapshot> RefreshOneAsync(
        CliProviderDescriptor descriptor,
        bool notifyHost,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await CheckAvailabilityAsync(descriptor, cancellationToken).ConfigureAwait(false);
            SetSnapshot(descriptor, snapshot, notifyHost);
            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<CliAvailabilitySnapshot> CheckAvailabilityAsync(
        CliProviderDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var candidates = _discovery.FindCandidates(descriptor.ExecutableName);
        if (candidates.Count == 0)
        {
            return new CliAvailabilitySnapshot(
                CliAvailabilityState.MissingExecutable,
                null,
                null,
                candidates,
                checkedAt);
        }

        string? configured;
        lock (_stateLock)
            configured = _selectedExecutables.GetValueOrDefault(descriptor.Key);
        var selected = candidates.FirstOrDefault(candidate =>
            string.Equals(candidate, configured, StringComparison.OrdinalIgnoreCase));
        if (configured is not null && selected is null)
        {
            return new CliAvailabilitySnapshot(
                CliAvailabilityState.SelectedExecutableMissing,
                configured,
                null,
                candidates,
                checkedAt);
        }

        if (selected is null && candidates.Count > 1)
        {
            return new CliAvailabilitySnapshot(
                CliAvailabilityState.AmbiguousExecutable,
                null,
                null,
                candidates,
                checkedAt);
        }

        selected ??= candidates[0];
        if (configured is null)
        {
            lock (_stateLock)
                _selectedExecutables[descriptor.Key] = selected;
            _host?.SetSetting(SelectedExecutableSetting(descriptor), selected);
        }

        if (!CliExecutableDiscovery.IsSafeNativeExecutable(selected, descriptor.ExecutableName))
        {
            return new CliAvailabilitySnapshot(
                CliAvailabilityState.UnsupportedExecutableType,
                null,
                null,
                candidates,
                checkedAt);
        }

        var tempDirectory = CreateTempDirectory();
        try
        {
            var versionProbe = await RunProbeAsync(
                descriptor,
                selected,
                descriptor.VersionArguments,
                tempDirectory,
                cancellationToken).ConfigureAwait(false);
            var versionOutput = versionProbe.StandardOutput + "\n" + versionProbe.StandardError;
            var version = descriptor.ParseVersion(versionOutput);
            if (versionProbe.ExitCode != 0 || version is null)
            {
                return new CliAvailabilitySnapshot(
                    CliAvailabilityState.UnsupportedVersion,
                    selected,
                    version,
                    candidates,
                    checkedAt);
            }

            var helpProbe = await RunProbeAsync(
                descriptor,
                selected,
                descriptor.HelpArguments,
                tempDirectory,
                cancellationToken).ConfigureAwait(false);
            var helpOutput = helpProbe.StandardOutput + "\n" + helpProbe.StandardError;
            if (helpProbe.ExitCode != 0 || !descriptor.HasRequiredCapabilities(helpOutput))
            {
                return new CliAvailabilitySnapshot(
                    CliAvailabilityState.UnsupportedVersion,
                    selected,
                    version,
                    candidates,
                    checkedAt);
            }

            if (!descriptor.SafetyControlsAvailable)
            {
                return new CliAvailabilitySnapshot(
                    CliAvailabilityState.SafetyControlsUnavailable,
                    selected,
                    version,
                    candidates,
                    checkedAt);
            }

            if (descriptor.AuthenticationArguments.Count == 0)
            {
                return new CliAvailabilitySnapshot(
                    CliAvailabilityState.AuthenticationUnknown,
                    selected,
                    version,
                    candidates,
                    checkedAt);
            }

            var authenticationProbe = await RunProbeAsync(
                descriptor,
                selected,
                descriptor.AuthenticationArguments,
                tempDirectory,
                cancellationToken).ConfigureAwait(false);
            var state = descriptor.IsAuthenticated(authenticationProbe.ExitCode, authenticationProbe.StandardOutput)
                ? CliAvailabilityState.Ready
                : authenticationProbe.ExitCode == 0
                    ? CliAvailabilityState.AuthenticationUnknown
                    : CliAvailabilityState.SignedOut;
            return new CliAvailabilitySnapshot(state, selected, version, candidates, checkedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is PluginRequestException
                                   or Win32Exception
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            _host?.Log(
                PluginLogLevel.Warning,
                $"provider={descriptor.Key} event=availability state=error type={ex.GetType().Name}");
            return new CliAvailabilitySnapshot(
                CliAvailabilityState.Error,
                selected,
                null,
                candidates,
                checkedAt);
        }
        finally
        {
            await DeleteTempDirectoryAsync(tempDirectory).ConfigureAwait(false);
        }
    }

    private async Task<CliProcessResult> RunProbeAsync(
        CliProviderDescriptor descriptor,
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            new CliProcessRequest(
                executablePath,
                arguments,
                "",
                workingDirectory,
                descriptor.ProviderEnvironmentVariables,
                ProbeTimeout,
                128 * 1024,
                64 * 1024),
            cancellationToken).ConfigureAwait(false);
        LogProcessMetadata(descriptor, result, "probe");
        return result;
    }

    private void SetSnapshot(
        CliProviderDescriptor descriptor,
        CliAvailabilitySnapshot snapshot,
        bool notifyHost)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = !_snapshots[descriptor.Key].HasSameCapabilities(snapshot);
            _snapshots[descriptor.Key] = snapshot;
        }

        if (!changed)
            return;

        _host?.Log(
            PluginLogLevel.Info,
            $"provider={descriptor.Key} event=availability state={snapshot.State} version={snapshot.Version ?? "unknown"}");
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        if (notifyHost)
            _host?.NotifyCapabilitiesChanged();
    }

    private void LogProcessMetadata(
        CliProviderDescriptor descriptor,
        CliProcessResult result,
        string operation)
    {
        _host?.Log(
            PluginLogLevel.Info,
            $"provider={descriptor.Key} event={operation} exit={result.ExitCode} elapsedMs={(long)result.Elapsed.TotalMilliseconds} stdoutBytes={result.StandardOutputBytes} stderrBytes={result.StandardErrorBytes}");
    }

    private static PluginRequestException ClassifyFailure(
        CliProviderDescriptor descriptor,
        string standardOutput,
        string standardError)
    {
        var text = (standardOutput + "\n" + standardError).ToLowerInvariant();
        if (ContainsAny(text, "not logged in", "not authenticated", "authentication", "sign in", "login required"))
        {
            return new PluginRequestException(
                $"{descriptor.Key} CLI is not signed in.",
                PluginRequestFailureKind.Authentication,
                isTransient: false);
        }

        if (ContainsAny(text, "rate limit", "too many requests", "quota"))
        {
            return new PluginRequestException(
                $"{descriptor.Key} CLI rate limit was reached.",
                PluginRequestFailureKind.RateLimit,
                isTransient: true);
        }

        if (ContainsAny(text, "permission", "forbidden", "subscription", "entitlement"))
        {
            return new PluginRequestException(
                $"{descriptor.Key} account is not permitted to process this request.",
                PluginRequestFailureKind.Permission,
                isTransient: false);
        }

        if (ContainsAny(text, "model", "invalid argument", "unknown option"))
        {
            return new PluginRequestException(
                $"{descriptor.Key} CLI rejected the request configuration.",
                PluginRequestFailureKind.InvalidRequest,
                isTransient: false);
        }

        if (ContainsAny(text, "network", "connection", "dns", "unreachable"))
        {
            return new PluginRequestException(
                $"{descriptor.Key} CLI could not reach the provider.",
                PluginRequestFailureKind.Network,
                isTransient: true);
        }

        return new PluginRequestException(
            $"{descriptor.Key} CLI exited without a valid result.",
            PluginRequestFailureKind.Unknown,
            isTransient: false);
    }

    private static PluginRequestException CreateAvailabilityFailure(
        CliProviderDescriptor descriptor,
        CliAvailabilityState state)
    {
        var failureKind = state == CliAvailabilityState.SignedOut
            ? PluginRequestFailureKind.Authentication
            : PluginRequestFailureKind.Configuration;
        return new PluginRequestException(
            $"{descriptor.Key} CLI is not ready: {state}.",
            failureKind,
            isTransient: false);
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private static string SelectedExecutableSetting(CliProviderDescriptor descriptor) =>
        $"selectedExecutable.{descriptor.Key}";

    private static string CreateTempDirectory()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TypeWhisper", "AuthenticatedCli"));
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task DeleteTempDirectoryAsync(string directory)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TypeWhisper", "AuthenticatedCli"));
        var fullPath = Path.GetFullPath(directory);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(fullPath))
                    Directory.Delete(fullPath, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 2)
                    await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetimeCancellation?.Cancel();
        _lifetimeCancellation?.Dispose();
        _lifetimeCancellation = null;
        _pollTask = null;
        _host = null;
    }

    private sealed class AuthenticatedCliProviderRole : ILlmProviderPlugin, ILlmProviderSelectionIdentity
    {
        private readonly AuthenticatedCliPlugin _owner;
        private readonly CliProviderDescriptor _descriptor;

        internal AuthenticatedCliProviderRole(
            AuthenticatedCliPlugin owner,
            CliProviderDescriptor descriptor)
        {
            _owner = owner;
            _descriptor = descriptor;
        }

        public string PluginId => _owner.PluginId;
        public string PluginName => ProviderName;
        public string PluginVersion => _owner.PluginVersion;
        public string LlmSelectionId => _descriptor.SelectionId;
        public string ProviderName => _owner.GetString(_descriptor.DisplayKey);
        public bool IsAvailable => _owner.GetSnapshot(_descriptor).State == CliAvailabilityState.Ready;
        public IReadOnlyList<PluginModelInfo> SupportedModels =>
            [new PluginModelInfo("default", _owner.GetString("Model.Default"))];

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public UserControl? CreateSettingsView() => _owner.CreateSettingsView();

        public Task<string> ProcessAsync(
            string systemPrompt,
            string userText,
            string model,
            CancellationToken ct) =>
            _owner.ProcessAsync(_descriptor, systemPrompt, userText, model, ct);

        public void Dispose()
        {
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(CliPromptEnvelope))]
internal partial class CliJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
