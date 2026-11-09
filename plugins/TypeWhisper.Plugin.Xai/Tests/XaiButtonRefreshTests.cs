using System.Net;
using System.Net.Http;
using System.Text;
using TypeWhisper.Plugin.Xai;

namespace TypeWhisper.PluginSystem.Tests;

public partial class XaiPluginTests
{
    // Both lists are due at activation. The user presses "Refresh text models" while the background refresh is still
    // loading the models, and the press fails. The voices the background refresh would have loaded next still load.
    [Fact]
    public async Task AFailedModelsButtonStillLetsTheDueVoicesRefresh()
    {
        var modelRequests = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paths = new List<string>();
        var handler = new CancellableHandler(async (request, ct) =>
        {
            lock (paths) paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri!.AbsolutePath == "/v1/tts/voices")
                return JsonAnswer(RefreshedVoices);
            if (Interlocked.Increment(ref modelRequests) == 1)
            {
                // The background request is slow but honours cancellation.
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return JsonAnswer("{}", HttpStatusCode.ServiceUnavailable);
        });
        var host = CatalogHost("xai-key", modelsFetchedAt: null, voicesFetchedAt: null);
        using var sut = CreateRefreshingPlugin(handler);
        await sut.ActivateAsync(host);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ExecuteSettingsActionAsync("models", CancellationToken.None));
        await sut.CatalogRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));

        lock (paths) Assert.Contains("/v1/tts/voices", paths);
        Assert.Equal(RefreshNow, sut.FetchedVoicesAt);
    }

    private static HttpResponseMessage JsonAnswer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class CancellableHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
