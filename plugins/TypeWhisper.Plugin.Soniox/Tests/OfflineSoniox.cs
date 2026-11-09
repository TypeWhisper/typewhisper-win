using System.Net.Http;
using TypeWhisper.Plugin.Soniox;

/// <summary>
/// Soniox plugins for tests that expect no request. The public constructor refreshes the model list from
/// Soniox in the background, so tests use this one, whose transport fails on any request.
/// </summary>
internal static class OfflineSoniox
{
    internal static SonioxPlugin Plugin() => new(new HttpClient(new Handler()));

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected network request: {request.RequestUri}");
    }
}
