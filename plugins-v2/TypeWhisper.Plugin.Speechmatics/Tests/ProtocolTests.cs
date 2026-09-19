using System.Net;
using System.Text.Json;
using Xunit;
using TypeWhisper.Plugin.Speechmatics;
public sealed partial class ProviderTests
{
    [Theory]
    [InlineData(null)] [InlineData("auto")] [InlineData("de")]
    public async Task AutomaticBatchUsesLanguageOfFirstRecognizedWord(string? requested)
    {
        using var http = new HttpClient(new Handler((request, body) =>
            request.RequestUri!.AbsolutePath.EndsWith("/transcript", StringComparison.Ordinal) ? Json("""
                {"metadata":{"transcription_config":{"language":"auto"}},"results":[
                  {"type":"punctuation","alternatives":[{"content":"(","language":"fr"}]},
                  {"type":"word","alternatives":[{"content":"Hello","language":"en"}]}]}
                """) : Success(request, body)));
        using var plugin = new SpeechmaticsPlugin(http); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        var result = await plugin.TranscribeAsync(Audio(), requested, false, null, default);
        Assert.Equal("en", result.DetectedLanguage);
    }

    [Theory]
    [InlineData(null, "{\"transcription_config\":{\"language\":\"en\"}}", "en")]
    [InlineData("auto", "{\"transcription_config\":{\"language\":\"de\"}}", "de")]
    [InlineData("en", "{\"transcription_config\":{\"language\":\"de\"}}", "de")]
    [InlineData("de", "{}", "de")]
    [InlineData(null, "{\"transcription_config\":{\"language\":\"auto\"}}", null)]
    [InlineData(null, "{\"transcription_config\":null}", null)]
    public async Task BatchReturnsEffectiveLanguageFromNestedMetadata(string? requested, string metadata, string? expected)
    {
        using var http = new HttpClient(new Handler((request, body) =>
            request.RequestUri!.AbsolutePath.EndsWith("/transcript", StringComparison.Ordinal)
                ? Json("{\"results\":[],\"metadata\":" + metadata + "}") : Success(request, body)));
        using var plugin = new SpeechmaticsPlugin(http); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        var result = await plugin.TranscribeAsync(Audio(), requested, false, null, default);
        Assert.Equal(expected, result.DetectedLanguage);
    }

private static HttpResponseMessage Success(HttpRequestMessage request,string? body)
{return request.RequestUri!.AbsolutePath switch {"/v2/jobs"=>Json("""{"id":"job"}"""),"/v2/jobs/job"=>Json("""{"job":{"status":"done"}}"""),"/v2/jobs/job/transcript"=>Json("""{"results":[{"type":"word","alternatives":[{"content":"Hallo"}]},{"type":"word","alternatives":[{"content":"Welt"}]}]}"""),_=>throw new InvalidOperationException("Unexpected path")};}
private static void CheckRequests(List<(string Path,string? Body)> requests)
{Assert.StartsWith("/v2/jobs",requests[0].Path);}
}
