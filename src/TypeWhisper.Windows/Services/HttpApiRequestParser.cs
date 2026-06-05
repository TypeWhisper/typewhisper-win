using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

internal sealed record HttpApiRequest(
    string Method,
    string Path,
    NameValueCollection QueryString,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body)
{
    public static async Task<HttpApiRequest> FromListenerRequestAsync(
        HttpListenerRequest request,
        CancellationToken ct)
    {
        byte[] body;
        await using (var buffer = new MemoryStream())
        {
            await request.InputStream.CopyToAsync(buffer, ct);
            body = buffer.ToArray();
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in request.Headers.AllKeys)
        {
            if (key is not null && request.Headers[key] is { } value)
                headers[key] = value;
        }

        return new HttpApiRequest(
            request.HttpMethod,
            request.Url?.AbsolutePath ?? "",
            request.QueryString,
            headers,
            body);
    }
}

internal sealed record HttpApiResponse
{
    public int StatusCode { get; }
    public string Body { get; }
    public string ContentType { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }

    public HttpApiResponse(
        int statusCode,
        string body,
        string contentType = "application/json",
        IReadOnlyDictionary<string, string>? headers = null)
    {
        StatusCode = statusCode;
        Body = body;
        ContentType = contentType;
        Headers = headers ?? new Dictionary<string, string>();
    }
}

internal sealed class HttpApiRequestException : Exception
{
    public int StatusCode { get; }

    public HttpApiRequestException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}

internal sealed record TranscribeApiRequest(
    byte[] AudioData,
    string FileExtension,
    string? Language,
    IReadOnlyList<string> LanguageHints,
    TranscriptionTask Task,
    string? TargetLanguage,
    string ResponseFormat,
    string? Prompt,
    string? Engine,
    string? Model,
    bool AwaitDownload,
    bool? NormalizeNumbers);

internal sealed record LocalFileTranscribeApiRequest(
    string Path,
    string? Language,
    IReadOnlyList<string> LanguageHints,
    TranscriptionTask Task,
    string? TargetLanguage,
    string ResponseFormat,
    string? Prompt,
    string? Engine,
    string? Model,
    bool AwaitDownload,
    bool? NormalizeNumbers);

internal sealed record MultipartPart(
    string Name,
    string? FileName,
    string? ContentType,
    byte[] Data);

