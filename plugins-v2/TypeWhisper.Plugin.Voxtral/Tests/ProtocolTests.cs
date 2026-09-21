using System.Net;
using System.Text.Json;
using Xunit;
public sealed partial class ProviderTests
{
private static HttpResponseMessage Success(HttpRequestMessage request,string? body)
{Assert.Equal("api.mistral.ai",request.RequestUri!.Host);Assert.Contains("voxtral-mini-latest",body);return Json("""{"text":"Hallo Welt"}""");}
private static void CheckRequests(List<(string Path,string? Body)> requests)
{Assert.StartsWith("/v1/audio/transcriptions",requests[0].Path);}
}
