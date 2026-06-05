using System.Collections.Specialized;
using System.IO;
using System.Text;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public class HttpApiRequestParserTests
{
    [Fact]
    public void ParseTranscribe_ReadsMultipartFileAndFields()
    {
        var boundary = "Boundary123";
        var body = Multipart(boundary,
            ("language_hint", null, null, "de"u8.ToArray()),
            ("language_hint", null, null, "en"u8.ToArray()),
            ("task", null, null, "translate"u8.ToArray()),
            ("target_language", null, null, "fr"u8.ToArray()),
            ("response_format", null, null, "verbose_json"u8.ToArray()),
            ("prompt", null, null, "Project names"u8.ToArray()),
            ("engine", null, null, "groq"u8.ToArray()),
            ("model", null, null, "whisper-large-v3"u8.ToArray()),
            ("normalize_numbers", null, null, "off"u8.ToArray()),
            ("file", "audio.wav", "audio/wav", [1, 2, 3, 4]));

        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection { ["await_download"] = "1" },
            new Dictionary<string, string> { ["content-type"] = $"multipart/form-data; boundary={boundary}" },
            body);

        var parsed = HttpApiRequestParser.ParseTranscribe(request);

        Assert.Equal([1, 2, 3, 4], parsed.AudioData);
        Assert.Equal("wav", parsed.FileExtension);
        Assert.Null(parsed.Language);
        Assert.Equal(["de", "en"], parsed.LanguageHints);
        Assert.Equal(TranscriptionTask.Translate, parsed.Task);
        Assert.Equal("fr", parsed.TargetLanguage);
        Assert.Equal("verbose_json", parsed.ResponseFormat);
        Assert.Equal("Project names", parsed.Prompt);
        Assert.Equal("groq", parsed.Engine);
        Assert.Equal("whisper-large-v3", parsed.Model);
        Assert.False(parsed.NormalizeNumbers);
        Assert.True(parsed.AwaitDownload);
    }

    [Fact]
    public void ParseTranscribe_ReadsUnquotedMultipartDispositionParameters()
    {
        var body = Encoding.UTF8.GetBytes(
            "--Boundary123\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Disposition: form-data; name=engine\r\n" +
            "\r\n" +
            "sherpa-onnx\r\n" +
            "--Boundary123\r\n" +
            "Content-Disposition: form-data; name=file; filename=audio.wav; filename*=utf-8''audio.wav\r\n" +
            "Content-Type: audio/wav\r\n" +
            "\r\n" +
            "audio-bytes\r\n" +
            "--Boundary123--\r\n");

        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string> { ["content-type"] = "multipart/form-data; boundary=\"Boundary123\"" },
            body);

        var parsed = HttpApiRequestParser.ParseTranscribe(request);

        Assert.Equal("sherpa-onnx", parsed.Engine);
        Assert.Equal("wav", parsed.FileExtension);
        Assert.Equal(Encoding.UTF8.GetBytes("audio-bytes"), parsed.AudioData);
    }

    [Fact]
    public void ParseTranscribe_ReadsRawBodyHeaders()
    {
        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "audio/mpeg",
                ["x-language-hints"] = "de, en",
                ["x-task"] = "translate",
                ["x-target-language"] = "es",
                ["x-response-format"] = "verbose_json",
                ["x-prompt"] = "Names",
                ["x-engine"] = "openai",
                ["x-model"] = "gpt-4o-transcribe",
                ["x-normalize-numbers"] = "yes"
            },
            [9, 8, 7]);

        var parsed = HttpApiRequestParser.ParseTranscribe(request);

        Assert.Equal("mp3", parsed.FileExtension);
        Assert.Equal(["de", "en"], parsed.LanguageHints);
        Assert.Equal(TranscriptionTask.Translate, parsed.Task);
        Assert.Equal("es", parsed.TargetLanguage);
        Assert.Equal("verbose_json", parsed.ResponseFormat);
        Assert.Equal("Names", parsed.Prompt);
        Assert.Equal("openai", parsed.Engine);
        Assert.Equal("gpt-4o-transcribe", parsed.Model);
        Assert.True(parsed.NormalizeNumbers);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    public void ParseTranscribe_ReadsNormalizeNumbersBooleanVariants(string rawValue, bool expected)
    {
        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "audio/wav",
                ["x-normalize-numbers"] = rawValue
            },
            [9, 8, 7]);

        var parsed = HttpApiRequestParser.ParseTranscribe(request);

        Assert.Equal(expected, parsed.NormalizeNumbers);
    }

    [Fact]
    public void ParseTranscribe_RejectsInvalidNormalizeNumbersHeader()
    {
        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "audio/wav",
                ["x-normalize-numbers"] = "maybe"
            },
            [9, 8, 7]);

        var ex = Assert.Throws<HttpApiRequestException>(() => HttpApiRequestParser.ParseTranscribe(request));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Invalid 'x-normalize-numbers' value", ex.Message);
    }

    [Fact]
    public void ParseTranscribe_RejectsInvalidMultipartNormalizeNumbers()
    {
        var boundary = "Boundary123";
        var body = Multipart(boundary,
            ("normalize_numbers", null, null, "maybe"u8.ToArray()),
            ("file", "audio.wav", "audio/wav", [1]));

        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string> { ["content-type"] = $"multipart/form-data; boundary={boundary}" },
            body);

        var ex = Assert.Throws<HttpApiRequestException>(() => HttpApiRequestParser.ParseTranscribe(request));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Invalid 'normalize_numbers' value", ex.Message);
    }

    [Fact]
    public void ParseTranscribe_RejectsLanguageAndHintsTogether()
    {
        var boundary = "Boundary123";
        var body = Multipart(boundary,
            ("language", null, null, "de"u8.ToArray()),
            ("language_hint", null, null, "en"u8.ToArray()),
            ("file", "audio.wav", "audio/wav", [1]));

        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string> { ["content-type"] = $"multipart/form-data; boundary={boundary}" },
            body);

        var ex = Assert.Throws<HttpApiRequestException>(() => HttpApiRequestParser.ParseTranscribe(request));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("language", ex.Message);
    }

    [Fact]
    public void ParseTranscribe_RejectsMultipartWithoutFile()
    {
        var boundary = "Boundary123";
        var body = Multipart(boundary, ("language", null, null, "de"u8.ToArray()));
        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string> { ["content-type"] = $"multipart/form-data; boundary={boundary}" },
            body);

        var ex = Assert.Throws<HttpApiRequestException>(() => HttpApiRequestParser.ParseTranscribe(request));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("file", ex.Message);
    }

    [Fact]
    public void ParseTranscribeLocalFile_RejectsNullLanguageHints()
    {
        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe/local-file",
            new NameValueCollection(),
            new Dictionary<string, string> { ["content-type"] = "application/json" },
            Encoding.UTF8.GetBytes("""{"path":"C:\\Audio\\test.wav","language_hints":null}"""));

        var ex = Assert.Throws<HttpApiRequestException>(() => HttpApiRequestParser.ParseTranscribeLocalFile(request));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("language_hints", ex.Message);
    }

    [Fact]
    public void ParseTranscribeLocalFile_ReadsNormalizeNumbers()
    {
        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe/local-file",
            new NameValueCollection(),
            new Dictionary<string, string> { ["content-type"] = "application/json" },
            Encoding.UTF8.GetBytes("""{"path":"C:\\Audio\\test.wav","normalize_numbers":false}"""));

        var parsed = HttpApiRequestParser.ParseTranscribeLocalFile(request);

        Assert.False(parsed.NormalizeNumbers);
    }

    private static byte[] Multipart(
        string boundary,
        params (string Name, string? FileName, string? ContentType, byte[] Data)[] parts)
    {
        using var body = new MemoryStream();
        foreach (var part in parts)
        {
            Write(body, $"--{boundary}\r\n");
            var disposition = $"Content-Disposition: form-data; name=\"{part.Name}\"";
            if (part.FileName is not null)
                disposition += $"; filename=\"{part.FileName}\"";
            Write(body, disposition + "\r\n");
            if (part.ContentType is not null)
                Write(body, $"Content-Type: {part.ContentType}\r\n");
            Write(body, "\r\n");
            body.Write(part.Data);
            Write(body, "\r\n");
        }

        Write(body, $"--{boundary}--\r\n");
        return body.ToArray();
    }

    private static void Write(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
    }
}
