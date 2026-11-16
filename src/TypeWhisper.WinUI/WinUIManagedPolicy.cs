using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

// One machine-policy service for the whole process. Stores and services consult Current; nothing
// here writes policy or user preferences. Changed is raised on a background thread.
internal static class WinUIManagedPolicy
{
    private static readonly object Lock = new();
    private static RegistryManagedPolicySource? _source;
    private static ManagedPolicyService? _service;
    private static IReadOnlyList<ManagedPolicyDefinition> _additionalDefinitions = [];

    internal static ManagedPolicySnapshot Current => _service?.Current ?? ManagedPolicySnapshot.Empty;
    internal static bool Watching => _source?.IsWatching == true;
    internal static event Action<ManagedPolicySnapshot>? Changed;

    // Reads policy before any store, plugin or background work consults it. Idempotent.
    internal static ManagedPolicySnapshot Initialize()
    {
        lock (Lock)
        {
            if (_service is not null) return _service.Current;
            _source = new RegistryManagedPolicySource();
            _service = new ManagedPolicyService(_source, reportFailure: (stage, error) => AppDiagnostics.Write(stage, error));
            _service.Changed += snapshot => Changed?.Invoke(snapshot);
            _service.UseAdditionalDefinitions(_additionalDefinitions);
            if (!_source.StartWatching()) AppDiagnostics.Write("policy.watch-unavailable");
            return _service.Start();
        }
    }

    // The keys optional modules declare are validated, shown and reported like the host's own. The policy is read
    // again right away, so modules see their values once they have contributed.
    internal static void UseAdditionalDefinitions(IReadOnlyList<ManagedPolicyDefinition> definitions)
    {
        ManagedPolicyService? service;
        lock (Lock)
        {
            _additionalDefinitions = definitions;
            service = _service;
        }
        try { service?.UseAdditionalDefinitions(definitions); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("policy.refresh-failed", ex); }
    }

    // Resume and session switches can hide registry notifications; re-read off the calling thread.
    internal static void RequestRefresh()
    {
        var service = _service;
        if (service is null) return;
        _ = Task.Run(() =>
        {
            try { service.Refresh(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("policy.refresh-failed", ex); }
        });
    }

    internal static void Shutdown()
    {
        lock (Lock)
        {
            _service?.Dispose();
            _source?.Dispose();
        }
    }
}
