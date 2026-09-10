using System.Globalization;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>Exposes persisted history through the cross-platform local API contract.</summary>
public sealed class LocalApiHistory(HistoryReader reader, HistoryActions actions)
{
    /// <summary>Lists or deletes history using the same persistence boundary as the desktop UI.</summary>
    public async Task<LocalApiResponse> HandleAsync(LocalApiRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Path != "/v1/history") return Error(404, "History endpoint not found");
        if (request.Method == "GET")
        {
            // Match the macOS API's defaults and clamping, including malformed integers.
            var limit = (int)Math.Clamp(Integer(request, "limit", 50), 0, 200);
            var offset = Math.Max(Integer(request, "offset", 0), 0);
            var query = request.Query.GetValueOrDefault("q")?.Trim() ?? "";
            var snapshot = await reader.ReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var matches = snapshot.Where(record => Matches(record, query)).ToArray();
            var entries = matches.Skip((int)Math.Min(offset, int.MaxValue)).Take(limit).Select(record => new
            {
                id = record.Id,
                text = record.FinalText,
                raw_text = record.RawText,
                timestamp = record.Timestamp.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(record.Timestamp, DateTimeKind.Utc) : record.Timestamp.ToUniversalTime(),
                app_name = record.AppName,
                app_bundle_id = (string?)null,
                app_url = record.AppUrl,
                duration = record.DurationSeconds,
                language = record.Language,
                engine = record.EngineUsed,
                model = record.ModelUsed,
                words_count = record.WordCount
            }).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return LocalApiResponse.Json(200, new { entries, total = matches.Length, limit, offset });
        }
        if (request.Method == "DELETE")
        {
            var id = request.Query.GetValueOrDefault("id");
            if (string.IsNullOrWhiteSpace(id)) return Error(400, "Missing or invalid 'id' query parameter");
            var snapshot = await reader.ReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var record = snapshot.FirstOrDefault(record => record.Id == id);
            // Preserve opaque Windows identities while accepting UUID casing used by macOS clients.
            var isUuid = Guid.TryParseExact(id, "D", out var uuid);
            record ??= isUuid ? snapshot.FirstOrDefault(record => Guid.TryParse(record.Id, out var candidate) && candidate == uuid) : null;
            if (record is null) return Error(isUuid ? 404 : 400,
                isUuid ? "History entry not found" : "Missing or invalid 'id' query parameter");
            cancellationToken.ThrowIfCancellationRequested();
            return await actions.DeleteAsync(record.Id).ConfigureAwait(false)
                ? LocalApiResponse.Json(200, new { deleted = true })
                : Error(500, "History entry could not be deleted");
        }
        return Error(405, "Method not allowed");
    }

    private static long Integer(LocalApiRequest request, string name, long fallback) =>
        long.TryParse(request.Query.GetValueOrDefault(name), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool Matches(TranscriptionRecord record, string query) => query.Length == 0
        || record.RawText.Contains(query, StringComparison.OrdinalIgnoreCase)
        || record.FinalText.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (record.AppName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        || (Uri.TryCreate(record.AppUrl, UriKind.Absolute, out var url)
            && url.Host.Contains(query, StringComparison.OrdinalIgnoreCase))
        || (record.SourceKind?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    private static LocalApiResponse Error(int status, string message) => LocalApiResponse.Json(status,
        new { error = new { code = status switch { 400 => "bad_request", 404 => "not_found", 405 => "method_not_allowed", _ => "error" }, message } });
}
