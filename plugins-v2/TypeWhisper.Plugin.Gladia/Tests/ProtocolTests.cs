using System.Net;
using System.Text.Json;
using Xunit;
public sealed partial class ProviderTests
{
    private const string JobId = "45463597-20b7-4af7-b3b3-f5fb778203ab";
    private const string PollPath = "/v2/pre-recorded/" + JobId;
    private static HttpResponseMessage Success(HttpRequestMessage request, string? body)
    {
        Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("x-gladia-key")));
        return request.RequestUri!.AbsolutePath switch
        {
            "/v2/upload" => Json("""{"audio_url":"https://files.gladia.io/audio.wav"}"""),
            "/v2/pre-recorded" => Json(JsonSerializer.Serialize(new { id = JobId, result_url = "https://api.gladia.io/v2/transcription/" + JobId })),
            PollPath => Json("""{"status":"done","result":{"metadata":{"audio_duration":3.125},"transcription":{"full_transcript":"Hallo Welt"}}}"""),
            _ => throw new InvalidOperationException("Unexpected path")
        };
    }
    private static void CheckRequests(List<(string Path,string? Body)> requests)
    {
        Assert.Equal(["/v2/upload", "/v2/pre-recorded", PollPath], requests.Select(r => r.Path));
    }
}
