using Microsoft.UI.Dispatching;
using TypeWhisper.Core.Services.Sync;

namespace TypeWhisper.WinUI;

internal static partial class WinUICloudSync
{
    private static PersistedCloudFolderSync? _service;
    private static DispatcherQueue? _dispatcher;
    private static DispatcherQueueTimer? _timer;
    private static readonly CancellationTokenSource Lifetime = new();
    private static Task _operation = Task.CompletedTask;
    private static bool _closing;
    private static int _heldForReview;
    internal static bool ChoosingFolder { get; set; }
    internal static Task WaitForIdleAsync() => _operation;
    internal static bool Busy { get; private set; }
    internal static bool CanUse => PremiumView.Access.Current.Commercial;
    internal static CloudFolderSyncPreferences Preferences => _service?.Preferences ?? new();
    internal static string Status { get; private set; } = Loc.T("Choose the same cloud folder on both devices.");
    internal static event Action? Changed;
    internal static event Action? DataChanged;

    // Startup starts automatic sync after its license and account checks; a page opened before them only loads it.
    internal static bool AwaitStartupChecks { get; set; }
    internal static void Open(DispatcherQueue dispatcher)
    {
        if (AwaitStartupChecks) Load(dispatcher);
        else Initialize(dispatcher);
    }

    // When startup fails before it starts sync, a page opened meanwhile starts it once the license check has finished,
    // as a page opened afterwards would.
    internal static void EndStartupWait()
    {
        AwaitStartupChecks = false;
        if (_dispatcher is { } dispatcher) Initialize(dispatcher);
    }

    private static void Load(DispatcherQueue dispatcher)
    {
        if (_dispatcher is not null || _closing) return;
        _dispatcher = dispatcher;
        try { _service = new(WinUIProfile.Root); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.T("Could not load sync preferences: {0}", ex.Message); }
    }

    internal static void Initialize(DispatcherQueue dispatcher)
    {
        Load(dispatcher);
        if (_timer is not null || _closing) return;
        _timer = dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(15);
        _timer.Tick += (_, _) => { if (!ChoosingFolder && Preferences.Enabled && CanUse) _ = SyncAsync(); };
        _timer.Start();
        PremiumView.Access.Changed += AccessChanged;
        // A page opened during startup shows the checked license.
        Changed?.Invoke();
        if (Preferences.Enabled && CanUse) _ = SyncAsync();
    }
    private static void AccessChanged() { Changed?.Invoke(); }

    internal static void Configure(string? folder, bool enabled)
    {
        if (_closing || Busy) return;
        try
        {
            if (_service is null) throw new InvalidOperationException(Loc.T("Sync storage is unavailable."));
            if (enabled && !CanUse) throw new InvalidOperationException(Loc.T("Cloud folder sync requires a commercial license."));
            _service.Configure(folder, enabled);
            Status = enabled ? Loc.T("Automatic sync is on. Checking every 15 seconds.") : Loc.T("Sync paused. Local and cloud files are kept.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = ex.Message; }
        Changed?.Invoke();
        if (Preferences.Enabled && CanUse) _ = SyncAsync();
    }

    internal static Task SyncAsync()
    {
        if (_closing || Busy || _service is null || !Preferences.Enabled || !CanUse) return _operation;
        Busy = true; Status = Loc.T("Synchronizing…"); Changed?.Invoke();
        return _operation = RunAsync();
    }
    private static async Task RunAsync()
    {
        try
        {
            var result = await _service!.SyncAsync(() => CanUse && !_closing, (action, changed) =>
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_dispatcher!.TryEnqueue(() =>
                {
                    try { action(); completion.SetResult(); }
                    catch (Exception ex) { completion.SetException(ex); }
                    finally { if (changed) DataChanged?.Invoke(); }
                })) completion.SetException(new InvalidOperationException(Loc.T("The app is closing.")));
                return completion.Task;
            }, Lifetime.Token);
            Status = Loc.T("Synced at {0:t} · {1} sent · {2} applied", result.SyncedAt.ToLocalTime(), result.OperationsWritten, result.MutationsApplied);
            // Received entries saved turned off stay listed until the app restarts, not just until the next pass.
            _heldForReview += result.HeldForReview;
            if (result.HeldForReview > 0) AppDiagnostics.Write("sync.lexicon.held-for-review");
            if (await SyncHistoryAsync() is { } history) Status += " · " + history;
            if (_heldForReview > 0)
                Status += " · " + Loc.T("{0} received entries use a regular expression, line breaks or the clipboard and were turned off. Review them in Dictionary or Snippets and turn them on if you trust them.", _heldForReview);
        }
        catch (OperationCanceledException) { Status = Loc.T("Sync canceled."); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.T("Sync could not finish: {0}", ex.Message); }
        finally { Busy = false; Changed?.Invoke(); }
    }
    internal static Task ShutdownAsync()
    {
        _closing = true; _timer?.Stop(); Lifetime.Cancel(); PremiumView.Access.Changed -= AccessChanged;
        return _operation;
    }
}
