using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>A validated request whose optional overrides are applied by the transcription backend.</summary>
/// <param name="Audio">Owned upload bytes, or empty for a local-file request.</param>
/// <param name="FileName">Optional upload filename metadata.</param>
/// <param name="LocalPath">Absolute Windows path for a local-file request.</param>
/// <param name="Language">Optional requested language.</param>
/// <param name="Task">Optional transcribe or translate task.</param>
/// <param name="ResponseFormat">Requested json, text, srt, or vtt representation.</param>
/// <param name="Model">Optional model constraint; must not change global selection.</param>
/// <param name="Engine">Optional engine constraint; must not change global selection.</param>
public sealed record ParsedApiTranscription(
    ReadOnlyMemory<byte> Audio, string? FileName, string? LocalPath, string? Language,
    string? Task, string ResponseFormat, string? Model, string? Engine);

/// <summary>A transcript segment with actual provider timestamps in seconds.</summary>
/// <param name="Text">Recognized segment text.</param>
/// <param name="Start">Start time in seconds.</param>
/// <param name="End">End time in seconds.</param>
public sealed record LocalApiTranscriptSegment(string Text, double Start, double End);

/// <summary>A request failure containing an HTTP status and a safe client-facing message.</summary>
/// <param name="statusCode">HTTP error status.</param>
/// <param name="message">Message safe to expose without local paths or provider secrets.</param>
public sealed class LocalApiRequestException(int statusCode, string message) : Exception(message)
{
    /// <summary>Gets the HTTP error status.</summary>
    public int StatusCode { get; } = statusCode;
}

