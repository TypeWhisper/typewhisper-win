using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides api server controller behavior.
/// </summary>
public sealed class ApiServerController : IDisposable
{
    private readonly ILocalApiServer _server;
    private readonly ISettingsService _settings;
    private readonly Func<bool> _automationEnabledProvider;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the ApiServerController class.
    /// </summary>
    public ApiServerController(
        ILocalApiServer server,
        ISettingsService settings,
        Func<bool>? automationEnabledProvider = null)
    {
        _server = server;
        _settings = settings;
        _automationEnabledProvider = automationEnabledProvider ?? HttpApiService.IsAutomationEnvironmentEnabled;
    }

    /// <summary>
    /// Gets whether the service is currently running.
    /// </summary>
    public bool IsRunning => _server.IsRunning;
    /// <summary>
    /// Gets or sets the active port value.
    /// </summary>
    public int? ActivePort { get; private set; }
    /// <summary>
    /// Gets or sets the error message value.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Raised when state changes.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// Initializes resources required before use.
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
            return;

        _settings.SettingsChanged += OnSettingsChanged;
        _initialized = true;
        Apply(_settings.Current);
    }

    /// <summary>
    /// Refreshes
    /// </summary>
    public void Refresh() => NotifyStateChanged();

    private void OnSettingsChanged(AppSettings settings) => Apply(settings);

    private void Apply(AppSettings settings)
    {
        if (!settings.ApiServerEnabled && !_automationEnabledProvider())
        {
            Stop(clearError: true);
            return;
        }

        if (settings.ApiServerPort is < 1 or > 65535)
        {
            Stop(clearError: false);
            ErrorMessage = $"Invalid API server port: {settings.ApiServerPort}";
            NotifyStateChanged();
            return;
        }

        if (_server.IsRunning && ActivePort == settings.ApiServerPort)
        {
            ErrorMessage = null;
            NotifyStateChanged();
            return;
        }

        Stop(clearError: true);

        try
        {
            _server.Start(settings.ApiServerPort);
            ActivePort = settings.ApiServerPort;
            ErrorMessage = null;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ActivePort = null;
            ErrorMessage = ex.Message;
            try
            {
                _server.Stop();
            }
            catch (Exception stopEx) when (IsRecoverable(stopEx))
            {
                ErrorMessage = $"{ErrorMessage}; cleanup failed: {stopEx.Message}";
            }
        }

        NotifyStateChanged();
    }

    private void Stop(bool clearError)
    {
        _server.Stop();

        ActivePort = null;
        if (clearError)
            ErrorMessage = null;

        NotifyStateChanged();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

    private static bool IsRecoverable(Exception ex) =>
        ex is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException
            and not BadImageFormatException;

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        if (_initialized)
            _settings.SettingsChanged -= OnSettingsChanged;

        _disposed = true;
    }
}
