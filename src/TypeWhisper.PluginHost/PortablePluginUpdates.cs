namespace TypeWhisper.PluginHost;

/// <summary>Shared installed-update state and one coordinated operation for all eligible packages.</summary>
public sealed class PortablePluginUpdates(PortablePluginStore store, PortablePluginCatalog catalog, Version hostVersion, string architecture)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private IReadOnlyList<PortableCatalogEntry> _entries = [];
    public event Action? Changed;
    public bool Busy { get; private set; }
    public string? Status { get; private set; }
    /// <summary>Whether a catalog request is running.</summary>
    public bool Checking { get; private set; }
    /// <summary>Whether a catalog has been received since startup.</summary>
    public bool Checked { get; private set; }
    /// <summary>Whether the latest catalog request failed.</summary>
    public bool CheckFailed { get; private set; }
    /// <summary>Names of the packages the latest update run could not install.</summary>
    public IReadOnlyList<string> Failed { get; private set; } = [];
    /// <summary>The package being installed and its position in the running update.</summary>
    public (string Name, int Index, int Total)? Progress { get; private set; }
    /// <summary>How many packages the latest update run installed.</summary>
    public int Updated { get; private set; }
    public bool RestartRequired => _entries.Any(entry => store.PendingRestart(entry.Id));
    public IReadOnlyList<PortableCatalogEntry> Available => _entries.Where(IsAvailable).ToArray();
    public bool IsAvailable(PortableCatalogEntry entry) => entry.Supports(hostVersion, architecture) &&
        !store.PendingRestart(entry.Id) && Version.TryParse(store.InstalledVersion(entry.Id), out var installed) &&
        Version.TryParse(entry.Version, out var offered) && offered > installed;
    public bool HasUpdate(string id) => _entries.Any(entry => entry.Id == id && IsAvailable(entry));

    public void AcceptCatalog(IReadOnlyList<PortableCatalogEntry> entries)
    {
        _entries = entries.ToArray();
        Checked = true; CheckFailed = false;
        Changed?.Invoke();
    }

    /// <summary>Fetches the catalog; true only when this call received a new one.</summary>
    public async Task<bool> RefreshAsync()
    {
        if (_shutdown.IsCancellationRequested || !await _gate.WaitAsync(0)) return false;
        Checking = true;
        Changed?.Invoke();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var entries = await catalog.FetchAsync(timeout.Token);
            if (Status?.StartsWith("Update check unavailable", StringComparison.Ordinal) == true) Status = null;
            AcceptCatalog(entries);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_shutdown.IsCancellationRequested) { CheckFailed = true; Status = "Update check unavailable. Try opening Integrations again."; } return false; }
        finally { Checking = false; _gate.Release(); Changed?.Invoke(); }
    }

    public async Task UpdateAsync(string? pluginId = null)
    {
        if (_shutdown.IsCancellationRequested || Busy) return;
        Busy = true;
        Status = "Preparing updates...";
        Failed = []; Updated = 0;
        Changed?.Invoke();
        await _gate.WaitAsync();
        var failed = new List<string>();
        var completed = 0;
        try
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            var plan = Available.Where(entry => pluginId is null || entry.Id == pluginId).ToArray();
            for (var i = 0; i < plan.Length; i++)
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                var entry = plan[i];
                if (!IsAvailable(entry)) continue;
                Progress = (entry.Name, i + 1, plan.Length);
                Status = $"Updating {entry.Name} ({i + 1}/{plan.Length})...";
                Changed?.Invoke();
                try { await store.InstallAsync(entry, ct: _shutdown.Token); completed++; }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { failed.Add(entry.Name); }
            }
            Failed = failed; Updated = completed;
            Status = failed.Count > 0 ? $"{completed} updated. Could not update: {string.Join(", ", failed)}. Try again."
                : RestartRequired ? "Updates ready. Restart TypeWhisper to apply them." : "Plugins are up to date.";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally { Busy = false; Progress = null; _gate.Release(); Changed?.Invoke(); }
    }

    public async Task ShutdownAsync()
    {
        await _shutdown.CancelAsync();
        await _gate.WaitAsync();
        _gate.Release();
    }
}
