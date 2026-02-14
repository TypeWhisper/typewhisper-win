using Velopack;
using Velopack.Sources;

namespace TypeWhisper.Windows.Services;

public sealed class UpdateService
{
    private UpdateManager? _updateManager;
    private UpdateInfo? _pendingUpdate;

    public bool IsUpdateAvailable => _pendingUpdate is not null;
    public string? AvailableVersion => _pendingUpdate?.TargetFullRelease?.Version?.ToString();

    public event EventHandler? UpdateAvailable;

    public void Initialize(string? updateUrl)
    {
        if (string.IsNullOrEmpty(updateUrl)) return;

        try
        {
            _updateManager = new UpdateManager(new SimpleWebSource(updateUrl));
        }
        catch
        {
            // Update check is best-effort
        }
    }

    public async Task CheckForUpdatesAsync()
    {
        if (_updateManager is null) return;

        try
        {
            _pendingUpdate = await _updateManager.CheckForUpdatesAsync();
            if (_pendingUpdate is not null)
                UpdateAvailable?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Silent fail - update check is non-critical
        }
    }

    public async Task DownloadAndApplyAsync()
    {
        if (_updateManager is null || _pendingUpdate is null) return;

        await _updateManager.DownloadUpdatesAsync(_pendingUpdate);
        _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
    }
}