internal static class HttpApiRequestParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public static TranscribeApiRequest ParseTranscribe(HttpApiRequest request)
    {
        var contentType = Header(request.Headers, "content-type") ?? "";
        byte[] audioData;
        var fileExtension = "wav";
        string? language = null;
        var languageHints = new List<string>();
        var task = TranscriptionTask.Transcribe;
        string? targetLanguage = null;
        var responseFormat = "json";
        string? prompt = null;
        string? engine = null;
        string? model = null;
        bool? normalizeNumbers = null;

        if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            var boundary = ExtractBoundary(contentType)
                ?? throw new HttpApiRequestException(400, "Missing multipart boundary");
            var parts = ParseMultipart(request.Body, boundary);
            var filePart = parts.FirstOrDefault(p => p.Name == "file")
                ?? throw new HttpApiRequestException(400, "Missing 'file' part in multipart form data");

            audioData = filePart.Data;
            fileExtension = ExtensionFromFileName(filePart.FileName)
                ?? ExtensionFromMime(filePart.ContentType)
                ?? "wav";

            language = Field(parts, "language");
            languageHints.AddRange(Fields(parts, "language_hint"));
            task = ParseTask(Field(parts, "task"));
            targetLanguage = Field(parts, "target_language");
            responseFormat = Field(parts, "response_format") ?? "json";
            prompt = Field(parts, "prompt");
            engine = Field(parts, "engine");
            model = Field(parts, "model");
            normalizeNumbers = ParseBooleanOverride(Field(parts, "normalize_numbers"), "normalize_numbers");
        }
        else if (request.Body.Length > 0)
        {
            audioData = request.Body;
            fileExtension = ExtensionFromMime(contentType) ?? "wav";
            language = Clean(Header(request.Headers, "x-language"));
            languageHints.AddRange(
                (Header(request.Headers, "x-language-hints") ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(v => !string.IsNullOrWhiteSpace(v)));
            task = ParseTask(Header(request.Headers, "x-task"));
            targetLanguage = Clean(Header(request.Headers, "x-target-language"));
            responseFormat = Clean(Header(request.Headers, "x-response-format")) ?? "json";
            prompt = Clean(Header(request.Headers, "x-prompt"));
            engine = Clean(Header(request.Headers, "x-engine"));
            model = Clean(Header(request.Headers, "x-model"));
            normalizeNumbers = ParseBooleanOverride(
                Clean(Header(request.Headers, "x-normalize-numbers")),
                "x-normalize-numbers");
        }
        else
        {
            throw new HttpApiRequestException(400, "No audio data provided");
        }

        if (audioData.Length == 0)
            throw new HttpApiRequestException(400, "Empty audio data");

        if (!string.IsNullOrWhiteSpace(language) && languageHints.Count > 0)
            throw new HttpApiRequestException(400, "Use either 'language' or 'language_hint', not both");

        var awaitDownload = string.Equals(request.QueryString["await_download"], "1", StringComparison.Ordinal)
            || string.Equals(request.QueryString["await_download"], "true", StringComparison.OrdinalIgnoreCase);

        return new TranscribeApiRequest(
            audioData,
            fileExtension,
            language,
            languageHints,
            task,
            targetLanguage,
            responseFormat,
            prompt,
            engine,
            model,
            awaitDownload,
            normalizeNumbers);
    }

    public static LocalFileTranscribeApiRequest ParseTranscribeLocalFile(HttpApiRequest request)
    {
        if (request.Body.Length == 0)
            throw new HttpApiRequestException(400, "Missing JSON body");

        LocalFileTranscribePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LocalFileTranscribePayload>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new HttpApiRequestException(400, "Invalid JSON body");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Path))
            throw new HttpApiRequestException(400, "Invalid JSON body");

        if (payload.LanguageHints is null)
            throw new HttpApiRequestException(400, "'language_hints' must be an array or omitted");

        if (!string.IsNullOrWhiteSpace(payload.Language) && payload.LanguageHints.Count > 0)
            throw new HttpApiRequestException(400, "Use either 'language' or 'language_hint', not both");

        var awaitDownload = string.Equals(request.QueryString["await_download"], "1", StringComparison.Ordinal)
            || string.Equals(request.QueryString["await_download"], "true", StringComparison.OrdinalIgnoreCase);

        return new LocalFileTranscribeApiRequest(
            payload.Path.Trim(),
            Clean(payload.Language),
            payload.LanguageHints
                .Select(Clean)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList(),
            ParseTask(payload.Task),
            Clean(payload.TargetLanguage),
            Clean(payload.ResponseFormat) ?? "json",
            Clean(payload.Prompt),
            Clean(payload.Engine),
            Clean(payload.Model),
            awaitDownload || payload.AwaitDownload,
            payload.NormalizeNumbers);
    }

    public static IReadOnlyList<MultipartPart> ParseMultipart(byte[] body, string boundary)
    {
        var boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary);
        var doubleCrlf = Encoding.UTF8.GetBytes("\r\n\r\n");
        var parts = new List<MultipartPart>();
        var searchStart = 0;

        while (searchStart < body.Length)
        {
            var boundaryStart = IndexOf(body, boundaryBytes, searchStart);
            if (boundaryStart < 0)
                break;

            var afterBoundary = boundaryStart + boundaryBytes.Length;
            if (afterBoundary + 1 < body.Length && body[afterBoundary] == (byte)'-' && body[afterBoundary + 1] == (byte)'-')
                break;

            var partHeaderStart = afterBoundary;
            if (partHeaderStart + 1 < body.Length && body[partHeaderStart] == (byte)'\r' && body[partHeaderStart + 1] == (byte)'\n')
                partHeaderStart += 2;

            var headerEnd = IndexOf(body, doubleCrlf, partHeaderStart);
            if (headerEnd < 0)
                break;

            var partBodyStart = headerEnd + doubleCrlf.Length;
            var nextBoundary = IndexOf(body, boundaryBytes, partBodyStart);
            if (nextBoundary < 0)
                break;

            var partBodyEnd = nextBoundary;
            if (partBodyEnd >= 2 && body[partBodyEnd - 2] == (byte)'\r' && body[partBodyEnd - 1] == (byte)'\n')
                partBodyEnd -= 2;

            if (partBodyEnd < partBodyStart)
            {
                searchStart = nextBoundary;
                continue;
            }

            var headers = Encoding.UTF8.GetString(body, partHeaderStart, headerEnd - partHeaderStart);
            var parsedHeaders = ParsePartHeaders(headers);
            if (!string.IsNullOrEmpty(parsedHeaders.Name))
            {
                var data = new byte[partBodyEnd - partBodyStart];
                Buffer.BlockCopy(body, partBodyStart, data, 0, data.Length);
                parts.Add(new MultipartPart(
                    parsedHeaders.Name,
                    parsedHeaders.FileName,
                    parsedHeaders.ContentType,
                    data));
            }

            searchStart = nextBoundary;
        }

        return parts;
    }

    internal static string? ExtractBoundary(string contentType)
    {
        foreach (var part in contentType.Split(';', StringSplitOptions.TrimEntries))
        {
            if (!part.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
                continue;

            var boundary = part["boundary=".Length..].Trim();
            if (boundary.Length >= 2 && boundary[0] == '"' && boundary[^1] == '"')
                boundary = boundary[1..^1];
            return string.IsNullOrWhiteSpace(boundary) ? null : boundary;
        }

        return null;
    }

    private static (string Name, string? FileName, string? ContentType) ParsePartHeaders(string headers)
    {
        string? name = null;
        string? fileName = null;
        string? contentType = null;

        foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;

            var headerName = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (headerName.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
            {
                name = ExtractDispositionParameter(value, "name");
                fileName = ExtractDispositionParameter(value, "filename")
                    ?? ExtractDispositionParameter(value, "filename*");
            }
            else if (headerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
            }
        }

        return (name ?? "", fileName, contentType);
    }

    private static string? ExtractDispositionParameter(string value, string key)
    {
        foreach (var part in value.Split(';', StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            if (equals < 0)
                continue;

            var partKey = part[..equals].Trim();
            if (!partKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            var parameterValue = part[(equals + 1)..].Trim();
            if (parameterValue.Length >= 2 && parameterValue[0] == '"' && parameterValue[^1] == '"')
                parameterValue = parameterValue[1..^1];

            if (key.EndsWith('*') && parameterValue.Contains("''", StringComparison.Ordinal))
                parameterValue = parameterValue[(parameterValue.IndexOf("''", StringComparison.Ordinal) + 2)..];

            return string.IsNullOrWhiteSpace(parameterValue)
                ? null
                : Uri.UnescapeDataString(parameterValue);
        }

        return null;
    }

    private static string? Header(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : null;

    private static string? Field(IEnumerable<MultipartPart> parts, string name) =>
        parts.Where(p => p.Name == name)
            .Select(p => Clean(Encoding.UTF8.GetString(p.Data)))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static IEnumerable<string> Fields(IEnumerable<MultipartPart> parts, string name) =>
        parts.Where(p => p.Name == name)
            .Select(p => Clean(Encoding.UTF8.GetString(p.Data)))
            .Where(v => !string.IsNullOrWhiteSpace(v))!;

    private static string? Clean(string? value)
    {
        var cleaned = value?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static TranscriptionTask ParseTask(string? value) =>
        string.Equals(value?.Trim(), "translate", StringComparison.OrdinalIgnoreCase)
            ? TranscriptionTask.Translate
            : TranscriptionTask.Transcribe;

    private static bool? ParseBooleanOverride(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new HttpApiRequestException(400, $"Invalid '{fieldName}' value")
        };
    }

    private static string? ExtensionFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var extension = Path.GetExtension(fileName).TrimStart('.').Trim();
        return string.IsNullOrWhiteSpace(extension) ? null : extension.ToLowerInvariant();
    }

    private static string? ExtensionFromMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
            return null;

        var lower = mime.ToLowerInvariant();
        if (lower.Contains("wav") || lower.Contains("wave")) return "wav";
        if (lower.Contains("mp3") || lower.Contains("mpeg")) return "mp3";
        if (lower.Contains("m4a") || lower.Contains("mp4")) return "m4a";
        if (lower.Contains("flac")) return "flac";
        if (lower.Contains("ogg")) return "ogg";
        if (lower.Contains("aac")) return "aac";
        if (lower.Contains("webm")) return "webm";
        return null;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int startIndex)
    {
        if (needle.Length == 0)
            return startIndex;

        for (var i = startIndex; i <= haystack.Length - needle.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j])
                    continue;

                found = false;
                break;
            }

            if (found)
                return i;
        }

        return -1;
    }

    private sealed record LocalFileTranscribePayload
    {
        public string? Path { get; init; }
        public string? Language { get; init; }
        public IReadOnlyList<string>? LanguageHints { get; init; } = [];
        public string? Task { get; init; }
        public string? TargetLanguage { get; init; }
        public string? ResponseFormat { get; init; }
        public string? Prompt { get; init; }
        public string? Engine { get; init; }
        public string? Model { get; init; }
        public bool AwaitDownload { get; init; }
        public bool? NormalizeNumbers { get; init; }
    }
}
