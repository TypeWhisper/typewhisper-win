using System.Diagnostics;
using System.Net;
using TypeWhisper.Plugin.Webhook;
namespace PortableMigration.Tests;
public sealed class DeliveryBudgetTests
{
    [Fact]
    public async Task UnresponsiveDestinations_ShareBudgetAndPreserveText()
    {
        using var f=new PortableFixture();var transport=new SlowTransport();
        using var p=new WebhookPlugin(new HttpClient(transport),TimeSpan.FromMilliseconds(100));
        await ConfigureAsync(p,f);
        var elapsed=Stopwatch.StartNew();
        Assert.Equal("preserved",await p.ProcessAsync("preserved",new(),default));
        Assert.Equal(1,transport.Calls);Assert.True(elapsed.Elapsed<TimeSpan.FromSeconds(3));
        Assert.Contains("timed out",await p.ExecuteSettingsActionAsync("log",default));
    }
    [Fact]
    public async Task CallerCancellationDuringDelivery_PropagatesAndSkipsRemainingDestinations()
    {
        using var f=new PortableFixture();var transport=new SlowTransport();
        using var p=new WebhookPlugin(new HttpClient(transport));await ConfigureAsync(p,f);
        using var cancel=new CancellationTokenSource();
        var delivery=p.ProcessAsync("preserved",new(),cancel.Token);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>delivery);
        Assert.Equal(1,transport.Calls);
    }
    private static async Task ConfigureAsync(WebhookPlugin plugin,PortableFixture fixture)
    {
        await plugin.ActivateAsync(fixture.Host);
        for(var i=0;i<3;i++)
        {
            await plugin.ExecuteSettingsActionAsync("add",default);var id=plugin.ConnectionIdentity;
            await plugin.SaveProfileSettingsAsync(id,new Dictionary<string,string>{{id+":url","https://fixture.invalid/"+i},{id+":enabled","true"}},null,default);
        }
    }
    private sealed class SlowTransport:HttpMessageHandler
    {
        internal int Calls;
        internal TaskCompletionSource Started { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan,ct);
            return new(HttpStatusCode.OK);
        }
    }
}
