using System.Net;
using System.Net.Http;
using System.Text;
using PortableMigration.Tests;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Meta.Tests;

public sealed class MetaKeyChangeRaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    // A manual refresh that runs while a key change waits in its secret write still sends the old key. Its list must
    // not be saved for the new key, where it would also count as fresh and block the new account's refresh for a day.
    [Fact]
    public async Task ManualRefreshStartedDuringAKeyChangeIsDropped()
    {
        var handler = new Handler(request => request.Headers.Authorization?.Parameter == "first-key"
            ? """{"data":[{"id":"muse-spark-first"},{"id":"muse-voice-transcribe-first"}]}"""
            : """{"data":[{"id":"muse-spark-second"},{"id":"muse-voice-transcribe-second"}]}""");
        var host = new BlockingHost();
        host.Secrets["api-key"] = "first-key";
        using var sut = new MetaPlugin(new HttpClient(handler), clock: new FixedClock(Now));
        await sut.ActivateAsync(host);

        var keyChange = sut.SetApiKeyAsync("second-key");
        await host.StoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var catalog = await sut.RefreshAvailableModelsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        host.ReleaseStore.SetResult();
        await keyChange.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(catalog);
        Assert.Equal("second-key", host.Secrets["api-key"]);
        Assert.Empty(host.GetSetting<List<MetaFetchedModel>>("fetchedLlmModels") ?? []);
        Assert.Null(host.GetSetting<DateTimeOffset?>("fetchedModelsAtUtc"));

        // After a restart the second account's list loads.
        using var restarted = new MetaPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: new FixedClock(Now));
        await restarted.ActivateAsync(host);
        await restarted.ModelRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(restarted.SupportedModels, model => model.Id == "muse-spark-second");
        Assert.DoesNotContain(restarted.SupportedModels, model => model.Id == "muse-spark-first");
    }

    // The hourly recheck runs on a timer thread. While a key change waits in its secret write, the old key is still set,
    // so a recheck must not start a refresh; the key change refreshes with the new key once it is in place.
    [Fact]
    public async Task RecheckDuringAKeyChangeSendsNoRequestWithTheOldKey()
    {
        var keys = new List<string?>();
        var handler = new Handler(request =>
        {
            lock (keys) keys.Add(request.Headers.Authorization?.Parameter);
            return """{"data":[{"id":"muse-spark-second"},{"id":"muse-voice-transcribe-second"}]}""";
        });
        var host = new BlockingHost();
        host.Secrets["api-key"] = "first-key";
        host.SetSetting<DateTimeOffset?>("fetchedModelsAtUtc", Now);
        var clock = new RecheckTestClock(Now);
        using var sut = new MetaPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);

        var keyChange = sut.SetApiKeyAsync("second-key");
        await host.StoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromHours(24));
        await sut.ModelRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        lock (keys) Assert.Empty(keys);

        host.ReleaseStore.SetResult();
        await keyChange.WaitAsync(TimeSpan.FromSeconds(10));
        await sut.ModelRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));

        lock (keys) Assert.Equal(["second-key"], keys);
        Assert.Contains(sut.SupportedModels, model => model.Id == "muse-spark-second");
    }

    private sealed class Handler(Func<HttpRequestMessage, string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body(request), Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // Holds the first secret write until the test releases it.
    private sealed class BlockingHost : IPluginHostServices
    {
        private readonly Dictionary<string, object?> _settings = [];
        public Dictionary<string, string> Secrets { get; } = [];
        public TaskCompletionSource StoreStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStore { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StoreSecretAsync(string key, string value)
        {
            StoreStarted.TrySetResult();
            await ReleaseStore.Task;
            lock (Secrets) Secrets[key] = value;
        }

        public Task<string?> LoadSecretAsync(string key)
        {
            lock (Secrets) return Task.FromResult(Secrets.GetValueOrDefault(key));
        }

        public Task DeleteSecretAsync(string key)
        {
            lock (Secrets) Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key)
        {
            lock (_settings) return _settings.TryGetValue(key, out var value) && value is T typed ? typed : default;
        }

        public void SetSetting<T>(string key, T value)
        {
            lock (_settings) _settings[key] = value;
        }

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus => throw new NotSupportedException();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
        public IPluginLocalization Localization => throw new NotSupportedException();
    }
}
