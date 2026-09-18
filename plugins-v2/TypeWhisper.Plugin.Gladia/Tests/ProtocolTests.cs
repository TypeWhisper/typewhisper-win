using System.Net;
using System.Text.Json;
using Xunit;
public sealed partial class ProviderTests
{
private static HttpResponseMessage Success(HttpRequestMessage request,string? body)
{Assert.Equal("fixture-key",Assert.Single(request.Headers.GetValues("x-gladia-key")));return request.RequestUri!.AbsolutePath switch {"/v2/upload"=>Json("""{"audio_url":"https://files.gladia.io/audio.wav"}"""),"/v2/pre-recorded"=>Json("""{"result_url":"https://api.gladia.io/v2/pre-recorded/job"}"""),"/v2/pre-recorded/job"=>Json("""{"status":"done","result":{"transcription":{"full_transcript":"Hallo Welt"}}}"""),_=>throw new InvalidOperationException("Unexpected path")};}
private static void CheckRequests(List<(string Path,string? Body)> requests)
{Assert.StartsWith("/v2/upload",requests[0].Path);}
}
