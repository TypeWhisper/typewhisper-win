using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

public sealed class ApiServerController : IDisposable
{
    private readonly ILocalApiServer _server;
    private readonly ISettingsService _settings;
    private bool _initialized;
    private bool _disposed;

    public ApiServerController(ILocalApiServer server, ISettingsService settings)
    {
        _server = server;
        _settings = settings;
    }

    public bool IsRunning => _server.IsRunning;
    public int? ActivePort { get; private set; }
    public string? ErrorMessage { get; private set; }

    public event Action? StateChanged;

    public void Initialize()
    {
        if (_initialized)
            return;

        _settings.SettingsChanged += OnSettingsChanged;
        _initialized = true;
        Apply(_settings.Current);
    }

    public void Refresh() => NotifyStateChanged();

    private void OnSettingsChanged(AppSettings settings) => Apply(settings);

    private void Apply(AppSettings settings)
    {
        if (!settings.ApiServerEnabled)
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
        catch (Exception ex)
        {
            ActivePort = null;
            ErrorMessage = ex.Message;
            try { _server.Stop(); } catch { }
        }

        NotifyStateChanged();
    }

    private void Stop(bool clearError)
    {
        if (_server.IsRunning)
            _server.Stop();

        ActivePort = null;
        if (clearError)
            ErrorMessage = null;

        NotifyStateChanged();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_initialized)
            _settings.SettingsChanged -= OnSettingsChanged;

        _disposed = true;
    }
}
