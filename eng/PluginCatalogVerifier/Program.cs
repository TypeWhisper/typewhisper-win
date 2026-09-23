using System.Net;
using TypeWhisper.PluginHost;

if (args.Length != 1) throw new ArgumentException("Usage: PluginCatalogVerifier <catalog.json>");
using var http = new HttpClient(new LocalCatalogHandler(Path.GetFullPath(args[0])));
var entries = await new PortablePluginCatalog(http).FetchAsync();
Console.WriteLine($"Host catalog validation passed: {entries.Count} entries.");

// Exercise the actual client, including its size limit, JSON schema, ID/version/category
// validation and duplicate handling, without making a network request.
sealed class LocalCatalogHandler(string path) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(File.OpenRead(path))
        });
    }
}
