using System.Net;
using System.Text.Json;
using Xunit;
public sealed partial class ProviderTests
{
private static HttpResponseMessage Success(HttpRequestMessage request,string? body)
{Assert.Equal("api.linear.app",request.RequestUri!.Host);using var document=JsonDocument.Parse(body!);var input=document.RootElement.GetProperty("variables").GetProperty("input");Assert.Equal("Hallo Welt",input.GetProperty("description").GetString());Assert.Equal("12345678-1234-1234-1234-123456789abc",input.GetProperty("teamId").GetString());return Json("""{"data":{"issueCreate":{"success":true,"issue":{"url":"https://linear.app/team/issue/TST-1"}}}}""");}
private static void CheckRequests(List<(string Path,string? Body)> requests)
{Assert.StartsWith("/graphql",requests[0].Path);}
}
