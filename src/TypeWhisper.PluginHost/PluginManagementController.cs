namespace TypeWhisper.PluginHost;

public sealed record ManagedPluginBinding(string Id, Func<bool> IsEnabled, Func<bool> IsBusy,
    Func<string?> Error, Func<bool, Task<string?>> ChangeEnabledAsync);

public sealed record ManagedPluginState(InstalledPluginPackage Package, bool Enabled,
    bool Supported, bool Busy, bool CanToggle, string? Error);

// The UI renders these states and forwards intentions. No dispatcher, native window,
// microphone, model download or desktop session is needed to exercise this controller.
public sealed class PluginManagementController
{
    private readonly string _root;
    private readonly Func<Task<IReadOnlyList<InstalledPluginPackage>>> _scan;
    private readonly IReadOnlyDictionary<string, ManagedPluginBinding> _bindings;
    private readonly Func<bool> _canChange;
    private readonly object _sync = new();
    private IReadOnlyList<InstalledPluginPackage> _packages = [];
    private string? _operationPath;
    private long _generation;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly Dictionary<string, (string Message, bool Enabled)> _errors = new(StringComparer.Ordinal);

    public PluginManagementController(string root, IEnumerable<ManagedPluginBinding> bindings,
        Func<bool> canChange, Func<Task<IReadOnlyList<InstalledPluginPackage>>> scan)
    {
        _root = Path.GetFullPath(root);
        _bindings = bindings.ToDictionary(binding => binding.Id, StringComparer.Ordinal);
        _canChange = canChange;
        _scan = scan;
    }

    public async Task RefreshAsync()
    {
        var generation = Interlocked.Increment(ref _generation);
        IReadOnlyList<InstalledPluginPackage> packages;
        try { packages = await _scan(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { packages = [new(_root, null, "Plugin discovery failed: " + ex.Message)]; }
        lock (_sync)
        {
            if (generation != _generation) return;
            _packages = packages.ToArray();
        }
    }

    public IReadOnlyList<ManagedPluginState> Snapshot()
    {
        lock (_sync)
            return _packages.Select(package =>
            {
                var binding = Resolve(package);
                var busy = binding?.IsBusy() == true || _operationPath == package.Directory;
                var enabled = binding?.IsEnabled() == true;
                if (_errors.TryGetValue(package.Directory, out var previous) && previous.Enabled != enabled)
                    _errors.Remove(package.Directory);
                var error = package.Error ?? binding?.Error() ?? (_errors.TryGetValue(package.Directory, out var cached) ? cached.Message : null);
                if (binding is null && error is null) error = "This plugin's capabilities are not connected to WinUI yet.";
                return new ManagedPluginState(package, binding?.IsEnabled() == true, binding is not null, busy,
                    binding is not null && package.Error is null && !busy && _operationPath is null && _canChange(), error);
            }).ToArray();
    }

    public async Task<string?> SetEnabledAsync(string directory, bool enabled)
    {
        if (!await _operation.WaitAsync(0)) return "A plugin operation is already in progress.";
        try
        {
            ManagedPluginBinding? binding;
            lock (_sync)
            {
                var package = _packages.SingleOrDefault(package => package.Directory == directory);
                binding = package is null ? null : Resolve(package);
                if (package is null || binding is null || package.Error is not null)
                    return package?.Error ?? "This plugin cannot be changed by this host.";
                if (!_canChange()) return "Finish the current recording or processing operation before changing plugins.";
                if (binding.IsBusy()) return "A plugin operation is already in progress.";
                _operationPath = directory;
                _errors.Remove(directory);
            }
            string? error;
            try { error = await binding.ChangeEnabledAsync(enabled); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { error = "Plugin operation failed: " + ex.Message; }
            lock (_sync)
            {
                // Runtime-owned errors clear when another surface successfully retries.
                // Cache only failures that the runtime does not expose itself.
                if (error is not null && binding.Error() is null) _errors[directory] = (error, binding.IsEnabled());
            }
            return error;
        }
        finally
        {
            lock (_sync) _operationPath = null;
            _operation.Release();
        }
    }

    private ManagedPluginBinding? Resolve(InstalledPluginPackage package)
    {
        if (package.Manifest is not { } manifest || !_bindings.TryGetValue(manifest.Id, out var binding)) return null;
        var expected = Path.Combine(_root, manifest.Id);
        return string.Equals(Path.GetFullPath(package.Directory), expected,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? binding : null;
    }
}
