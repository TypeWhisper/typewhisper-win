using System.Net;
using System.Text.Json;
using TypeWhisper.Plugin.Webhook;
namespace PortableMigration.Tests;
public sealed class WebhookBehaviorTests
{
    [Fact]
    public async Task Delivery_UsesSecretHeadersAndWorkflowFilterWithoutChangingText()
    {
        using var f=new PortableFixture();var transport=new Transport();using var p=new WebhookPlugin(new HttpClient(transport));await p.ActivateAsync(f.Host);
        await p.ExecuteSettingsActionAsync("add",default);var id=p.TextSettings[0].Id.Split(':')[0];
        await p.SaveTextSettingAsync(id+":url","https://fixture.invalid/hook",default);
        await p.SaveTextSettingAsync(id+":headers","{\"Authorization\":\"Bearer fixture\"}",default);
        await p.SaveTextSettingAsync(id+":workflows","Work",default);await p.SaveTextSettingAsync(id+":enabled","true",default);
        Assert.DoesNotContain("Bearer fixture",File.ReadAllText(Path.Combine(f.Host.PluginDataDirectory,"settings.json")));
        Assert.Equal("hello",await p.ProcessAsync("hello",new(){ProfileName="Other"},default));Assert.Equal(0,transport.Calls);
        Assert.Equal("hello",await p.ProcessAsync("hello",new(){ProfileName="Work",SourceLanguage="de",AudioDurationSeconds=2},default));
        Assert.Equal(1,transport.Calls);Assert.Contains("HTTP 200",await p.ExecuteSettingsActionAsync("log",default));await p.DeactivateAsync();
    }
    [Theory]
    [InlineData("http://remote.example/hook")][InlineData("file:///tmp/hook")][InlineData("https://user:secret@example.test/hook")]
    public async Task InvalidEndpoint_IsRejectedBeforeEnable(string url)
    {
        using var f=new PortableFixture();using var p=new WebhookPlugin();await p.ActivateAsync(f.Host);await p.ExecuteSettingsActionAsync("add",default);
        var id=p.TextSettings[0].Id.Split(':')[0];await Assert.ThrowsAsync<ArgumentException>(()=>p.SaveTextSettingAsync(id+":url",url,default));
        await Assert.ThrowsAsync<ArgumentException>(()=>p.SaveTextSettingAsync(id+":enabled","true",default));await p.DeactivateAsync();
    }
    [Fact]
    public async Task Cancellation_DoesNotSend()
    {
        using var f=new PortableFixture();var transport=new Transport();using var p=new WebhookPlugin(new HttpClient(transport));await p.ActivateAsync(f.Host);
        await p.ExecuteSettingsActionAsync("add",default);var id=p.TextSettings[0].Id.Split(':')[0];await p.SaveTextSettingAsync(id+":url","http://localhost:8000/hook",default);await p.SaveTextSettingAsync(id+":enabled","true",default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>p.ProcessAsync("hello",new(),new CancellationToken(true)));Assert.Equal(0,transport.Calls);await p.DeactivateAsync();
    }
    private sealed class Transport:HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;Assert.Equal("Bearer fixture",request.Headers.Authorization!.ToString());
            using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));Assert.Equal("hello",json.RootElement.GetProperty("text").GetString());
            Assert.Equal("de",json.RootElement.GetProperty("detectedLanguage").GetString());return new(HttpStatusCode.OK){Content=new StringContent("")};
        }
    }
}
