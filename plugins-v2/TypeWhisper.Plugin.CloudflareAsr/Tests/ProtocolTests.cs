using System.Net;
using System.Text.Json;
using Xunit;
public sealed partial class ProviderTests
{
private static HttpResponseMessage Success(HttpRequestMessage request,string? body)
{Assert.Equal("/client/v4/accounts/"+new string('a',32)+"/ai/run/@cf/openai/whisper",request.RequestUri!.AbsolutePath);Assert.Equal(HttpMethod.Post,request.Method);Assert.Equal("Bearer",request.Headers.Authorization!.Scheme);Assert.Equal(Audio(),request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());Assert.Equal("application/octet-stream",request.Content!.Headers.ContentType!.MediaType);return Json("""{"success":true,"result":{"text":"Hallo Welt"}}""");}
private static void CheckRequests(List<(string Path,string? Body)> requests)
{Assert.Equal("/client/v4/accounts/"+new string('a',32)+"/ai/run/@cf/openai/whisper",requests[0].Path);}
}
