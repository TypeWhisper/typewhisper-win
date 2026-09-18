using System.Net;
using System.Text.Json;
using Xunit;
public sealed partial class ProviderTests
{
private static HttpResponseMessage Success(HttpRequestMessage request,string? body)
{Assert.Contains(new string('a',32),request.RequestUri!.AbsolutePath);Assert.Equal("application/octet-stream",request.Content!.Headers.ContentType!.MediaType);return Json("""{"success":true,"result":{"text":"Hallo Welt"}}""");}
private static void CheckRequests(List<(string Path,string? Body)> requests)
{Assert.StartsWith("/client/v4/accounts/",requests[0].Path);}
}
