using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Voxtral;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public sealed partial class ProviderTests
{
    private static readonly string Id="com.typewhisper.voxtral";
    private static async Task Configure(ITypeWhisperPlugin plugin,CancellationToken ct=default)
    {
        await ((IApiKeyPlugin)plugin).SetApiKeyAsync("fixture-key");
        var settings=(IPluginTextSettings)plugin;
        if(Id=="com.typewhisper.cloudflare-asr") await settings.SaveTextSettingAsync("accountId",new string('a',32),ct);
        if(Id=="com.typewhisper.linear") await settings.SaveTextSettingAsync("teamId","12345678-1234-1234-1234-123456789abc",ct);
    }
    private static async Task<string> Run(ITypeWhisperPlugin plugin,CancellationToken ct=default)
    {
        if(plugin is ITranscriptionEnginePlugin stt) return (await stt.TranscribeAsync(Audio(),"de",false,"TypeWhisper, Marco",ct)).Text;
        var action=await ((IActionPlugin)plugin).ExecuteAsync("Hallo Welt",new(null,null,null,"de","Hallo Welt"),ct);
        Assert.True(action.Success); Assert.Equal("https://linear.app/team/issue/TST-1",action.Url); return "Hallo Welt";
    }
    private static byte[] Audio() { var wav=new byte[48]; Encoding.ASCII.GetBytes("RIFF").CopyTo(wav,0); BitConverter.GetBytes(40).CopyTo(wav,4); Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav,8); BitConverter.GetBytes(16).CopyTo(wav,16); BitConverter.GetBytes((short)1).CopyTo(wav,20); BitConverter.GetBytes((short)1).CopyTo(wav,22); BitConverter.GetBytes(16000).CopyTo(wav,24); BitConverter.GetBytes(32000).CopyTo(wav,28); BitConverter.GetBytes((short)2).CopyTo(wav,32); BitConverter.GetBytes((short)16).CopyTo(wav,34); Encoding.ASCII.GetBytes("data").CopyTo(wav,36); BitConverter.GetBytes(4).CopyTo(wav,40); return wav; }

    [Fact]
    public async Task ConfiguresRestartsRemovesKeyAndNeverExposesSecrets()
    {
        var host=new Host(); using var plugin=new VoxtralPlugin(); await plugin.ActivateAsync(host); await Configure(plugin);
        Assert.True(plugin.IsConfigured); Assert.DoesNotContain("fixture-key",JsonSerializer.Serialize(plugin.TextSettings));
        Assert.DoesNotContain("fixture-key",JsonSerializer.Serialize(host.Settings));
        await plugin.DeactivateAsync(); Assert.False(plugin.IsConfigured); await plugin.ActivateAsync(host); Assert.True(plugin.IsConfigured);
        await plugin.SetApiKeyAsync(""); Assert.Empty(host.Secrets);
        if(Id!="com.typewhisper.qwen3-stt") Assert.False(plugin.IsConfigured);
    }
    [Fact]
    public async Task FailedSecretOrConfigurationSaveKeepsOldConnection()
    {
        var host=new Host(); using var plugin=new VoxtralPlugin(); await plugin.ActivateAsync(host); await Configure(plugin);
        var before=JsonSerializer.Serialize(host.Settings); var secrets=host.Secrets.ToArray();
        host.FailSecret=true; await Assert.ThrowsAsync<IOException>(()=>plugin.SetApiKeyAsync("replacement"));
        Assert.Equal(before,JsonSerializer.Serialize(host.Settings)); Assert.Equal(secrets,host.Secrets.ToArray());
        host.FailSetting=true; await Assert.ThrowsAsync<IOException>(()=>plugin.SetApiKeyAsync("replacement"));
        Assert.Equal(before,JsonSerializer.Serialize(host.Settings)); Assert.Equal(secrets,host.Secrets.ToArray()); Assert.True(plugin.IsConfigured);
    }
    [Fact]
    public async Task InvalidOrCanceledSettingsDoNotWrite()
    {
        var host=new Host(); using var plugin=new VoxtralPlugin(); await plugin.ActivateAsync(host);
        await Assert.ThrowsAsync<ArgumentException>(()=>plugin.SetApiKeyAsync("bad\r\nkey"));
        await Assert.ThrowsAsync<ArgumentException>(()=>plugin.SaveTextSettingAsync("unknown","value",default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>plugin.SaveTextSettingAsync("unknown","value",new(true)));
        Assert.Empty(host.Settings); Assert.Empty(host.Secrets);
    }
    [Fact]
    public async Task ProtocolRoundTripPreservesUnicodeAndUsesExpectedEndpoint()
    {
        var requests=new List<(string Path,string? Body)>();
        using var http=new HttpClient(new Handler((request,body)=>
        {
            Assert.DoesNotContain("fixture-key",request.RequestUri!.AbsoluteUri);
            requests.Add((request.RequestUri.AbsolutePath,body)); return Success(request,body);
        }));
        using var plugin=new VoxtralPlugin(http); await plugin.ActivateAsync(new Host()); await Configure(plugin);
        Assert.Equal("Hallo Welt",await Run(plugin)); Assert.NotEmpty(requests);
        CheckRequests(requests);
    }
    [Theory]
    [InlineData(401,PluginRequestFailureKind.Authentication)]
    [InlineData(403,PluginRequestFailureKind.Permission)]
    [InlineData(429,PluginRequestFailureKind.RateLimit)]
    [InlineData(503,PluginRequestFailureKind.ServerError)]
    public async Task HttpFailuresRemainTyped(int status,PluginRequestFailureKind expected)
    {
        using var http=new HttpClient(new Handler((_,_)=>new((HttpStatusCode)status){Content=new StringContent("{}"),Headers={RetryAfter=new(TimeSpan.FromSeconds(3))}}));
        using var plugin=new VoxtralPlugin(http); await plugin.ActivateAsync(new Host());await Configure(plugin);
        var error=await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));Assert.Equal(expected,error.FailureKind);Assert.Equal(status,error.HttpStatusCode);
        if(status==429) Assert.Equal(TimeSpan.FromSeconds(3),error.RetryAfter);
    }
    [Fact]
    public async Task MalformedSuccessIsRejected()
    {
        using var http=new HttpClient(new Handler((_,_)=>Json("not-json")));
        using var plugin=new VoxtralPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await Assert.ThrowsAsync<PluginRequestException>(()=>Run(plugin));
    }
    [Fact]
    public async Task CancellationDoesNotSendRequests()
    {
        var calls=0;using var http=new HttpClient(new Handler((_,_)=>{calls++;return Json("{}");}));
        using var plugin=new VoxtralPlugin(http);await plugin.ActivateAsync(new Host());await Configure(plugin);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Run(plugin,new(true)));Assert.Equal(0,calls);
    }
    [Fact]
    public async Task EmptyAudioAndUnsupportedTranslationAreRejected()
    {
        using var plugin=new VoxtralPlugin();await plugin.ActivateAsync(new Host());await Configure(plugin);
        if((ITypeWhisperPlugin)plugin is not ITranscriptionEnginePlugin engine) return;
        await Assert.ThrowsAsync<ArgumentException>(()=>engine.TranscribeAsync([],"en",false,null,default));
        if(!engine.SupportsTranslation) await Assert.ThrowsAsync<NotSupportedException>(()=>engine.TranscribeAsync(Audio(),"en",true,null,default));
        Assert.Throws<ArgumentException>(()=>engine.SelectModel("not-a-model"));
    }
    [Fact]
    public async Task PackageInstallsLoadsRestartsAndReinstallsIndependently()
    {
        var root=Path.Combine(Path.GetTempPath(),"voxtral-package-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var source=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..","bin",new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,"portable-host","Plugins",Id));
            var archive=Path.Combine(root,"package.zip");ZipFile.CreateFromDirectory(source,archive);var bytes=await File.ReadAllBytesAsync(archive);
            Assert.Equal(3,Directory.GetFiles(source).Length);
            using var http=new HttpClient(new Handler((r,_)=>new(HttpStatusCode.OK){RequestMessage=r,Content=new ByteArrayContent(bytes)}));var host=new Host();
            PortablePluginStore Store()=>new(Path.Combine(root,"store"),new(1,1,2),http,_=>host);
            var entry=new PortableCatalogEntry{Id=Id,Name="Voxtral",Version="1.2.0",MinHostVersion="1.1.2",DownloadUrl="https://fixture.invalid/plugin.zip",Sha256=Convert.ToHexString(SHA256.HashData(bytes)),Size=bytes.Length,SupportedArchitectures=[PortablePluginCatalog.Architecture]};
            var store=Store();await store.InitializeAsync();await store.InstallAsync(entry);
            await using(var runtime=new PortablePluginRuntimeRegistry(store,new(1,1,2),_=>host))
            {
                await runtime.InitializeAsync();Assert.Null(await runtime.SetEnabledAsync(Id,true));
                await runtime.UseConfigurationAsync(Id,async(p,ct)=>{await Configure(p,ct);Assert.DoesNotContain(p.GetType().Assembly.GetReferencedAssemblies(),a=>a.Name is "PresentationFramework" or "WindowsBase");return true;});
                await Assert.ThrowsAsync<InvalidOperationException>(()=>store.InstallAsync(entry));
            }
            var restart=Store();await restart.InitializeAsync();
            await using(var runtime=new PortablePluginRuntimeRegistry(restart,new(1,1,2),_=>host))
            {
                await runtime.InitializeAsync();Assert.True(await runtime.UseConfigurationAsync(Id,(p,_)=>Task.FromResult(((IApiKeyPlugin)p).IsConfigured)));
                Assert.Null(await runtime.SetEnabledAsync(Id,false));await restart.UninstallAsync(Id);
            }
            var reinstall=Store();await reinstall.InitializeAsync();await reinstall.InstallAsync(entry);
            await using var final=new PortablePluginRuntimeRegistry(reinstall,new(1,1,2),_=>host);Assert.Null(await final.SetEnabledAsync(Id,true));
            Assert.True(await final.UseConfigurationAsync(Id,(p,_)=>Task.FromResult(((IApiKeyPlugin)p).IsConfigured)));
            await Assert.ThrowsAsync<InvalidDataException>(()=>PortablePluginPackage.LoadAsync(reinstall.Resolve(Id),host,new(1,1,1)));
        }
        finally {GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();try{Directory.Delete(root,true);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){} }
    }
    private static HttpResponseMessage Json(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")};
    private sealed class Handler(Func<HttpRequestMessage,string?,HttpResponseMessage> respond):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();return respond(request,request.Content is null?null:await request.Content.ReadAsStringAsync(ct));}
    }
    private sealed class Host:IPluginHostServices
    {
        public Dictionary<string,JsonElement> Settings {get;}=[];public Dictionary<string,string> Secrets {get;}=[];
        public bool FailSecret {get;set;} public bool FailSetting {get;set;}
        public Task StoreSecretAsync(string key,string value){if(FailSecret){FailSecret=false;throw new IOException("secret store failed");}Secrets[key]=value;return Task.CompletedTask;}
        public Task<string?> LoadSecretAsync(string key)=>Task.FromResult(Secrets.GetValueOrDefault(key));
        public Task DeleteSecretAsync(string key){Secrets.Remove(key);return Task.CompletedTask;}
        public T? GetSetting<T>(string key)=>Settings.TryGetValue(key,out var value)?value.Deserialize<T>():default;
        public void SetSetting<T>(string key,T value){if(FailSetting){FailSetting=false;throw new IOException("settings failed");}Settings[key]=JsonSerializer.SerializeToElement(value);}
        public string PluginDataDirectory=>Path.GetTempPath();public string? ActiveAppProcessName=>null;public string? ActiveAppName=>null;
        public IReadOnlyList<string> AvailableProfileNames=>[];public void Log(PluginLogLevel level,string text){}public void NotifyCapabilitiesChanged(){}
        public IPluginLocalization Localization {get;}=new LocalizationStub();public IPluginEventBus EventBus=>throw new NotSupportedException();
    }
    private sealed class LocalizationStub:IPluginLocalization
    {public string CurrentLanguage=>"de";public IReadOnlyList<string> AvailableLanguages=>["en","de"];public string GetString(string key)=>key;public string GetString(string key,params object[] args)=>string.Format(key,args);}
}
