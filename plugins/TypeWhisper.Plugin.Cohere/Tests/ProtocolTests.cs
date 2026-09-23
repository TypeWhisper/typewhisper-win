using System.Net;
using System.Text.Json;
using Xunit;
public sealed partial class ProviderTests
{
private static HttpResponseMessage Success(HttpRequestMessage request,string? body)
{Assert.Equal("api.cohere.com",request.RequestUri!.Host);Assert.Contains("cohere-transcribe-03-2026",body);return Json("""{"text":"Hallo Welt"}""");}
private static void CheckRequests(List<(string Path,string? Body)> requests)
{Assert.StartsWith("/v2/audio/transcriptions",requests[0].Path);}
}