/// <summary>Strict, platform-independent parsing of the supported file transcription contract.</summary>
public static class LocalApiTranscription
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> OptionNames = new(StringComparer.Ordinal)
        { "language", "task", "response_format", "model", "engine" };

    /// <summary>Parses an uploaded or local-file transcription request without accessing the filesystem.</summary>
    public static ParsedApiTranscription Parse(LocalApiRequest request)
    {
        if (request.Method != "POST" || request.Path is not ("/v1/transcribe" or "/v1/transcribe/local-file"))
            throw new LocalApiRequestException(404, "Unknown transcription route.");
        if (request.Body.Length > 32 * 1024 * 1024)
            throw new LocalApiRequestException(413, "Request body is too large.");
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media))
            throw Bad("A valid Content-Type is required.");
        if (media.Parameters.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw Bad("Duplicate Content-Type parameter.");
        if (media.CharSet is { } charset && !Unquote(charset).Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            throw Bad("Only UTF-8 request text is supported.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in request.Query)
            AddOption(options, pair.Key, pair.Value);
        ReadOnlyMemory<byte> audio = default;
        string? fileName = null;
        string? localPath = null;
        if (request.Path == "/v1/transcribe/local-file")
        {
            if (!string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw new LocalApiRequestException(415, "Local-file requests require application/json.");
            try
            {
                using var json = JsonDocument.Parse(request.Body);
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw Bad("Expected a JSON object.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in json.RootElement.EnumerateObject())
                {
                    if (!seen.Add(property.Name)) throw Bad("Duplicate request field.");
                    if (property.Value.ValueKind != JsonValueKind.String) throw Bad("Request fields must be strings.");
                    var value = property.Value.GetString()!;
                    if (property.Name == "path") localPath = value;
                    else AddOption(options, property.Name, value);
                }
            }
            catch (JsonException) { throw Bad("Invalid JSON body."); }
            // Windows paths are validated independently of the platform executing the parser.
            if (string.IsNullOrWhiteSpace(localPath) || localPath.Length > 32767 ||
                localPath.Any(c => char.IsControl(c) || c is '"' or '<' or '>' or '|' or '*' or '?') ||
                !(localPath.Length >= 3 && char.IsAsciiLetter(localPath[0]) && localPath[1] == ':' &&
                  localPath[2] is '\\' or '/') || localPath.AsSpan(2).Contains(':'))
                throw Bad("An absolute local Windows file path is required.");
        }
        else if (string.Equals(media.MediaType, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            (audio, fileName) = ParseMultipart(request.Body, media, options);
        }
        else if (string.Equals(media.MediaType, "audio/wav", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(media.MediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Body.Length == 0) throw Bad("An audio file is required.");
            audio = request.Body.ToArray();
        }
        else throw new LocalApiRequestException(415, "Use audio/wav, application/octet-stream, or multipart/form-data.");

        var task = options.GetValueOrDefault("task");
        if (task is not (null or "transcribe" or "translate")) throw Bad("Unsupported task.");
        var format = options.GetValueOrDefault("response_format", "json");
        if (format is not ("json" or "text" or "srt" or "vtt")) throw Bad("Unsupported response_format.");
        return new(audio, fileName, localPath, options.GetValueOrDefault("language"), task, format,
            options.GetValueOrDefault("model"), options.GetValueOrDefault("engine"));
    }

    /// <summary>Formats a transcript and refuses subtitle output if actual timestamps are unavailable.</summary>
    public static LocalApiResponse FormatResponse(string text, IEnumerable<LocalApiTranscriptSegment> segments, string format)
    {
        var items = segments.ToArray();
        if (format == "json") return LocalApiResponse.Json(200, new
        {
            text,
            segments = items.Select(s => new { text = s.Text, start = s.Start, end = s.End })
        });
        if (format == "text") return new(200, Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8");
        if (format is not ("srt" or "vtt")) throw Bad("Unsupported response_format.");
        if (items.Length == 0 || items.Any(s => !double.IsFinite(s.Start) || !double.IsFinite(s.End) ||
                s.Start < 0 || s.End <= s.Start || s.End > TimeSpan.MaxValue.TotalSeconds / 2 ||
                Math.Round(s.End * 1000, MidpointRounding.AwayFromZero) <= Math.Round(s.Start * 1000, MidpointRounding.AwayFromZero)) ||
            items.Zip(items.Skip(1)).Any(pair => pair.First.Start > pair.Second.Start))
            throw new LocalApiRequestException(422, "Subtitle output requires valid segment timestamps.");
        var output = new StringBuilder(format == "vtt" ? "WEBVTT\n\n" : "");
        for (var index = 0; index < items.Length; index++)
        {
            var segment = items[index];
            if (format == "srt") output.Append(index + 1).Append('\n');
            output.Append(Timestamp(segment.Start, format)).Append(" --> ").Append(Timestamp(segment.End, format))
                .Append('\n').Append(segment.Text.Replace("\r\n", "\n").Replace('\r', '\n').Trim()).Append("\n\n");
        }
        return new(200, Encoding.UTF8.GetBytes(output.ToString()),
            format == "vtt" ? "text/vtt; charset=utf-8" : "application/x-subrip; charset=utf-8");
    }

    private static string Timestamp(double seconds, string format)
    {
        var milliseconds = (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        return FormattableString.Invariant($"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}:{milliseconds / 1000 % 60:00}{(format == "srt" ? ',' : '.')}{milliseconds % 1000:000}");
    }

    private static void AddOption(Dictionary<string, string> options, string name, string? value)
    {
        if (!OptionNames.Contains(name)) throw Bad("Unsupported request option.");
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw Bad("Request options must be nonempty strings without control characters.");
        if (!options.TryAdd(name, value)) throw Bad("Duplicate request option.");
    }

    private static (ReadOnlyMemory<byte>, string?) ParseMultipart(byte[] body, MediaTypeHeaderValue media,
        Dictionary<string, string> options)
    {
        var boundaries = media.Parameters.Where(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (boundaries.Length != 1) throw Bad("A single multipart boundary is required.");
        var boundary = Unquote(boundaries[0].Value);
        if (boundary.Length is < 1 or > 70 || boundary[^1] == ' ' ||
            boundary.Any(c => !(char.IsAsciiLetterOrDigit(c) || "'()+_,-./:=? ".Contains(c))))
            throw Bad("Invalid multipart boundary.");
        var opening = Encoding.ASCII.GetBytes("--" + boundary + "\r\n");
        var delimiter = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        if (!body.AsSpan().StartsWith(opening)) throw Bad("Malformed multipart body.");
        var position = opening.Length;
        byte[]? audio = null;
        string? fileName = null;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var headerEnd = body.AsSpan(position).IndexOf("\r\n\r\n"u8);
            if (headerEnd < 0 || headerEnd > 8192) throw Bad("Invalid multipart headers.");
            var headers = Encoding.Latin1.GetString(body, position, headerEnd).Split("\r\n", StringSplitOptions.None);
            string? dispositionValue = null;
            var headerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                var colon = header.IndexOf(':');
                if (colon <= 0 || !headerNames.Add(header[..colon])) throw Bad("Invalid multipart headers.");
                var name = header[..colon];
                var value = header[(colon + 1)..].Trim();
                if (name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase)) dispositionValue = value;
                else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    if (!MediaTypeHeaderValue.TryParse(value, out var partType) ||
                        partType.Parameters.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1) ||
                        partType.CharSet is { } partCharset && !Unquote(partCharset).Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                        throw Bad("Invalid part Content-Type or text encoding.");
                }
                else throw Bad("Unsupported multipart header.");
            }
            if (!ContentDispositionHeaderValue.TryParse(dispositionValue, out var disposition) ||
                !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) ||
                disposition.Parameters.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1) ||
                disposition.Parameters.Any(p => p.Name is not ("name" or "filename" or "filename*")))
                throw Bad("Invalid multipart disposition.");
            var field = Unquote(disposition.Name);
            if (!fields.Add(field)) throw Bad("Duplicate multipart field.");
            position += headerEnd + 4;
            var end = FindDelimiter(body, position, delimiter);
            if (end < 0) throw Bad("Unterminated multipart body.");
            if (field == "file")
            {
                if (end == position) throw Bad("An audio file is required.");
                audio = body.AsSpan(position, end - position).ToArray();
                fileName = Unquote(disposition.FileNameStar ?? disposition.FileName);
                if (fileName.Length == 0) fileName = null;
                if (fileName?.Any(char.IsControl) == true) throw Bad("Invalid filename.");
            }
            else
            {
                if (disposition.FileName != null || disposition.FileNameStar != null) throw Bad("Unexpected file field.");
                if (end - position > 2048) throw Bad("Request option is too long.");
                try { AddOption(options, field, StrictUtf8.GetString(body, position, end - position)); }
                catch (DecoderFallbackException) { throw Bad("Request options must be UTF-8."); }
            }
            position = end + delimiter.Length;
            if (body.AsSpan(position).StartsWith("--"u8))
            {
                position += 2;
                if (body.AsSpan(position).StartsWith("\r\n"u8)) position += 2;
                if (position != body.Length) throw Bad("Unexpected multipart trailing data.");
                break;
            }
            position += 2; // FindDelimiter requires CRLF for a nonfinal delimiter.
        }
        if (audio == null) throw Bad("An audio file is required.");
        return (audio, fileName);
    }

    private static int FindDelimiter(byte[] body, int start, byte[] delimiter)
    {
        while (start < body.Length)
        {
            var relative = body.AsSpan(start).IndexOf(delimiter);
            if (relative < 0) return -1;
            var found = start + relative;
            var tail = body.AsSpan(found + delimiter.Length);
            if (tail.StartsWith("\r\n"u8) || tail.StartsWith("--"u8)) return found;
            start = found + 1;
        }
        return -1;
    }

    private static string Unquote(string? value) => value is { Length: >= 2 } && value[0] == '"' && value[^1] == '"'
        ? value[1..^1] : value ?? "";
    private static LocalApiRequestException Bad(string message) => new(400, message);
}
