using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>A timed meeting occurrence returned by a connected cloud calendar.</summary>
public sealed record CalendarMeeting(string CalendarId, string EventId, string Title, DateTimeOffset Start,
    DateTimeOffset End, string Participation, IReadOnlyList<MeetingLink> Links)
{
    /// <summary>Follows the Mac join window: ten minutes before start through thirty minutes after end.</summary>
    public bool IsInsideJoinWindow(DateTimeOffset now) => now >= Start.AddMinutes(-10) && now <= End.AddMinutes(30);
    /// <summary>Only accepted, tentative or organizer-owned events permit automatic start.</summary>
    public bool PermitsAutomaticStart => Participation is "accepted" or "tentative" or "organizer";
    /// <summary>Omits event content and identifiers from diagnostics.</summary>
    public override string ToString() => "Calendar meeting";
}

/// <summary>A read-only calendar API adapter. The host supplies OAuth tokens and must disable HTTP redirects.</summary>
public sealed class CalendarMeetingClient(HttpClient http, Func<CancellationToken, Task<string>> acquireToken)
{
    /// <summary>Reads expanded Outlook occurrences for one selected calendar, using UTC response times.</summary>
    public Task<IReadOnlyList<CalendarMeeting>> ReadMicrosoftAsync(string calendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => ReadAsync(true, calendarId, from, to, ct);

    /// <summary>Reads expanded Google occurrences for one selected calendar.</summary>
    public Task<IReadOnlyList<CalendarMeeting>> ReadGoogleAsync(string calendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => ReadAsync(false, calendarId, from, to, ct);

    private async Task<IReadOnlyList<CalendarMeeting>> ReadAsync(bool microsoft, string calendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(calendarId) || calendarId.Length > 2048 || calendarId.Any(char.IsControl)) throw new ArgumentException("Invalid calendar identifier.");
        if (to <= from || to - from > TimeSpan.FromDays(31)) throw new ArgumentException("Calendar window must be between zero and 31 days.");
        var id = Uri.EscapeDataString(calendarId);
        var start = Uri.EscapeDataString(from.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        var end = Uri.EscapeDataString(to.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        var root = microsoft ? "https://graph.microsoft.com" : "https://www.googleapis.com";
        var path = microsoft ? $"/v1.0/me/calendars/{id}/calendarView" : $"/calendar/v3/calendars/{id}/events";
        var first = root + path + (microsoft
            ? $"?startDateTime={start}&endDateTime={end}&$top=250&$select=id,subject,start,end,isAllDay,isCancelled,responseStatus,onlineMeeting,location,body"
            : $"?timeMin={start}&timeMax={end}&singleEvents=true&orderBy=startTime&showDeleted=false&maxResults=250&timeZone=UTC");
        string? next = first;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new Dictionary<string, CalendarMeeting>(StringComparer.Ordinal);
        var token = await acquireToken(ct);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw new InvalidDataException("Invalid calendar session.");
        while (next is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(next) || seen.Count > 100) throw new InvalidDataException("Calendar pagination did not complete.");
            if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != root ||
                uri.AbsolutePath != new Uri(root + path).AbsolutePath || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("Untrusted calendar page address.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (microsoft) request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\", outlook.body-content-type=\"text\", IdType=\"ImmutableId\"");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Calendar request failed (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[16_384];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) != 0)
            {
                if (buffer.Length + read > 8_000_000) throw new InvalidDataException("Calendar page is too large.");
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var page = document.RootElement;
            var entries = page.TryGetProperty(microsoft ? "value" : "items", out var collection)
                ? collection.EnumerateArray().ToArray() : microsoft ? throw new InvalidDataException("Invalid calendar page.") : [];
            foreach (var entry in entries)
            {
                var meeting = Parse(entry, calendarId, microsoft);
                if (meeting is not null && meeting.Start < to && meeting.End > from) results[meeting.EventId] = meeting;
            }
            var continuation = Text(page, microsoft ? "@odata.nextLink" : "nextPageToken");
            next = string.IsNullOrEmpty(continuation) ? null : microsoft ? continuation : first + "&pageToken=" + Uri.EscapeDataString(continuation);
        }
        return results.Values.OrderBy(m => m.Start).ToArray();
    }

    private static CalendarMeeting? Parse(JsonElement item, string calendar, bool microsoft)
    {
        if (Flag(item, "isAllDay") || Flag(item, "isCancelled") || Text(item, "status") == "cancelled") return null;
        var id = Text(item, "id");
        var start = Time(item, "start", microsoft); var end = Time(item, "end", microsoft);
        if (id is null || start is null || end is null || end <= start) return null;
        var participation = "unknown";
        var urls = new List<string?>();
        if (microsoft)
        {
            participation = Nested(item, "responseStatus", "response") ?? "unknown";
            participation = participation == "tentativelyAccepted" ? "tentative" : participation;
            urls.Add(Nested(item, "onlineMeeting", "joinUrl"));
            urls.Add(Nested(item, "location", "displayName"));
            urls.Add(WebUtility.HtmlDecode(Nested(item, "body", "content")));
        }
        else
        {
            var ownResponse = item.TryGetProperty("attendees", out var attendees)
                ? attendees.EnumerateArray().Where(a => Flag(a, "self")).Select(a => Text(a, "responseStatus")).FirstOrDefault() : null;
            participation = ownResponse ?? (item.TryGetProperty("organizer", out var organizer) && Flag(organizer, "self") ? "organizer" : "unknown");
            urls.Add(Text(item, "hangoutLink")); urls.Add(Text(item, "location")); urls.Add(WebUtility.HtmlDecode(Text(item, "description")));
            if (item.TryGetProperty("conferenceData", out var data) && data.TryGetProperty("entryPoints", out var points))
                urls.AddRange(points.EnumerateArray().Where(p => Text(p, "entryPointType") == "video").Select(p => Text(p, "uri")));
        }
        if (participation == "declined") return null;
        var links = MeetingLink.Extract(urls.ToArray());
        return links.Count == 0 ? null : new(calendar, id, Text(item, microsoft ? "subject" : "summary") ?? "Meeting", start.Value, end.Value, participation, links);
    }

    private static DateTimeOffset? Time(JsonElement item, string property, bool microsoft)
    {
        var value = Nested(item, property, "dateTime");
        if (value is null) return null; // Date-only Google events are all-day entries.
        if (microsoft && Nested(item, property, "timeZone") != "UTC") return null; // Do not guess a local offset.
        if (!microsoft && !System.Text.RegularExpressions.Regex.IsMatch(value, @"(?:Z|[+-]\d{2}:\d{2})$")) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result) ? result : null;
    }
    private static string? Nested(JsonElement item, string parent, string key) => item.TryGetProperty(parent, out var child) && child.ValueKind == JsonValueKind.Object ? Text(child, key) : null;
    private static string? Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Flag(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
}
