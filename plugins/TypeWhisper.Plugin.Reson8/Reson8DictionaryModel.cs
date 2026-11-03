using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Plugin.Reson8;

/// <summary>
/// The custom model that holds the dictionary and the terms it is known to hold. <paramref name="Unconfirmed"/> are
/// terms of a call that failed or was interrupted, so Reson8 may or may not hold them.
/// </summary>
internal sealed record Reson8DictionaryModelState(string? ModelId, IReadOnlyList<string> Terms, IReadOnlyList<string>? Unconfirmed = null)
{
    internal static Reson8DictionaryModelState Empty { get; } = new(null, []);
}

/// <summary>The custom model endpoints the dictionary sync uses. https://docs.reson8.dev/speech-to-text/features/custom-models/</summary>
internal interface IReson8CustomModelApi
{
    /// <summary>Creates a model and returns its id; Reson8 requires at least one phrase.</summary>
    Task<string> CreateModelAsync(string name, string description, IReadOnlyList<string> phrases, CancellationToken ct);
    Task AddPhrasesAsync(string modelId, IReadOnlyList<string> phrases, CancellationToken ct);
    Task DeletePhrasesAsync(string modelId, IReadOnlyList<string> phrases, CancellationToken ct);
}

/// <summary>Reson8 no longer has the model, for example because it was deleted in the Reson8 console.</summary>
internal sealed class Reson8ModelNotFoundException(string message) : Exception(message);

