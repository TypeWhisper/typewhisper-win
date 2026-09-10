using System.Net.Http.Headers;
using System.Text;
using TypeWhisper.Presentation;
using Xunit;

public sealed class LocalApiTranscriptionTests
{
    private static LocalApiRequest Request(byte[] body, string? contentType = "audio/wav",
        string path = "/v1/transcribe", IReadOnlyDictionary<string, string?>? query = null) =>
        new("POST", path, body, contentType, query ?? new Dictionary<string, string?>());

    [Fact]
    public void RawCopiesBytesAndPreservesExplicitOptions()
    {
        byte[] bytes = [0, 255, 10, 13, 23];
        var parsed = LocalApiTranscription.Parse(Request(bytes, query: new Dictionary<string, string?>
        {
            ["language"] = "de", ["task"] = "translate", ["response_format"] = "text",
            ["model"] = "Größe", ["engine"] = "whisper"
        }));
        bytes[0] = 99;
        Assert.Equal(new byte[] { 0, 255, 10, 13, 23 }, parsed.Audio.ToArray());
        Assert.Equal("de", parsed.Language);
        Assert.Equal("translate", parsed.Task);
        Assert.Equal("text", parsed.ResponseFormat);
        Assert.Equal("Größe", parsed.Model);
        Assert.Equal("whisper", parsed.Engine);
    }

    [Fact]
    public void AbsentOverridesRemainAbsent()
    {
        var parsed = LocalApiTranscription.Parse(Request([1]));
        Assert.Null(parsed.Task);
        Assert.Null(parsed.Language);
        Assert.Null(parsed.Engine);
        Assert.Null(parsed.Model);
        Assert.Equal("json", parsed.ResponseFormat);
    }

    [Fact]
    public void LocalFileParsesMacCliOptionsAndBooleanDefaults()
    {
        var json = """{"path":"C:/audio.wav","language_hints":["DE","en"],"target_language":"pt_BR","engine":"remote","model":"new-model","apply_corrections":false}""";
        var parsed = LocalApiTranscription.Parse(Request(Encoding.UTF8.GetBytes(json), "application/json",
            "/v1/transcribe/local-file", new Dictionary<string, string?> { ["await_download"] = "1" }));
        Assert.Equal(new[] { "de", "en" }, parsed.LanguageHints);
        Assert.Equal("pt-br", parsed.TargetLanguage);
        Assert.Equal("remote", parsed.Engine);
        Assert.Equal("new-model", parsed.Model);
        Assert.True(parsed.AwaitDownload);
        Assert.False(parsed.ApplyCorrections);
        var defaults = LocalApiTranscription.Parse(Request([1]));
        Assert.True(defaults.ApplyCorrections);
        Assert.False(defaults.AwaitDownload);
        Assert.Empty(defaults.LanguageHints!);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("ON", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    public async Task MultipartSupportsRepeatedHintsAndMacBooleanForms(string boolean, bool expected)
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent([1, 2]), "file", "a.wav");
        multipart.Add(new StringContent("de"), "language_hint");
        multipart.Add(new StringContent("en"), "language_hint");
        multipart.Add(new StringContent("fr"), "target_language");
        multipart.Add(new StringContent(boolean), "apply_corrections");
        var parsed = LocalApiTranscription.Parse(Request(await multipart.ReadAsByteArrayAsync(),
            multipart.Headers.ContentType!.ToString(), query: new Dictionary<string, string?> { ["await_download"] = "1" }));
        Assert.Equal(new[] { "de", "en" }, parsed.LanguageHints);
        Assert.Equal("fr", parsed.TargetLanguage);
        Assert.Equal(expected, parsed.ApplyCorrections);
        Assert.True(parsed.AwaitDownload);
    }

