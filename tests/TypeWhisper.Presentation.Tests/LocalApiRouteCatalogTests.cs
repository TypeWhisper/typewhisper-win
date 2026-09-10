using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LocalApiRouteCatalogTests
{
    [Fact]
    public void CatalogContainsMacContractAndWindowsDiscoveryWithoutDuplicates()
    {
        var expected = new Dictionary<string, string[]>
        {
            ["/v1/status"] = ["GET"], ["/v1/models"] = ["GET", "DELETE"],
            ["/v1/models/load"] = ["POST"], ["/v1/models/unload"] = ["POST"],
            ["/v1/transcribe"] = ["POST"], ["/v1/transcribe/local-file"] = ["POST"],
            ["/v1/history"] = ["GET", "DELETE"], ["/v1/rules"] = ["GET"], ["/v1/profiles"] = ["GET"],
            ["/v1/rules/toggle"] = ["PUT"], ["/v1/profiles/toggle"] = ["PUT"],
            ["/v1/dictation/start"] = ["POST"], ["/v1/dictation/stop"] = ["POST"],
            ["/v1/dictation/status"] = ["GET"], ["/v1/dictation/transcription"] = ["GET"],
            ["/v1/recorder/start"] = ["POST"], ["/v1/recorder/stop"] = ["POST"],
            ["/v1/recorder/status"] = ["GET"], ["/v1/recorder/session"] = ["GET"],
            ["/v1/dictionary/terms"] = ["GET", "PUT", "DELETE"],
            ["/v1/dictionary/corrections"] = ["GET", "PUT", "DELETE"],
            ["/v1/settings/export"] = ["GET"], ["/v1/settings/import"] = ["POST"]
        }.SelectMany(pair => pair.Value.Select(method => new LocalApiRoute(method, pair.Key))).ToHashSet();
        Assert.Equal(29, expected.Count);
        Assert.True(expected.SetEquals(LocalApiRouteCatalog.MacRoutes));
        Assert.Equal(32, LocalApiRouteCatalog.Routes.Count);
        Assert.Equal(32, LocalApiRouteCatalog.Routes.Distinct().Count());
        Assert.True(LocalApiRouteCatalog.Contains("GET", "/v1/capabilities"));
        Assert.True(LocalApiRouteCatalog.Contains("GET", "/docs"));
        Assert.True(LocalApiRouteCatalog.Contains("GET", "/docs/"));
        Assert.False(LocalApiRouteCatalog.Contains("POST", "/v1/profiles/toggle"));
        Assert.False(LocalApiRouteCatalog.Contains("get", "/v1/status"));
    }

    [Fact]
    public void CatalogDataRoutesAreAllDispatchedAndRejectUnsupportedMethods()
    {
        var directory = Path.Combine(Path.GetTempPath(), "typewhisper-route-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var handler = new LocalApiDataHandler(Path.Combine(directory, "dictionary.json"), Path.Combine(directory, "workflows.json"));
            var routes = LocalApiRouteCatalog.MacRoutes.Where(route => route.Path.StartsWith("/v1/dictionary/", StringComparison.Ordinal)
                || route.Path.StartsWith("/v1/rules", StringComparison.Ordinal) || route.Path.StartsWith("/v1/profiles", StringComparison.Ordinal)).ToArray();
            Assert.Equal(10, routes.Length);
            foreach (var route in routes)
            {
                var response = handler.Handle(new(route.Method, route.Path, [], null, new Dictionary<string, string?>()));
                Assert.NotNull(response);
                // Missing mutation arguments are a 400, never an unknown route or wrong method.
                Assert.Contains(response.StatusCode, new[] { 200, 400 });
                var wrongMethod = handler.Handle(new("PATCH", route.Path, [], null, new Dictionary<string, string?>()));
                Assert.Equal(405, wrongMethod!.StatusCode);
            }
            Assert.Null(handler.Handle(new("GET", "/unregistered", [], null, new Dictionary<string, string?>())));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
