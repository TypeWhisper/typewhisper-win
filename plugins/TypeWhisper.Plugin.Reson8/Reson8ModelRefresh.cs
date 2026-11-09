using System.Net;
using System.Net.Http;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Reson8;

/// <summary>
/// Reson8 retires models, and custom models can be deleted in its console. Lists that only reload on request keep
/// offering them, and requests to them fail. With a key, both lists reload in the background once they are a day old.
/// </summary>
public sealed partial class Reson8Plugin
{
    /// <summary>The connection a refresh fetches with. A newer generation means the connection changed since.</summary>
    private readonly record struct Reson8Connection(string ApiKey, string BaseUrl, string AuthHeader, long Generation);

    internal DateTimeOffset? ModelListsFetchedAt => _modelListsFetchedAt;
    /// <summary>The running background refresh, or the last one. Tests await it.</summary>
    internal Task ModelRefreshTask => _modelRefresh.Current;

    private Reson8Connection? CurrentConnection()
    {
        lock (_modelListLock)
            return string.IsNullOrEmpty(_apiKey) ? null : new Reson8Connection(_apiKey, _customBaseUrl, _customAuthHeader, _connectionGeneration);
    }

    /// <summary>Drops a refresh that started with the previous connection, also once its answer arrives.</summary>
    private void BeginConnectionChange()
    {
        lock (_modelListLock)
        {
            _connectionGeneration++;
            _modelRefresh.Cancel();
        }
    }

    private void StartModelRefreshIfStale()
    {
        lock (_modelListLock)
        {
            if (!_modelRefresh.Enabled || _host is not { } host || CurrentConnection() is not { } connection
                || !_modelRefresh.IsStale(_modelListsFetchedAt))
                return;
            host.Log(PluginLogLevel.Info, "Refreshing the Reson8 model lists in the background.");
            _modelRefresh.Start(ct => RefreshModelListsInBackgroundAsync(host, connection, ct),
                ex => host.Log(PluginLogLevel.Warning, $"The Reson8 model lists could not be refreshed ({Describe(ex)}). Keeping the current lists."));
        }
    }

    private async Task RefreshModelListsInBackgroundAsync(IPluginHostServices host, Reson8Connection connection, CancellationToken ct)
    {
        var (models, customModels, failure, complete) = await FetchModelListsAsync(connection, ct).ConfigureAwait(false);
        // Saves nothing once the plugin was deactivated or the connection changed since the fetch.
        if (TryApplyModelLists(models, customModels, complete, connection.Generation, ct) && failure is not null)
            host.Log(PluginLogLevel.Warning, $"One Reson8 model list could not be refreshed ({Describe(failure)}) and keeps its previous entries.");
    }

    /// <summary>
    /// Fetches both lists. A list that cannot be fetched comes back as null and keeps the entries fetched before; a proxy
    /// without <c>/v1/model</c> still loads its custom models. Throws when neither list loads. <c>Complete</c> is false
    /// when a list failed for a reason that may pass, so the next start tries again instead of waiting a day.
    /// </summary>
    private async Task<(IReadOnlyList<Reson8Model>? Models, IReadOnlyList<Reson8CustomModel>? CustomModels, Exception? Failure, bool Complete)>
        FetchModelListsAsync(Reson8Connection connection, CancellationToken ct)
    {
        IReadOnlyList<Reson8Model>? models = null;
        IReadOnlyList<Reson8CustomModel>? customModels = null;
        Exception? modelsFailure = null;
        Exception? customModelsFailure = null;
        try { models = await FetchModelsAsync(connection, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        { modelsFailure = ex; }
        try { customModels = await FetchCustomModelsAsync(connection, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        { customModelsFailure = ex; }
        var failure = modelsFailure ?? customModelsFailure;
        if (models is null && customModels is null)
            throw new InvalidOperationException($"Reson8 models could not be loaded: {failure?.Message}", failure);
        // A proxy without /v1/model answers 404 every time; that counts as done for the day.
        var complete = customModels is not null
            && (models is not null || modelsFailure is HttpRequestException { StatusCode: HttpStatusCode.NotFound });
        return (models, customModels, failure, complete);
    }

    /// <summary>
    /// Saves the fetched lists, unless the refresh was cancelled or the connection changed since the fetch started. The
    /// date is recorded only for a complete fetch. A null list keeps its entries. An empty custom model list is saved: it
    /// is what the account has. The saved selection is not touched; it applies whenever the lists offer it.
    /// </summary>
    private bool TryApplyModelLists(IReadOnlyList<Reson8Model>? models, IReadOnlyList<Reson8CustomModel>? customModels,
        bool complete, long generation, CancellationToken refreshToken)
    {
        IPluginHostServices? changedHost = null;
        try
        {
            lock (_modelListLock)
            {
                if (refreshToken.IsCancellationRequested || _host is not { } host || generation != _connectionGeneration)
                    return false;
                try
                {
                    if (customModels is not null)
                    {
                        var nextModels = customModels.ToArray();
                        host.SetSetting(FetchedCustomModelsSettingName, nextModels);
                        _fetchedCustomModels = nextModels;
                        changedHost = host;
                    }
                    if (models is not null)
                    {
                        var sanitized = Reson8Catalog.Sanitize(models);
                        host.SetSetting(FetchedModelsSettingName, sanitized);
                        _fetchedModels = sanitized;
                        changedHost = host;
                    }
                    if (complete)
                    {
                        var fetchedAt = _modelRefresh.UtcNow;
                        host.SetSetting<DateTimeOffset?>(ModelListsFetchedAtSettingName, fetchedAt);
                        _modelListsFetchedAt = fetchedAt;
                    }
                }
                finally
                {
                    ResolveSelectedModel();
                }
            }
            return true;
        }
        finally
        {
            changedHost?.NotifyCapabilitiesChanged();
        }
    }

    // The type and HTTP status only: Reson8's error messages can repeat the response body.
    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        InvalidOperationException { InnerException: { } inner } => Describe(inner),
        _ => ex.GetType().Name,
    };
}
