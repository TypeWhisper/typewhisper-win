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
/// The plugin's public constructor passes <see cref="AllowedInThisProcess"/>; the internal test constructor
/// leaves the refresh off, so unit tests never reach a provider.
/// </remarks>
internal sealed class ModelCatalogAutoRefresh(bool enabled, TimeProvider? clock = null) : IDisposable
{
    /// <summary>How old a saved list may get before activation refreshes it.</summary>
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);

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

    public void Dispose() => Cancel();

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
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            try { onFailure(ex); }
            catch (Exception) { }
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
        catch (AggregateException) { }
    }
}