/// <summary>
/// Keeps the dictionary in a custom model, so Reson8 takes up to 50,000 terms per request instead of 250 phrases
/// in the query string. Only changes go to Reson8: terms added to or removed from the dictionary since the last
/// sync. Calls run one at a time.
/// </summary>
internal sealed class Reson8DictionaryModelSync(
    Reson8DictionaryModelState state,
    IReson8CustomModelApi api,
    Action<Reson8DictionaryModelState> persist) : IDisposable
{
    internal const string ModelName = "TypeWhisper Dictionary";
    internal const string ModelDescription = "Dictionary terms from TypeWhisper. TypeWhisper updates this model when the dictionary changes.";
    /// <summary>Phrases per request; Reson8 documents no limit.</summary>
    internal const int BatchSize = 1_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Reson8DictionaryModelState _state = state;

    internal Reson8DictionaryModelState State => Volatile.Read(ref _state);

    /// <summary>The model that holds exactly <paramref name="terms"/>, created or updated first when needed.</summary>
    internal async Task<string> ModelIdForAsync(IReadOnlyList<string> terms, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = State;
            if (current.ModelId is { } modelId && current.Unconfirmed is null or { Count: 0 }
                && current.Terms.ToHashSet(StringComparer.Ordinal).SetEquals(terms))
                return modelId;
            return await SyncAsync(current, terms, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets a model Reson8 no longer has, so the next sync creates a new one.</summary>
    internal async Task ForgetAsync(string modelId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State.ModelId == modelId)
                Save(Reson8DictionaryModelState.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> SyncAsync(Reson8DictionaryModelState current, IReadOnlyList<string> terms, CancellationToken ct)
    {
        if (current.ModelId is { } modelId)
        {
            try
            {
                var confirmed = current.Terms.ToHashSet(StringComparer.Ordinal);
                var unconfirmed = (current.Unconfirmed ?? []).Where(term => !confirmed.Contains(term)).ToHashSet(StringComparer.Ordinal);
                var wanted = terms.ToHashSet(StringComparer.Ordinal);
                // Reson8 skips phrases that are already present or already gone, so unconfirmed terms simply go
                // again in the direction the dictionary needs now.
                var deletions = confirmed.Concat(unconfirmed).Where(term => !wanted.Contains(term)).ToArray();
                var additions = terms.Where(term => !confirmed.Contains(term)).ToArray();
                foreach (var batch in Batches(deletions))
                    await SendBatchAsync(modelId, batch, add: false, confirmed, unconfirmed, ct).ConfigureAwait(false);
                foreach (var batch in Batches(additions))
                    await SendBatchAsync(modelId, batch, add: true, confirmed, unconfirmed, ct).ConfigureAwait(false);
                Save(new Reson8DictionaryModelState(modelId, terms.ToArray()));
                return modelId;
            }
            catch (Reson8ModelNotFoundException)
            {
                Save(Reson8DictionaryModelState.Empty);
            }
        }

        var batches = Batches(terms);
        if (batches.Count == 0)
            throw new InvalidOperationException("Reson8 creates no custom model without terms.");
        var created = await api.CreateModelAsync(ModelName, ModelDescription, batches[0], ct).ConfigureAwait(false);
        Save(new Reson8DictionaryModelState(created, batches[0].ToArray()));
        var createdTerms = batches[0].ToHashSet(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in batches.Skip(1))
            await SendBatchAsync(created, batch, add: true, createdTerms, pending, ct).ConfigureAwait(false);
        if (batches.Count > 1)
            Save(new Reson8DictionaryModelState(created, terms.ToArray()));
        return created;
    }

    /// <summary>
    /// Sends one batch. Its terms are saved as unconfirmed while the call runs, so a call that fails, times out or is
    /// interrupted leaves them to the next sync instead of a saved state that no longer matches the model.
    /// </summary>
    private async Task SendBatchAsync(
        string modelId, IReadOnlyList<string> batch, bool add, HashSet<string> confirmed, HashSet<string> unconfirmed, CancellationToken ct)
    {
        confirmed.ExceptWith(batch);
        unconfirmed.UnionWith(batch);
        Save(new Reson8DictionaryModelState(modelId, confirmed.ToArray(), unconfirmed.ToArray()));

        if (add)
            await api.AddPhrasesAsync(modelId, batch, ct).ConfigureAwait(false);
        else
            await api.DeletePhrasesAsync(modelId, batch, ct).ConfigureAwait(false);

        unconfirmed.ExceptWith(batch);
        if (add)
            confirmed.UnionWith(batch);
        Save(new Reson8DictionaryModelState(modelId, confirmed.ToArray(), unconfirmed.ToArray()));
    }

    private void Save(Reson8DictionaryModelState next)
    {
        Volatile.Write(ref _state, next);
        persist(next);
    }

    private static IReadOnlyList<IReadOnlyList<string>> Batches(IReadOnlyList<string> terms) =>
        terms.Chunk(BatchSize).Select(chunk => (IReadOnlyList<string>)chunk).ToArray();

    public void Dispose() => _gate.Dispose();
}

/// <summary>Reson8's custom model endpoints with the connection settings at the time of each call.</summary>
internal sealed class Reson8HttpCustomModelApi(
    HttpClient httpClient,
    Func<string, HttpRequestMessage?> createRequest) : IReson8CustomModelApi
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public async Task<string> CreateModelAsync(string name, string description, IReadOnlyList<string> phrases, CancellationToken ct)
    {
        var json = await SendAsync("/v1/custom-model", new Dictionary<string, object>
        {
            ["name"] = name,
            ["description"] = description,
            ["phrases"] = phrases,
        }, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            && Reson8Plugin.IsUsableModelId(id.GetString())
                ? id.GetString()!
                : throw new InvalidDataException("Reson8 returned no custom model id.");
    }

    public Task AddPhrasesAsync(string modelId, IReadOnlyList<string> phrases, CancellationToken ct) =>
        SendAsync($"/v1/custom-model/{Uri.EscapeDataString(modelId)}/phrases", new Dictionary<string, object> { ["phrases"] = phrases }, ct);

    public Task DeletePhrasesAsync(string modelId, IReadOnlyList<string> phrases, CancellationToken ct) =>
        SendAsync($"/v1/custom-model/{Uri.EscapeDataString(modelId)}/phrases/delete", new Dictionary<string, object> { ["phrases"] = phrases }, ct);

    private async Task<string> SendAsync(string path, Dictionary<string, object> body, CancellationToken ct)
    {
        using var request = createRequest(path) ?? throw new InvalidOperationException("Reson8 API key required.");
        request.Method = HttpMethod.Post;
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return response.StatusCode switch
        {
            _ when response.IsSuccessStatusCode => json,
            HttpStatusCode.NotFound => throw new Reson8ModelNotFoundException(Reson8Plugin.ExtractApiError(json)),
            HttpStatusCode.Unauthorized => throw new HttpRequestException("Invalid Reson8 API key.", null, response.StatusCode),
            _ => throw new HttpRequestException(
                $"Reson8 custom model error {(int)response.StatusCode}: {Reson8Plugin.ExtractApiError(json)}", null, response.StatusCode),
        };
    }
}
