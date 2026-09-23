using System.Net;
using System.Text.Json;
using Xunit;
public sealed partial class ProviderTests
{
private static HttpResponseMessage Success(HttpRequestMessage request,string? body)
{Assert.Equal("audio-turbo.api.fireworks.ai",request.RequestUri!.Host); Assert.Equal("Bearer",request.Headers.Authorization!.Scheme);return Json("""{"text":"Hallo Welt","language":"de"}""");}
private static void CheckRequests(List<(string Path,string? Body)> requests)
{Assert.StartsWith("/v1/audio/transcriptions",requests[0].Path);}
}
