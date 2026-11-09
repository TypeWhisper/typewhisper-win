namespace TypeWhisper.Plugin.Shared;

/// <summary>
/// Refreshes a plugin's fetched model list in the background once the saved copy is a day old.
/// Providers retire models, and a list that only refreshes when the user presses a button keeps
/// offering them for months.
/// </summary>
/// <remarks>
/// The plugin keeps the list, its fetch date and the request itself. This type only decides when a
/// list is stale and runs at most one refresh at a time. To drop a result that arrives too late, the
/// plugin calls <see cref="Cancel"/> on deactivation and on a key change, and its apply step checks the
/// token under the same lock or gate that guards the key. A cancelled token then means "discard".
/// While the plugin is active, <see cref="StartRechecks"/> checks the list again every
/// <see cref="RecheckInterval"/>, because the app often runs for days without activating plugins again.
/// The plugin's public constructor passes <see cref="AllowedInThisProcess"/>; the internal test constructor
/// leaves the refresh off, so unit tests never reach a provider.
/// </remarks>
internal sealed class ModelCatalogAutoRefresh(bool enabled, TimeProvider? clock = null) : IDisposable
{
    /// <summary>How old a saved list may get before activation refreshes it.</summary>
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);

    /// <summary>
    /// How often an active plugin checks whether its list became stale. It also paces another attempt after a
    /// failed refresh, for example while the PC is offline.
    /// </summary>
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// AppContext switch that turns the refresh of every public plugin constructor off. Test projects set it
    /// with <c>&lt;RuntimeHostConfigurationOption&gt;</c>: a package test loads the built plugin through its public
    /// constructor, and its refresh would otherwise reach the real provider. The app never sets it.
    /// </summary>
    internal const string DisableSwitchName = "TypeWhisper.Plugins.DisableAutomaticModelRefresh";

    /// <summary>What a plugin's public constructor passes as <c>enabled</c>.</summary>
    internal static bool AllowedInThisProcess =>
        !(AppContext.TryGetSwitch(DisableSwitchName, out var disabled) && disabled);

    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task _current = Task.CompletedTask;
    private ITimer? _recheck;

    internal bool Enabled { get; } = enabled;
    internal TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    internal DateTimeOffset UtcNow => Clock.GetUtcNow();

    /// <summary>The running refresh, or the last one. Tests await it.</summary>
    internal Task Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// True when the list has no fetch date, is at least <see cref="MaximumAge"/> old, or has a date in
    /// the future (a clock that was set back would otherwise keep the list forever).
    /// </summary>
    internal bool IsStale(DateTimeOffset? fetchedAt)
    {
        if (fetchedAt is not { } at)
            return true;

        var age = UtcNow - at;
        return age < TimeSpan.Zero || age >= MaximumAge;
    }

    /// <summary>
    /// Cancels a running refresh and runs <paramref name="refresh"/> on the thread pool.
    /// <paramref name="onFailure"/> receives any exception except the cancellation of this refresh;
    /// the plugin logs it and keeps its current list. Returns false when the refresh is disabled.
    /// </summary>
    internal bool Start(Func<CancellationToken, Task> refresh, Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(onFailure);
        if (!Enabled)
            return false;

        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _cancellation;
            _cancellation = cancellation;
            _current = Task.Run(() => RunAsync(refresh, onFailure, token), CancellationToken.None);
        }

        CancelSource(previous);
        return true;
    }

    /// <summary>Cancels the running refresh, if any. Its result must then be discarded.</summary>
    internal void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _cancellation;
            _cancellation = null;
        }

        CancelSource(cancellation);
    }

    /// <summary>
    /// Calls <paramref name="startIfStale"/> every <see cref="RecheckInterval"/> until <see cref="StopRechecks"/>.
    /// The plugin passes the same check it runs on activation, so a list that ages while the app keeps running
    /// reloads, and a failed refresh is tried again. A check is skipped while a refresh runs, so it never
    /// cancels one. Replaces earlier rechecks; does nothing when the refresh is disabled.
    /// </summary>
    internal void StartRechecks(Action startIfStale)
    {
        ArgumentNullException.ThrowIfNull(startIfStale);
        if (!Enabled)
            return;

        ITimer? previous;
        lock (_gate)
        {
            previous = _recheck;
            _recheck = Clock.CreateTimer(_ => Recheck(startIfStale), null, RecheckInterval, RecheckInterval);
        }

        previous?.Dispose();
    }

    /// <summary>
    /// Stops the rechecks. The plugin calls it on deactivation: a running timer would keep the plugin, and
    /// with it its assembly, alive.
    /// </summary>
    internal void StopRechecks()
    {
        ITimer? recheck;
        lock (_gate)
        {
            recheck = _recheck;
            _recheck = null;
        }

        recheck?.Dispose();
    }

    public void Dispose()
    {
        StopRechecks();
        Cancel();
    }

    private void Recheck(Action startIfStale)
    {
        if (!Current.IsCompleted)
            return;

        try { startIfStale(); }
        catch (Exception)
        {
            // A timer callback must not throw; the next check tries again.
        }
    }

    private static async Task RunAsync(
        Func<CancellationToken, Task> refresh,
        Action<Exception> onFailure,
        CancellationToken token)
    {
        try
        {
            await refresh(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancelled on purpose: the plugin no longer wants this result.
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            try { onFailure(ex); }
            catch (Exception)
            {
                // The failure was only to be logged; a background refresh must not end with an unobserved exception.
            }
        }
        catch (Exception)
        {
            // A refresh that failed after it was cancelled has nothing left to report.
        }
    }

    // The source is not disposed: the refresh may still read or register on its token, and a
    // source without a timer holds nothing that needs releasing.
    private static void CancelSource(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;

        try { cancellation.Cancel(); }
        catch (AggregateException)
        {
            // Thrown by a token registration; the refresh is cancelled either way.
        }
    }
}
