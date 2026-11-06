using System.Net;
using System.Text.Json;
using Xunit;
public sealed partial class ProviderTests
{
private static HttpResponseMessage Success(HttpRequestMessage request,string? body)
{
    Assert.Equal("api.mistral.ai",request.RequestUri!.Host);
    var fields=FormFields(request);
    Assert.Contains(("model","voxtral-mini-transcribe-3-0"),fields);Assert.Contains(("language","de"),fields);
    Assert.DoesNotContain(fields,f=>f.Name=="return_language");
    Assert.Equal(new[]{"TypeWhisper","Marco"},fields.Where(f=>f.Name=="context_bias").Select(f=>f.Value));
    return Json("""{"text":"Hallo Welt"}""");
}
private static void CheckRequests(List<(string Path,string? Body)> requests)
{Assert.StartsWith("/v1/audio/transcriptions",requests[0].Path);}
}
