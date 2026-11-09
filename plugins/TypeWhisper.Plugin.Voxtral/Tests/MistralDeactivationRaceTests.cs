using TypeWhisper.Plugin.Shared;
using TypeWhisper.Plugin.Voxtral;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public sealed partial class ProviderTests
{
    // Deactivate resets the shared connection without its gate. A background catalog save that is copying the
    // configuration at that moment must still write the key reference it checked. The pausing dictionary holds the
    // copy at that point.
    [Fact]
    public async Task DeactivationDuringTheBackgroundCatalogSaveKeepsTheKeyReference()
    {
        var values = new PausingValues { ["model"] = "voxtral-mini-latest", ["temperatureMode"] = "custom" };
        var initial = new ProviderConnection.Configuration("api-key-fixture", values);
        var host = new RaceHost(initial);
        host.Secrets["api-key-fixture"] = "fixture-key";
        using var http = new HttpClient(new Handler((_, _) => Json(LiveCatalog)));
        using var plugin = new VoxtralPlugin(http, refreshModelsAutomatically: true, clock: new TestClock(RefreshNow));

        values.Armed = true;
        await plugin.ActivateAsync(host);
        Assert.True(await Task.Run(() => values.Entered.Wait(TimeSpan.FromSeconds(10))));
        await plugin.DeactivateAsync();
        values.Release.Set();
        await plugin.ModelCatalogRefreshTask;

        var persisted = host.Saved ?? initial;
        Assert.Equal("api-key-fixture", persisted.SecretName);
        Assert.Equal("voxtral-mini-latest", persisted.Values["model"]);
        using var restarted = OfflinePlugin();
        await restarted.ActivateAsync(host);
        Assert.True(restarted.IsConfigured);
    }

    private sealed class PausingValues : Dictionary<string, string>, IEnumerable<KeyValuePair<string, string>>
    {
        public volatile bool Armed;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        IEnumerator<KeyValuePair<string, string>> IEnumerable<KeyValuePair<string, string>>.GetEnumerator()
        {
            if (Armed)
            {
                Armed = false;
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
            return GetEnumerator();
        }
    }

    private sealed class RaceHost(ProviderConnection.Configuration initial) : IPluginHostServices
    {
        private ProviderConnection.Configuration? _saved;
        public ProviderConnection.Configuration? Saved => Volatile.Read(ref _saved);
        public Dictionary<string, string> Secrets { get; } = [];
        public Task StoreSecretAsync(string key, string value) { Secrets[key] = value; return Task.CompletedTask; }
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult(Secrets.GetValueOrDefault(key));
        public Task DeleteSecretAsync(string key) { Secrets.Remove(key); return Task.CompletedTask; }
        public T? GetSetting<T>(string key) => key == "configuration" && (object)(Saved ?? initial) is T value ? value : default;
        public void SetSetting<T>(string key, T value)
        {
            if (key == "configuration" && value is ProviderConnection.Configuration configuration) Volatile.Write(ref _saved, configuration);
        }
        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string text) { }
        public void NotifyCapabilitiesChanged() { }
        public IPluginLocalization Localization { get; } = new LocalizationStub();
        public IPluginEventBus EventBus => throw new NotSupportedException();
    }
}
