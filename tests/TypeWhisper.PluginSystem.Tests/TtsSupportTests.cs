using System.Reflection;
using System.Windows.Controls;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class TtsContractTests
{
    [Fact]
    public void TtsSpeakRequest_DefaultsToStatusPurpose()
    {
        var request = new TtsSpeakRequest("hello");

        Assert.Equal("hello", request.Text);
        Assert.Null(request.Language);
        Assert.Equal(TtsPurpose.Status, request.Purpose);
    }

    [Fact]
    public void PlaybackSession_Stop_CompletesOnce()
    {
        var session = new FakeTtsPlaybackSession();
        var completedCount = 0;
        session.Completed += (_, _) => completedCount++;

        session.Stop();
        session.Stop();

        Assert.False(session.IsActive);
        Assert.Equal(1, completedCount);
    }
}

public class SpeechFeedbackServiceTests : IDisposable
{
    private readonly FakeSettingsService _settings = new(AppSettings.Default);
    private readonly FakeTtsProvider _systemProvider = new("windows-sapi", "System Voice");
    private readonly PluginManager _pluginManager;
    private readonly List<IDisposable> _disposables = [];

    public SpeechFeedbackServiceTests()
    {
        _pluginManager = TestPluginManagerFactory.Create(_settings);
    }

    [Fact]
    public async Task AutomaticTranscription_UsesSelectedConfiguredPluginProvider()
    {
        var provider = new FakeTtsProvider("plugin-tts", "Plugin TTS");
        TestPluginManagerFactory.SetTtsProviders(_pluginManager, provider);
        _settings.Save(AppSettings.Default with { SpokenFeedbackProviderId = provider.ProviderId });
        using var sut = CreateService();
        sut.IsEnabled = true;

        sut.SpeakAutomaticTranscription("transcribed text", "en");
        await WaitUntilAsync(() => provider.Requests.Count == 1);

        Assert.Empty(_systemProvider.Requests);
        Assert.Equal(TtsPurpose.Transcription, provider.Requests[0].Purpose);
        Assert.Equal("transcribed text", provider.Requests[0].Text);
        Assert.Equal("en", provider.Requests[0].Language);
    }

    [Fact]
    public async Task AutomaticTranscription_FallsBackToSystemProvider_WhenSelectionMissing()
    {
        var provider = new FakeTtsProvider("plugin-tts", "Plugin TTS");
        TestPluginManagerFactory.SetTtsProviders(_pluginManager, provider);
        _settings.Save(AppSettings.Default with { SpokenFeedbackProviderId = "missing-provider" });
        using var sut = CreateService();
        sut.IsEnabled = true;

        sut.SpeakAutomaticTranscription("hello", "en");
        await WaitUntilAsync(() => _systemProvider.Requests.Count == 1);

        Assert.Empty(provider.Requests);
        Assert.Equal(TtsPurpose.Transcription, _systemProvider.Requests[0].Purpose);
    }

    [Fact]
    public async Task AutomaticTranscription_FallsBackToSystemProvider_WhenSelectedPluginUnconfigured()
    {
        var provider = new FakeTtsProvider("plugin-tts", "Plugin TTS") { IsConfigured = false };
        TestPluginManagerFactory.SetTtsProviders(_pluginManager, provider);
        _settings.Save(AppSettings.Default with { SpokenFeedbackProviderId = provider.ProviderId });
        using var sut = CreateService();
        sut.IsEnabled = true;

        sut.SpeakAutomaticTranscription("hello", "en");
        await WaitUntilAsync(() => _systemProvider.Requests.Count == 1);

        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task AutomaticTranscription_IgnoresDisabledAndEmptyText()
    {
        using var sut = CreateService();

        sut.IsEnabled = false;
        sut.SpeakAutomaticTranscription("hello");
        sut.IsEnabled = true;
        sut.SpeakAutomaticTranscription("");

        await Task.Delay(50);
        Assert.Empty(_systemProvider.Requests);
    }

    [Fact]
    public async Task Speak_ReplacesActivePlayback()
    {
        using var sut = CreateService();
        sut.IsEnabled = true;

        sut.SpeakAutomaticTranscription("first");
        await WaitUntilAsync(() => _systemProvider.Requests.Count == 1);
        var firstSession = _systemProvider.LastSession;

        sut.SpeakAutomaticTranscription("second");
        await WaitUntilAsync(() => _systemProvider.Requests.Count == 2);

        Assert.NotNull(firstSession);
        Assert.Equal(1, firstSession!.StopCount);
    }

    [Fact]
    public async Task ReadBack_TogglesActivePlaybackOff()
    {
        using var sut = CreateService();

        sut.ReadBack("read this", "en");
        await WaitUntilAsync(() => _systemProvider.Requests.Count == 1);
        var session = _systemProvider.LastSession;

        sut.ReadBack("read this", "en");
        await WaitUntilAsync(() => session?.StopCount == 1);

        Assert.Single(_systemProvider.Requests);
        Assert.False(sut.IsSpeaking);
    }

    private SpeechFeedbackService CreateService()
    {
        var service = new SpeechFeedbackService(_settings, _pluginManager, _systemProvider);
        _disposables.Add(service);
        return service;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();
        _pluginManager.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 40; i++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.True(condition());
    }
}

public class SettingsViewModelTtsTests
{
    [Fact]
    public void SelectingSystemVoice_PersistsBuiltInVoiceSetting()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var pluginManager = TestPluginManagerFactory.Create(settings);
        var system = new FakeTtsProvider("windows-sapi", "System Voice")
        {
            AvailableVoices = [new PluginVoiceInfo("system-voice", "System Voice")]
        };
        using var speech = new SpeechFeedbackService(settings, pluginManager, system);
        using var audio = new AudioRecordingService();
        var sut = CreateSettingsViewModel(settings, audio, speech);

