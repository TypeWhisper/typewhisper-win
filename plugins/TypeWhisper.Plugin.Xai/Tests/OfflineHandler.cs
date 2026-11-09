namespace PortableMigration.Tests;

/// <summary>Fails every request, for plugins whose tests must not reach a provider.</summary>
internal sealed class OfflineHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"Unexpected network request to {request.RequestUri?.Host}.");
}
