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
    public bool RestartRequired => _entries.Any(entry => store.PendingRestart(entry.Id));
    public IReadOnlyList<PortableCatalogEntry> Available => _entries.Where(IsAvailable).ToArray();
    public bool IsAvailable(PortableCatalogEntry entry) => entry.Supports(hostVersion, architecture) &&
        !store.PendingRestart(entry.Id) && Version.TryParse(store.InstalledVersion(entry.Id), out var installed) &&
        Version.TryParse(entry.Version, out var offered) && offered > installed;
    public bool HasUpdate(string id) => _entries.Any(entry => entry.Id == id && IsAvailable(entry));

    public void AcceptCatalog(IReadOnlyList<PortableCatalogEntry> entries)
    {
        _entries = entries.ToArray();
        Changed?.Invoke();
    }

    public async Task RefreshAsync()
    {
        if (_shutdown.IsCancellationRequested || !await _gate.WaitAsync(0)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var entries = await catalog.FetchAsync(timeout.Token);
            if (Status?.StartsWith("Update check unavailable", StringComparison.Ordinal) == true) Status = null;
            AcceptCatalog(entries);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_shutdown.IsCancellationRequested) Status = "Update check unavailable. Try opening Integrations again."; }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    public async Task UpdateAsync(string? pluginId = null)
    {
        if (_shutdown.IsCancellationRequested || Busy) return;
        Busy = true;
        Status = "Preparing updates...";
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
                Status = $"Updating {entry.Name} ({i + 1}/{plan.Length})...";
                Changed?.Invoke();
                try { await store.InstallAsync(entry, ct: _shutdown.Token); completed++; }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { failed.Add(entry.Name); }
            }
            Status = failed.Count > 0 ? $"{completed} updated. Could not update: {string.Join(", ", failed)}. Try again."
                : RestartRequired ? "Updates ready. Restart TypeWhisper to apply them." : "Plugins are up to date.";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally { Busy = false; _gate.Release(); Changed?.Invoke(); }
    }

    public async Task ShutdownAsync()
    {
        await _shutdown.CancelAsync();
        await _gate.WaitAsync();
        _gate.Release();
    }
}