        sut.SelectedSpokenFeedbackProviderId = "windows-sapi";
        sut.SelectedSpokenFeedbackVoiceId = "system-voice";

        Assert.Equal("windows-sapi", settings.Current.SpokenFeedbackProviderId);
        Assert.Equal("system-voice", settings.Current.SpokenFeedbackVoiceId);
    }

    [Fact]
    public void SystemDefaultVoice_UsesSelectableOptionAndPersistsNull()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var pluginManager = TestPluginManagerFactory.Create(settings);
        var system = new FakeTtsProvider("windows-sapi", "System Voice")
        {
            AvailableVoices = [new PluginVoiceInfo("system-voice", "System Voice")]
        };
        using var speech = new SpeechFeedbackService(settings, pluginManager, system);
        using var audio = new AudioRecordingService();
        var sut = CreateSettingsViewModel(settings, audio, speech);

        sut.SelectedSpokenFeedbackVoiceId = "system-voice";
        sut.SelectedSpokenFeedbackVoiceId = SpeechFeedbackService.DefaultVoiceOptionId;

        Assert.Contains(sut.SpokenFeedbackVoices, v => v.Id == SpeechFeedbackService.DefaultVoiceOptionId);
        Assert.Null(system.SelectedVoiceId);
        Assert.Null(settings.Current.SpokenFeedbackVoiceId);
    }

    [Fact]
    public void SelectingPluginVoice_DelegatesToPluginWithoutReplacingBuiltInVoiceSetting()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            SpokenFeedbackVoiceId = "system-voice"
        });
        var pluginProvider = new FakeTtsProvider("plugin-tts", "Plugin TTS")
        {
            AvailableVoices = [new PluginVoiceInfo("plugin-voice", "Plugin Voice")]
        };
        var pluginManager = TestPluginManagerFactory.Create(settings);
        TestPluginManagerFactory.SetTtsProviders(pluginManager, pluginProvider);
        var system = new FakeTtsProvider("windows-sapi", "System Voice");
        using var speech = new SpeechFeedbackService(settings, pluginManager, system);
        using var audio = new AudioRecordingService();
        var sut = CreateSettingsViewModel(settings, audio, speech);

        sut.SelectedSpokenFeedbackProviderId = pluginProvider.ProviderId;
        sut.SelectedSpokenFeedbackVoiceId = "plugin-voice";

        Assert.Equal(pluginProvider.ProviderId, settings.Current.SpokenFeedbackProviderId);
        Assert.Equal("system-voice", settings.Current.SpokenFeedbackVoiceId);
        Assert.Equal("plugin-voice", pluginProvider.SelectedVoiceId);
    }

    private static SettingsViewModel CreateSettingsViewModel(
        FakeSettingsService settings,
        AudioRecordingService audio,
        SpeechFeedbackService speech)
    {
        var api = new ApiServerController(Mock.Of<ILocalApiServer>(), settings);
        var cli = new CliInstallService();
        return new SettingsViewModel(settings, audio, api, cli, speech);
    }
}

public class PluginManagerTtsProviderTests : IDisposable
{
    private readonly FakeSettingsService _settings = new(AppSettings.Default);
    private readonly PluginManager _manager;

