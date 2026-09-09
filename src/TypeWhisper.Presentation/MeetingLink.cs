using System.Text.RegularExpressions;

namespace TypeWhisper.Presentation;

/// <summary>A provider and canonical meeting identity, matching the Mac link parser.</summary>
public sealed record MeetingLink(string Provider, string Identity)
{
    private static readonly Regex Candidates = new("(?i)(?:(?:https?|zoommtg|msteams):(?://)?[^\\s<>\"']+|(?:[a-z0-9-]+\\.)*(?:zoom\\.us|zoomgov\\.com|teams\\.microsoft\\.com|teams\\.live\\.com|meet\\.google\\.com)/[^\\s<>\"']+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Extracts supported Windows meeting links from calendar URLs, locations and notes.</summary>
    public static IReadOnlyList<MeetingLink> Extract(params string?[] values) => values.Where(v => !string.IsNullOrEmpty(v))
        .SelectMany(v => Candidates.Matches(v!.Length > 100_000 ? v[..100_000] : v).Cast<Match>())
        .Select(m => Parse(m.Value)).OfType<MeetingLink>().Distinct().ToArray();

    /// <summary>Normalizes Zoom, Teams and Google Meet URLs without retaining passcodes or tracking parameters.</summary>
    public static MeetingLink? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl)) return null;
        var text = value.Trim().TrimEnd('.', ',', ';', '!', '?', ')', ']', '}', '>');
        if (!Regex.IsMatch(text, "^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) text = "https://" + text;
        if (text.StartsWith("msteams:/", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("msteams://", StringComparison.OrdinalIgnoreCase))
            text = "msteams://teams.microsoft.com/" + text[9..];
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return null;
        var web = uri.Scheme is "http" or "https";
        if (!web && uri.Scheme is not ("zoommtg" or "msteams")) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        var path = Regex.Replace(Uri.UnescapeDataString(uri.AbsolutePath), "/{2,}", "/").TrimEnd('/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = item.Split('=', 2);
            if (pair.Length == 2) query.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1]));
        }
        if (uri.Scheme == "zoommtg" || web && (host is "zoom.us" or "zoomgov.com" || host.EndsWith(".zoom.us", StringComparison.Ordinal) || host.EndsWith(".zoomgov.com", StringComparison.Ordinal)))
        {
            string? number = null;
            if (uri.Scheme == "zoommtg") number = query.GetValueOrDefault("confno") ?? query.GetValueOrDefault("meetingid") ?? parts.LastOrDefault();
            else if (parts.Length >= 2 && parts[0].Equals("my", StringComparison.OrdinalIgnoreCase)) return new("zoom", "my/" + parts[1].ToLowerInvariant());
            else if (parts.Length >= 2 && (parts[0].Equals("j", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("s", StringComparison.OrdinalIgnoreCase))) number = parts[1];
            else if (parts.Length >= 3 && parts[0].Equals("wc", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("join", StringComparison.OrdinalIgnoreCase)) number = parts[2];
            number = number is null ? null : string.Concat(number.Where(c => !char.IsWhiteSpace(c) && c != '-'));
            return !string.IsNullOrEmpty(number) && number.All(char.IsAsciiDigit) ? new("zoom", "j/" + number) : null;
        }
        if (uri.Scheme == "msteams" || web && host is "teams.microsoft.com" or "teams.live.com")
        {
            foreach (var (prefix, canonicalHost) in new[] { ("/l/meetup-join/", "teams.microsoft.com"), ("/meet/", "teams.live.com") })
                if ((uri.Scheme == "msteams" || host == canonicalHost) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.Length > prefix.Length)
                    return new("teams", canonicalHost + prefix + path[prefix.Length..]);
            return uri.Scheme == "msteams" && query.TryGetValue("meetingid", out var id) && id.Length > 0 ? new("teams", "meetingid/" + id.ToLowerInvariant()) : null;
        }
        if (web && host == "meet.google.com" && parts.Length > 0)
        {
            if (parts.Length >= 2 && parts[0].Equals("lookup", StringComparison.OrdinalIgnoreCase)) return new("googleMeet", "lookup/" + parts[1].ToLowerInvariant());
            if (Regex.IsMatch(parts[0], "^[a-z]{3}-[a-z]{4}-[a-z]{3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return new("googleMeet", parts[0].ToLowerInvariant());
        }
        return null;
    }

    /// <summary>Omits private meeting identifiers from diagnostics.</summary>
    public override string ToString() => Provider + " meeting";
}
