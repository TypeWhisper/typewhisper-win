using System.Net;
using System.Text.Json;
using Xunit;
public sealed partial class ProviderTests
{
private static HttpResponseMessage Success(HttpRequestMessage request,string? body)
{return request.RequestUri!.AbsolutePath switch {"/v2/jobs"=>Json("""{"id":"job"}"""),"/v2/jobs/job"=>Json("""{"job":{"status":"done"}}"""),"/v2/jobs/job/transcript"=>Json("""{"results":[{"type":"word","alternatives":[{"content":"Hallo"}]},{"type":"word","alternatives":[{"content":"Welt"}]}]}"""),_=>throw new InvalidOperationException("Unexpected path")};}
private static void CheckRequests(List<(string Path,string? Body)> requests)
{Assert.StartsWith("/v2/jobs",requests[0].Path);}
}