    public PluginManagerTtsProviderTests()
    {
        _manager = TestPluginManagerFactory.Create(_settings);
    }

    [Fact]
    public void RebuildCapabilityIndices_AddsAndRemovesTtsProvidersByActivationState()
    {
        var provider = new FakeTtsProvider("plugin-tts", "Plugin TTS");
        var manifest = new PluginManifest
        {
            Id = provider.PluginId,
            Name = provider.PluginName,
            Version = provider.PluginVersion,
            AssemblyName = "Fake.dll",
            PluginClass = provider.GetType().FullName!
        };
        var context = new PluginAssemblyLoadContext(typeof(PluginManagerTtsProviderTests).Assembly.Location);
        var loaded = new LoadedPlugin(manifest, provider, context, AppContext.BaseDirectory);

        TestPluginManagerFactory.SetPrivateField(_manager, "_allPlugins", new List<LoadedPlugin> { loaded });
        TestPluginManagerFactory.SetPrivateField(_manager, "_activatedPlugins", new HashSet<string> { manifest.Id });
        TestPluginManagerFactory.InvokeRebuildCapabilityIndices(_manager);

        Assert.Single(_manager.TtsProviders);
        Assert.Same(provider, _manager.GetTtsProvider(provider.ProviderId));

        TestPluginManagerFactory.SetPrivateField(_manager, "_activatedPlugins", new HashSet<string>());
        TestPluginManagerFactory.InvokeRebuildCapabilityIndices(_manager);

        Assert.Empty(_manager.TtsProviders);
        Assert.Null(_manager.GetTtsProvider(provider.ProviderId));
    }

    public void Dispose() => _manager.Dispose();
}

internal sealed class FakeSettingsService : ISettingsService
{
    public FakeSettingsService(AppSettings current)
    {
        Current = current;
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? SettingsChanged;

    public AppSettings Load() => Current;

    public void Save(AppSettings settings)
    {
        Current = settings;
        SettingsChanged?.Invoke(settings);
    }
}

internal sealed class FakeTtsProvider : ITtsProviderPlugin
{
    public FakeTtsProvider(string providerId, string providerDisplayName)
    {
        ProviderId = providerId;
        ProviderDisplayName = providerDisplayName;
        PluginId = $"com.test.{providerId}";
        PluginName = providerDisplayName;
    }

    public string PluginId { get; }
    public string PluginName { get; }
    public string PluginVersion => "1.0.0";
    public string ProviderId { get; }
    public string ProviderDisplayName { get; }
    public bool IsConfigured { get; set; } = true;
    public IReadOnlyList<PluginVoiceInfo> AvailableVoices { get; set; } = [];
    public string? SelectedVoiceId { get; private set; }
    public List<TtsSpeakRequest> Requests { get; } = [];
    public FakeTtsPlaybackSession? LastSession { get; private set; }

    public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
    public Task DeactivateAsync() => Task.CompletedTask;
    public UserControl? CreateSettingsView() => null;
    public void Dispose() { }

    public void SelectVoice(string? voiceId)
    {
        SelectedVoiceId = voiceId;
    }

    public Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Add(request);
        LastSession = new FakeTtsPlaybackSession();
        return Task.FromResult<ITtsPlaybackSession>(LastSession);
    }
}

internal sealed class FakeTtsPlaybackSession : ITtsPlaybackSession
{
    private int _stopped;

    public bool IsActive => Volatile.Read(ref _stopped) == 0;
    public int StopCount { get; private set; }
    public event EventHandler? Completed;

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        StopCount++;
        Completed?.Invoke(this, EventArgs.Empty);
    }
}

internal static class TestPluginManagerFactory
{
    public static PluginManager Create(ISettingsService settings)
    {
        var activeWindow = new Mock<IActiveWindowService>();
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.Profiles).Returns([]);

        return new PluginManager(
            new PluginLoader(),
            new PluginEventBus(),
            activeWindow.Object,
            profiles.Object,
            settings,
            []);
    }

    public static void SetTtsProviders(PluginManager manager, params ITtsProviderPlugin[] providers) =>
        SetPrivateField(manager, "_ttsProviders", providers.ToList());

    public static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    public static void InvokeRebuildCapabilityIndices(PluginManager manager)
    {
        var method = typeof(PluginManager).GetMethod(
            "RebuildCapabilityIndices",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(PluginManager).FullName, "RebuildCapabilityIndices");
        method.Invoke(manager, null);
    }
}
