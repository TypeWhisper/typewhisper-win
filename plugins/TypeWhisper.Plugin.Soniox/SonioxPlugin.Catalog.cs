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
    private int _catalogRefreshQueued;
    private long _catalogRetryAfterTicks;

    /// <summary>
    /// Loads the current speech-to-text and text-to-speech model lists for the selected region and caches them.
    /// A failed voice list keeps the voices cached for that region, because transcription does not depend on it.
    /// </summary>
    internal async Task<SonioxCatalog> RefreshCatalogAsync(CancellationToken ct)
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

            IReadOnlyList<SonioxTtsModel> tts;
            try
            {
                tts = SonioxModels.ParseTtsModels(
                    await GetCatalogJsonAsync($"{region.BaseUrl}/v1/tts-models", key, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                && ex is HttpRequestException or InvalidDataException or JsonException or TimeoutException)
            {
                host.Log(PluginLogLevel.Warning, $"Soniox voice list unavailable; keeping the cached voices: {ex.Message}");
                tts = Volatile.Read(ref _catalog) is { } previous && previous.Region == region.Id ? previous.TtsModels : [];
            }

            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(host, _host) || key != _apiKey || region.Id != _region)
                throw new InvalidOperationException("The Soniox connection changed. Refresh the models again.");

            var catalog = new SonioxCatalog(region.Id, _time.GetUtcNow(), stt, tts);
            host.SetSetting(CatalogSettingKey, catalog);
            Volatile.Write(ref _catalog, catalog);
            Interlocked.Exchange(ref _catalogRetryAfterTicks, 0);
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
    /// Starts a background refresh when no list is cached for this region or the cached one is a day old.
    /// The current request keeps using the cached list or the built-in v6 fallback.
    /// </summary>
    internal void RefreshCatalogInBackgroundIfStale()
    {
        var host = _host;
        if (!_backgroundCatalogRefresh || host is null || _apiKey is null || Volatile.Read(ref _disposed) != 0)
            return;

        var now = _time.GetUtcNow();
        if (CurrentCatalog is { } catalog && catalog.FetchedAt <= now && now - catalog.FetchedAt < CatalogMaximumAge)
            return;
        if (now.UtcTicks < Interlocked.Read(ref _catalogRetryAfterTicks)
            || Interlocked.CompareExchange(ref _catalogRefreshQueued, 1, 0) != 0)
            return;

        var token = _backgroundCancellation.Token;
        lock (_cleanupLock)
        {
            _cleanupTasks.RemoveAll(task => task.IsCompleted);
            _cleanupTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await RefreshCatalogAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Interlocked.Exchange(ref _catalogRetryAfterTicks, (_time.GetUtcNow() + CatalogRetryDelay).UtcTicks);
                    if (!token.IsCancellationRequested)
                        host.Log(PluginLogLevel.Warning, $"Soniox model list refresh failed: {ex.Message}");
                }
                finally
                {
                    Volatile.Write(ref _catalogRefreshQueued, 0);
                }
            }, CancellationToken.None));
        }
    }

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
