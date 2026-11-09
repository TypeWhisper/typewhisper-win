using System.IO;
using System.Net.Http;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Soniox;

public sealed partial class SonioxPlugin
{
    // After a failed background refresh, wait before trying again so an offline PC does not retry on every dictation.
    private static readonly TimeSpan CatalogRetryDelay = TimeSpan.FromMinutes(10);
    private long _catalogRetryAfterTicks;

    /// <summary>The running or last background model list refresh. Tests await it.</summary>
    internal Task CatalogRefreshTask => _catalogRefresh.Current;
    internal bool RefreshesModelsAutomatically => _catalogRefresh.Enabled;

    /// <summary>
    /// Loads the current speech-to-text and text-to-speech model lists for the selected region and caches them.
    /// A failed voice list keeps the voices cached for that region, because transcription does not depend on it.
    /// </summary>
    internal Task<SonioxCatalog> RefreshCatalogAsync(CancellationToken ct) => RefreshCatalogAsync(automatic: false, ct);

    private async Task<SonioxCatalog> RefreshCatalogAsync(bool automatic, CancellationToken ct)
    {
        var host = _host ?? throw new InvalidOperationException("Activate the plugin first.");
        var key = _apiKey ?? throw new InvalidOperationException("Soniox API key required.");
        var region = ResolveRegion(_region);

        await _catalogLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stt = SonioxModels.ParseSttModels(
                await GetCatalogJsonAsync($"{region.BaseUrl}/v1/models", key, ct).ConfigureAwait(false));
            if (stt.Count == 0)
                throw new InvalidDataException("Soniox returned no transcription models.");

            var cachedVoices = Volatile.Read(ref _catalog) is { } previous && previous.Region == region.Id ? previous.TtsModels : [];
            IReadOnlyList<SonioxTtsModel> tts;
            try
            {
                tts = SonioxModels.ParseTtsModels(
                    await GetCatalogJsonAsync($"{region.BaseUrl}/v1/tts-models", key, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                && ex is HttpRequestException or InvalidDataException or JsonException or TimeoutException)
            {
                host.Log(PluginLogLevel.Warning, $"Soniox voice list unavailable ({Describe(ex)}); keeping the cached voices.");
                tts = cachedVoices;
            }

            // In the background an empty voice list counts as unavailable, so spoken feedback keeps the voices it had.
            if (automatic && tts.Count == 0 && cachedVoices.Count > 0)
            {
                host.Log(PluginLogLevel.Warning, "Soniox listed no voices; keeping the cached voices.");
                tts = cachedVoices;
            }

            SonioxCatalog catalog;
            // Checked under the lock that guards the key. A key change, a region change and deactivation cancel the
            // background refresh before they change anything, so a cancelled result arrived too late.
            await _apiKeyWriteLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!ReferenceEquals(host, _host) || key != _apiKey || region.Id != _region)
                    throw new InvalidOperationException("The Soniox connection changed. Refresh the models again.");

                catalog = new SonioxCatalog(region.Id, _catalogRefresh.UtcNow, stt, tts);
                host.SetSetting(CatalogSettingKey, catalog);
                Volatile.Write(ref _catalog, catalog);
                Interlocked.Exchange(ref _catalogRetryAfterTicks, 0);
            }
            finally
            {
                _apiKeyWriteLock.Release();
            }

            host.NotifyCapabilitiesChanged();
            return catalog;
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    private async Task<string> GetCatalogJsonAsync(string url, string apiKey, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CatalogRequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthorization(request, apiKey);
        try
        {
            return await SendJsonAsync(request, "Soniox model list", timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Soniox did not return its model list in time.");
        }
    }

    /// <summary>
    /// Starts a background refresh when no list is cached for this region, the cached one is a day old or its date
    /// lies in the future. Runs on activation, every hour while the plugin is active (<see cref="RecheckCatalog"/>) and
    /// whenever Soniox is used; the current request keeps using the cached list or the built-in v6 fallback.
    /// </summary>
    internal void RefreshCatalogInBackgroundIfStale()
    {
        var host = _host;
        if (host is null || _apiKey is null || Volatile.Read(ref _disposed) != 0
            || !_catalogRefresh.IsStale(CurrentCatalog?.FetchedAt))
            return;
        // One refresh at a time, and after a failure a pause, so a dictation never waits for or repeats one.
        if (!_catalogRefresh.Current.IsCompleted
            || _catalogRefresh.UtcNow.UtcTicks < Interlocked.Read(ref _catalogRetryAfterTicks))
            return;

        _catalogRefresh.Start(ct => RefreshCatalogAsync(automatic: true, ct), ex => OnCatalogRefreshFailed(host, ex));
    }

    /// <summary>
    /// The hourly check. It runs on a timer thread, under the lock that deactivation and <see cref="Dispose"/> take to
    /// stop it: it either starts its refresh before they cancel and await it, or starts none. The key needs no lock here,
    /// as for the check a dictation runs: the refresh reads the key only when it runs and checks it again before it
    /// saves, and a key change cancels or replaces it.
    /// </summary>
    private void RecheckCatalog()
    {
        lock (_cleanupLock)
        {
            if (_catalogRechecksActive)
                RefreshCatalogInBackgroundIfStale();
        }
    }

    /// <summary>
    /// Stops the hourly checks and cancels a running refresh. <see cref="LastCleanupTask"/>, read afterwards, also covers
    /// a refresh that a check started just before.
    /// </summary>
    private void StopCatalogRechecks()
    {
        lock (_cleanupLock)
        {
            _catalogRechecksActive = false;
            _catalogRefresh.StopRechecks();
        }

        // After the lock no check starts a refresh, and one that got in first has already started it, so this cancels it.
        _catalogRefresh.Cancel();
    }

    /// <summary>Starts a refresh right away and replaces a running one. Used after a key or region change.</summary>
    private void StartCatalogRefresh()
    {
        var host = _host;
        if (host is null || _apiKey is null || Volatile.Read(ref _disposed) != 0)
            return;

        _catalogRefresh.Start(ct => RefreshCatalogAsync(automatic: true, ct), ex => OnCatalogRefreshFailed(host, ex));
    }

    private void OnCatalogRefreshFailed(IPluginHostServices host, Exception ex)
    {
        Interlocked.Exchange(ref _catalogRetryAfterTicks, (_catalogRefresh.UtcNow + CatalogRetryDelay).UtcTicks);
        host.Log(PluginLogLevel.Warning, $"Soniox model list refresh failed ({Describe(ex)}); keeping the current list.");
    }

    // Logs name the failure without the response body, which can echo request details.
    private static string Describe(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } status } ? $"HTTP {(int)status}" : ex.GetType().Name;

    internal static string NormalizeModelSelection(string? selection)
    {
        // A stored full model id such as "stt-rt-v5" becomes its version, so no doubled prefix reaches Soniox.
        return SonioxModels.SelectionVersion(selection) ?? SonioxModels.Automatic;
    }

    internal static string? NormalizeContextText(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length <= MaximumContextTextCharacters ? trimmed : trimmed[..MaximumContextTextCharacters].TrimEnd();
    }

    /// <summary>
    /// Builds Soniox's context object from the dictionary prompt and the transcription context text.
    /// The terms are clipped again because the context text may have grown since the host applied the budget.
    /// </summary>
    internal static IReadOnlyDictionary<string, object>? CreateRequestContext(string? prompt, string? contextText)
    {
        var text = NormalizeContextText(contextText);
        var terms = PluginDictionaryTerms.Clip(
            PluginDictionaryTerms.ParsePrompt(prompt),
            new DictionaryTermsBudget(MaxTotalChars: MaximumContextCharacters - (text?.Length ?? 0)));

        var context = new Dictionary<string, object>(StringComparer.Ordinal);
        if (terms.Count > 0)
            context["terms"] = terms.ToArray();
        if (text is not null)
            context["text"] = text;
        return context.Count == 0 ? null : context;
    }
}