    [Theory]
    [InlineData("\"language_hints\":[\"de\",\"en\",\"fr\"]")]
    [InlineData("\"language_hints\":[\"de,en\"]")]
    [InlineData("\"language_hints\":[\"en\"],\"language\":\"de\"")]
    [InlineData("\"language_hints\":\"de\"")]
    [InlineData("\"language_hints\":[1]")]
    [InlineData("\"language_hints\":[\"\"]")]
    [InlineData("\"language_hints\":[\"english\"]")]
    [InlineData("\"language_hints\":[\"en-!\"]")]
    [InlineData("\"apply_corrections\":\"false\"")]
    [InlineData("\"apply_corrections\":0")]
    [InlineData("\"await_download\":\"yes\"")]
    [InlineData("\"target_language\":\"invalid-language\"")]
    public void RejectsMalformedMacOptions(string fields)
    {
        var body = Encoding.UTF8.GetBytes("{\"path\":\"C:/a.wav\"," + fields + "}");
        Assert.Equal(400, Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.Parse(
            Request(body, "application/json", "/v1/transcribe/local-file"))).StatusCode);
    }

    [Fact]
    public async Task RejectsHintsDuplicatedAcrossQueryAndMultipart()
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent([1]), "file", "a.wav");
        multipart.Add(new StringContent("en"), "language_hint");
        var request = Request(await multipart.ReadAsByteArrayAsync(), multipart.Headers.ContentType!.ToString(),
            query: new Dictionary<string, string?> { ["language_hint"] = "de" });
        Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.Parse(request));
    }

    [Theory]
    [InlineData("prompt", "hello")]
    [InlineData("unknown", "value")]
    [InlineData("apply_corrections", "maybe")]
    [InlineData("await_download", "2")]
    [InlineData("language_hints", "de,en,fr")]
    [InlineData("language_hint", "de,en")]
    [InlineData("task", "summarize")]
    [InlineData("response_format", "verbose_json")]
    [InlineData("language", "")]
    [InlineData("model", "x\ny")]
    public void RejectsUnsupportedOrInvalidOptions(string key, string value) =>
        Assert.Equal(400, Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.Parse(
            Request([1], query: new Dictionary<string, string?> { [key] = value }))).StatusCode);

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"path\":3}")]
    [InlineData("{\"path\":null}")]
    [InlineData("{\"path\":\"C:\\\\a.wav\",\"prompt\":\"hello\"}")]
    [InlineData("{\"path\":\"C:\\\\a.wav\",\"language\":true}")]
    [InlineData("{\"path\":\"C:\\\\a.wav\",\"path\":\"C:\\\\b.wav\"}")]
    [InlineData("{\"path\":\"C:\\\\a.wav\",\"language\":\"de\",\"language\":\"en\"}")]
    [InlineData("{\"path\":\"relative.wav\"}")]
    [InlineData("{\"path\":\"C:a.wav\"}")]
    [InlineData("{\"path\":\"C:\\\\a.wav:secret\"}")]
    [InlineData("{\"path\":\"C:\\\\a*.wav\"}")]
    [InlineData("{\"path\":\"\\\\\\\\server\\\\share\\\\a.wav\"}")]
    public void RejectsInvalidLocalFileJson(string json) =>
        Assert.Equal(400, Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.Parse(
            Request(Encoding.UTF8.GetBytes(json), "application/json", "/v1/transcribe/local-file"))).StatusCode);

    [Fact]
    public void LocalFileSupportsUnicodeAndRejectsCrossSourceDuplicates()
    {
        var body = Encoding.UTF8.GetBytes("{\"path\":\"C:/Größe/录音.wav\",\"language\":\"de\"}");
        var request = Request(body, "application/json", "/v1/transcribe/local-file");
        var parsed = LocalApiTranscription.Parse(request);
        Assert.Equal("C:/Größe/录音.wav", parsed.LocalPath);
        Assert.True(parsed.Audio.IsEmpty);
        Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.Parse(request with
        {
            Query = new Dictionary<string, string?> { ["language"] = "de" }
        }));
    }

    [Fact]
    public async Task ParsesActualMultipartSerializationWithBinaryBoundaryPrefixAndUnicode()
    {
        using var multipart = new MultipartFormDataContent("test-boundary");
        var bytes = Encoding.UTF8.GetBytes("\0音声\r\n--test-boundary-extra\r\nxyz");
        using var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(file, "file", "Größe.wav");
        multipart.Add(new StringContent("de"), "language");
        multipart.Add(new StringContent("Modellgröße"), "model");
        var parsed = LocalApiTranscription.Parse(Request(await multipart.ReadAsByteArrayAsync(), multipart.Headers.ContentType!.ToString()));
        Assert.Equal(bytes, parsed.Audio.ToArray());
        Assert.Equal("Größe.wav", parsed.FileName);
        Assert.Equal("de", parsed.Language);
        Assert.Equal("Modellgröße", parsed.Model);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("language")]
    public async Task RejectsMultipartDuplicates(string field)
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent([1]), "file", "a.wav");
        multipart.Add(new StringContent("de"), "language");
        multipart.Add(new StringContent("duplicate"), field);
        var request = Request(await multipart.ReadAsByteArrayAsync(), multipart.Headers.ContentType!.ToString());
        Assert.Equal(400, Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.Parse(request)).StatusCode);
    }

    [Theory]
    [InlineData("multipart/form-data")]
    [InlineData("multipart/form-data; boundary=abc; boundary=abc")]
    [InlineData("multipart/form-data; boundary=\"abc@def\"")]
    [InlineData("multipart/form-data; boundary=\"abc \"")]
    public void RejectsInvalidBoundary(string contentType) =>
        Assert.Equal(400, Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.Parse(Request([1], contentType))).StatusCode);

    [Theory]
    [InlineData("--b\r\nContent-Disposition: form-data; name=\"file\"\r\n\r\nx")]
    [InlineData("--b\r\nContent-Disposition: form-data; name=\"file\"; name=\"other\"\r\n\r\nx\r\n--b--\r\n")]
    [InlineData("--b\r\nContent-Disposition: form-data; name=\"file\"\r\nContent-Transfer-Encoding: base64\r\n\r\nx\r\n--b--\r\n")]
    [InlineData("--b\r\nContent-Disposition: form-data; name=\"file\"\r\n\r\nx\r\n--b--\r\ntrailing")]
    [InlineData("--b\r\nContent-Disposition: form-data; name=\"prompt\"\r\n\r\nx\r\n--b--\r\n")]
    [InlineData("--b\r\nContent-Disposition: form-data; name=\"language\"\r\n\r\nde\r\n--b--\r\n")]
    [InlineData("--b\r\nContent-Disposition: form-data; name=\"file\"\r\n\r\n\r\n--b--\r\n")]
    [InlineData("--b\r\nContent-Disposition: form-data; name=\"file\"\r\nContent-Type: not a media type\r\n\r\nx\r\n--b--\r\n")]
    [InlineData("--b\r\nContent-Disposition: form-data; name=\"language\"\r\nContent-Type: text/plain; charset=iso-8859-1\r\n\r\nde\r\n--b--\r\n")]
    public void RejectsMalformedMultipart(string body) =>
        Assert.Equal(400, Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.Parse(
            Request(Encoding.UTF8.GetBytes(body), "multipart/form-data; boundary=b"))).StatusCode);

    [Fact]
    public void EnforcesBodySizeAndMediaType()
    {
        Assert.Equal(413, Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.Parse(Request(new byte[32 * 1024 * 1024 + 1]))).StatusCode);
        Assert.Equal(415, Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.Parse(Request([1], "text/plain"))).StatusCode);
        Assert.Equal(400, Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.Parse(Request([]))).StatusCode);
    }

    [Theory]
    [InlineData("srt", "1\n01:01:01,235 --> 01:01:02,000\nGröße 世界\n\n")]
    [InlineData("vtt", "WEBVTT\n\n01:01:01.235 --> 01:01:02.000\nGröße 世界\n\n")]
    public void SubtitleOutputUsesRealTimesAndUnicode(string format, string expected)
    {
        var response = LocalApiTranscription.FormatResponse("Größe 世界", [new("Größe 世界", 3661.2345, 3662)], format);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(expected, Encoding.UTF8.GetString(response.Body));
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(2, 1)]
    [InlineData(1, 1)]
    [InlineData(1, 1.0001)]
    [InlineData(double.NaN, 1)]
    [InlineData(0, double.PositiveInfinity)]
    public void RejectsInvalidSubtitleTimes(double start, double end) =>
        Assert.Equal(422, Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.FormatResponse("text", [new("text", start, end)], "srt")).StatusCode);

    [Fact]
    public void MissingTimestampsAreNotFabricatedAndTextStaysUtf8()
    {
        Assert.Equal(422, Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.FormatResponse("text", [], "vtt")).StatusCode);
        Assert.Equal("Größe 世界", Encoding.UTF8.GetString(LocalApiTranscription.FormatResponse("Größe 世界", [], "text").Body));
    }

    [Fact]
    public void JsonUsesStableLowercaseFieldsAndOriginalSegmentTimes()
    {
        var response = LocalApiTranscription.FormatResponse("Größe 世界", [new("Größe 世界", 0.125, 2.5)], "json");
        using var json = System.Text.Json.JsonDocument.Parse(response.Body);
        Assert.Equal("Größe 世界", json.RootElement.GetProperty("text").GetString());
        Assert.Equal(0.125, json.RootElement.GetProperty("segments")[0].GetProperty("start").GetDouble());
        Assert.Equal(2.5, json.RootElement.GetProperty("segments")[0].GetProperty("end").GetDouble());
    }

    [Fact]
    public void MultipartOptionsRejectInvalidUtf8()
    {
        var bytes = Encoding.ASCII.GetBytes("--b\r\nContent-Disposition: form-data; name=\"language\"\r\n\r\n")
            .Concat(new byte[] { 0xff }).Concat(Encoding.ASCII.GetBytes("\r\n--b--\r\n")).ToArray();
        Assert.Equal(400, Assert.Throws<LocalApiRequestException>(() => LocalApiTranscription.Parse(
            Request(bytes, "multipart/form-data; boundary=b"))).StatusCode);
    }
}
