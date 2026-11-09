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

    // A button pressed while a key change writes its secret still fetches with the old key. Its list must not stay in
    // use once the new key is in place.
    [Fact]
    public async Task ARefreshButtonPressedDuringAKeyChangeLeavesNoOldAccountList()
    {
        var storeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            await releaseAnswer.Task;
            return CatalogResponse(request, """{ "data": [ { "id": "grok-first-only" } ] }""", RefreshedVoices);
        });
        var host = CatalogHost("first-key", RefreshNow, RefreshNow);
        using var sut = new XaiPlugin(new HttpClient(handler), clock: new TestClock(RefreshNow));
        await sut.ActivateAsync(host);
        host.BeforeStoreSecret = async () => { storeStarted.TrySetResult(); await releaseStore.Task; };

        var keyChange = sut.SetApiKeyAsync("second-key");
        await storeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var press = sut.ExecuteSettingsActionAsync("models", CancellationToken.None);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(10));
        releaseStore.SetResult();
        await keyChange.WaitAsync(TimeSpan.FromSeconds(10));
        releaseAnswer.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => press.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(sut.SupportedModels, model => model.Id == "grok-first-only");
    }

    private static HttpResponseMessage JsonAnswer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class CancellableHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
