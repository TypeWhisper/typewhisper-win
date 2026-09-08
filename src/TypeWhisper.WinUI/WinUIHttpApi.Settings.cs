using System.Text;
using TypeWhisper.Core.Services;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class WinUIHttpApi
{
    internal Func<PersistedProfileBackup, PersistedProfileBackupPreview, Task>? ImportSettings { get; set; }
    private bool _importPending;
    private bool _importReserved;
    private async Task<LocalApiResponse?> HandleSettingsAsync(LocalApiRequest request, CancellationToken ct)
    {
        if (request.Path is not ("/v1/settings/export" or "/v1/settings/import")) return null;
        if (request.Query.Count != 0) return Error(400, "Settings endpoints accept no query parameters.");
        var store = new PersistedProfileBackup(WinUIProfile.Root);
        if (request.Path == "/v1/settings/export")
        {
            if (request.Method != "GET") return Error(405, "Use GET.");
            if (request.Body.Length != 0) return Error(400, "Export accepts no body.");
            return new(200, Encoding.UTF8.GetBytes(await store.ExportAsync(PersistedProfileBackup.SupportedCategories, ct)));
        }
        if (request.Method != "POST") return Error(405, "Use POST.");
        if (request.Body.Length == 0) return Error(400, "Request body must contain a TypeWhisper settings backup.");
        if (ImportSettings is null) return Error(503, "Settings restoration is unavailable.");
        if (!session.CanChangeProvider) return Error(409, "Finish active recording and processing before importing settings.");
        if (_importReserved) return Error(409, "Another settings import is already in progress.");
        _importReserved = true;
        var handedOff = false;
        try
        {
        PersistedProfileBackupPreview preview;
        try { preview = await store.PreviewAsync(new UTF8Encoding(false, true).GetString(request.Body), PersistedProfileBackup.SupportedCategories, ct); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or DecoderFallbackException or ArgumentException)
        { return Error(400, "Request body is not a valid supported TypeWhisper settings backup."); }
        ct.ThrowIfCancellationRequested();
        if (_closed || _importPending) return Error(503, "The app is shutting down.");
        if (!session.CanChangeProvider) return Error(409, "The app became busy. Retry after processing finishes.");
        if (preview.ChangedFileCount == 0) return LocalApiResponse.Json(200, new { status = "unchanged", restart_required = false });
        // Restore drains all live writers and reopens the profile. Schedule only after the HTTP response.
        handedOff = true;
        return LocalApiResponse.Json(202, new { status = "restoring", restart_required = true, changed_files = preview.ChangedFileCount }) with
        {
            ResponseFailed = () => dispatcher.TryEnqueue(() => _importReserved = false),
            AfterResponse = () => dispatcher.TryEnqueue(async () =>
            {
                if (_closed || _importPending) return;
                _importPending = true;
                try { await ImportSettings(store, preview); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { _importPending = false; Status = "Settings import failed. Reopen TypeWhisper before retrying."; Changed?.Invoke(); }
            })
        };
        }
        finally { if (!handedOff) _importReserved = false; }
    }
}
