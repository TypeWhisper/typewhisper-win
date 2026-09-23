using System.Net;
using System.Text.Json;
using TypeWhisper.Plugin.Webhook;
namespace PortableMigration.Tests;
public sealed class ProfileBehaviorTests
{
    [Fact]
    public async Task DraftTest_DoesNotSaveEnableOrForwardSavedCredentialsToAnotherUrl()
    {
        using var f=new PortableFixture();
        string? authorization=null; string? url=null; string? text=null;
        using var p=new WebhookPlugin(new HttpClient(new Handler(async request=> {
            authorization=request.Headers.Authorization?.ToString();url=request.RequestUri!.ToString();
            using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());text=json.RootElement.GetProperty("text").GetString();
            return new(HttpStatusCode.OK);
        })));
        await p.ActivateAsync(f.Host);await p.ExecuteSettingsActionAsync("add",default);var id=p.ConnectionIdentity;
        await p.SaveProfileSettingsAsync(id,new Dictionary<string,string>{{id+":url","https://saved.invalid/"},{id+":headers","{\"Authorization\":\"Bearer private\"}"}},null,default);
        var before=File.ReadAllText(Path.Combine(f.Host.PluginDataDirectory,"settings.json"));
        var result=await p.ExecuteProfileActionAsync(id,"test:"+id,new Dictionary<string,string>{{id+":url","https://draft.invalid/"}},null,default);
        Assert.Contains("HTTP 200",result.Message);Assert.Null(authorization);Assert.Equal("https://draft.invalid/",url);Assert.Equal("Hallo TypeWhisper!",text);
        Assert.Equal(before,File.ReadAllText(Path.Combine(f.Host.PluginDataDirectory,"settings.json")));
        Assert.Equal("false",p.TextSettings.Single(x=>x.Id==id+":enabled").Value);
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task FailedSave_PreservesPreviousConfigurationAndCredentials(bool secretFailure)
    {
        using var f=new PortableFixture();using var p=new WebhookPlugin();await p.ActivateAsync(f.Host);
        await p.ExecuteSettingsActionAsync("add",default);var id=p.ConnectionIdentity;
        await p.SaveProfileSettingsAsync(id,new Dictionary<string,string>{{id+":url","https://saved.invalid/"},{id+":headers","{\"Authorization\":\"Bearer old\"}"}},null,default);
        var path=Path.Combine(f.Host.PluginDataDirectory,"settings.json");var before=File.ReadAllText(path);var secrets=f.Secrets.Values.ToArray();
        if(secretFailure)f.Secrets.FailWrites=true;else Directory.CreateDirectory(path+".tmp");
        var failure=await Record.ExceptionAsync(()=>p.SaveProfileSettingsAsync(id,new Dictionary<string,string>{{id+":name","Replacement"},{id+":enabled","true"},{id+":headers","{\"Authorization\":\"Bearer replacement\"}"}},null,default));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(before,File.ReadAllText(path));Assert.Equal(secrets,f.Secrets.Values.ToArray());
        Assert.Equal("false",p.TextSettings.Single(x=>x.Id==id+":enabled").Value);
    }
    [Fact]
    public async Task DisabledDestination_DoesNotSend_AndHttpFailurePreservesTextAndRedactsLog()
    {
        using var f=new PortableFixture();var calls=0;
        using var p=new WebhookPlugin(new HttpClient(new Handler(request=>{calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError){Content=new StringContent("private response")});})));
        await p.ActivateAsync(f.Host);await p.ExecuteSettingsActionAsync("add",default);var id=p.ConnectionIdentity;
        await p.SaveProfileSettingsAsync(id,new Dictionary<string,string>{{id+":url","https://fixture.invalid/private-url"},{id+":headers","{\"Authorization\":\"Bearer secret-value\"}"}},null,default);
        Assert.Equal("private dictation",await p.ProcessAsync("private dictation",new(),default));Assert.Equal(0,calls);
        await p.SaveTextSettingAsync(id+":enabled","true",default);
        Assert.Equal("private dictation",await p.ProcessAsync("private dictation",new(),default));Assert.Equal(1,calls);
        var log=await p.ExecuteSettingsActionAsync("log",default);Assert.Contains("HTTP 500",log);
        foreach(var secret in new[]{"private dictation","private response","private-url","secret-value"})Assert.DoesNotContain(secret,log);
    }
    [Theory]
    [InlineData("{\"Host\":\"evil.invalid\"}")]
    [InlineData("{\"Authorization\":\"Bearer abc\\r\\nInjected: yes\"}")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"Bad Name\":\"value\"}")]
    [InlineData("{\"Content-Language\":\"de\"}")]
    public async Task InvalidHeaders_DoNotSaveOtherDraftChanges(string headers)
    {
        using var f=new PortableFixture();using var p=new WebhookPlugin();await p.ActivateAsync(f.Host);await p.ExecuteSettingsActionAsync("add",default);var id=p.ConnectionIdentity;
        var before=File.ReadAllText(Path.Combine(f.Host.PluginDataDirectory,"settings.json"));
        await Assert.ThrowsAsync<ArgumentException>(()=>p.SaveProfileSettingsAsync(id,new Dictionary<string,string>{{id+":name","Changed"},{id+":headers",headers}},null,default));
        Assert.Equal(before,File.ReadAllText(Path.Combine(f.Host.PluginDataDirectory,"settings.json")));Assert.Empty(f.Secrets.Values);
    }
    [Fact]
    public async Task SavedProfile_RestartsAndBlankHeadersKeepWhileEmptyObjectClears()
    {
        using var f=new PortableFixture();using var p=new WebhookPlugin();await p.ActivateAsync(f.Host);await p.ExecuteSettingsActionAsync("add",default);var id=p.ConnectionIdentity;
        await p.SaveProfileSettingsAsync(id,new Dictionary<string,string>{{id+":url","https://fixture.invalid/"},{id+":enabled","true"},{id+":headers","{\"X-Test\":\"value\"}"}},null,default);
        await p.SaveTextSettingAsync(id+":headers","",default);Assert.Contains("value",Assert.Single(f.Secrets.Values).Value);
        await p.DeactivateAsync();await p.ActivateAsync(f.Host);Assert.Equal(id,p.ConnectionIdentity);Assert.Equal("true",p.TextSettings.Single(x=>x.Id==id+":enabled").Value);
        await p.SaveTextSettingAsync(id+":headers","{}",default);Assert.Equal("{}",Assert.Single(f.Secrets.Values).Value);
    }
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request);
    }
}
