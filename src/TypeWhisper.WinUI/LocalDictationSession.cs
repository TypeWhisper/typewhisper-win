using System.Runtime.InteropServices;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Presentation;
using RecordingMode = TypeWhisper.Presentation.RecordingMode;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginHost;

namespace TypeWhisper.WinUI;

// Initial local vertical slice: reuses the existing capture implementation and
// Parakeet configuration. Does not instantiate the WPF application or plugin UI.
internal sealed class LocalDictationSession : IDisposable
{
    private readonly AudioRecordingService _audio = new();
    private readonly SoundService _sounds = new();
    private readonly AudioDuckingService _ducking = new();
    private readonly RecordingAudioEffects _effects;
    private readonly LocalLivePreview _livePreview = new();
    internal WinUIPluginPackages Packages { get; } = new();
    internal LocalCtcVocabulary CtcVocabulary { get; }
    private bool _ctcAtStart;
    private Task<DictationDictionarySnapshot>? _dictionarySnapshot;
    private Task<DictationSnippetSnapshot>? _snippetSnapshot;
    private bool _boostVocabulary;
    internal DictationOutputPreferencesStore OutputPreferences { get; } = new(WinUIProfile.DataPath("dictation-output.json"));
    internal RecordingModePreferencesStore RecordingModePreferences { get; } = new(WinUIProfile.DataPath("recording-mode.json"));
    internal string? SelectRecordingMode(RecordingMode mode)
    {
        if (!CanChangeProvider || !_gate.Wait(0)) return "Finish dictation before changing recording mode.";
        try { return RecordingModePreferences.Save(mode); }
        finally { _gate.Release(); Changed?.Invoke(); }
    }
    internal DictationTextPreferencesStore TextPreferences { get; } = new(WinUIProfile.DataPath("dictation-text.json"));
    internal TranscriptionTaskPreferencesStore TranscriptionTaskPreferences { get; } = new(WinUIProfile.DataPath("transcription-task.json"));
    internal bool SupportsTranslation => UsesRegistryProvider ? ActiveRegistryProvider?.SupportsTranslation == true : Models.SupportsTranslation;
    private TranscriptionTask _taskAtStart;
    private string _engineAtStart = "";
    private string? _modelAtStart;
    internal string? SelectTranscriptionTask(TranscriptionTask task)
    {
        if (!CanChangeProvider || !_gate.Wait(0)) return "Finish dictation before changing the task.";
        try
        {
            if (task == TranscriptionTask.Translate && !SupportsTranslation)
                return "This model does not support translation to English. Choose a compatible model first.";
            return TranscriptionTaskPreferences.Save(task);
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }
    private DictationTextPreferences _textAtStart = new();
    private DictationOutputPreferences _outputAtStart = new();
    internal event Action<DictationOutputResult>? ReviewRequested;
    internal bool LivePreviewEnabled { get; set; } = true;
    internal string LivePreviewText { get; private set; } = "";
    internal event Action? LivePreviewChanged;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _silenceTimer;
    private volatile SilenceAutoStop? _silence;
    private readonly System.Diagnostics.Stopwatch _silenceClock = new();
    internal DictationAudioPreferences AudioPreferences { get; private set; } = new();
    internal string? AudioPreferencesError { get; private set; }
    private static readonly string AudioPreferencesPath = WinUIProfile.DataPath("audio.json");

    internal string? SaveAudioPreferences(DictationAudioPreferences preferences)
    {
        try
        {
            preferences = preferences.Validated();
            Directory.CreateDirectory(Path.GetDirectoryName(AudioPreferencesPath)!);
            File.WriteAllText(AudioPreferencesPath + ".tmp", System.Text.Json.JsonSerializer.Serialize(preferences));
            File.Move(AudioPreferencesPath + ".tmp", AudioPreferencesPath, true);
            AudioPreferences = preferences;
            AudioPreferencesError = null;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return AudioPreferencesError = "Could not save audio preferences: " + ex.Message; }
    }
    internal HistoryRetentionController HistoryRetention { get; }
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _retentionTimer;
    private bool _applyingRetention;
    private async Task ApplyHistoryRetentionAsync()
    {
        if (_disposed || _applyingRetention) return;
        _applyingRetention = true;
        try
        {
            var error = await Task.Run(HistoryRetention.ApplyAsync);
            if (!_disposed && error is not null && _phase == DictationPhase.Idle) SetStatus(error, DictationPhase.Idle);
        }
        finally { _applyingRetention = false; }
    }
    private readonly IHistoryService _history;
    internal HistoryReader HistoryReader => new(_history);
    private readonly ClipboardTextInserter _inserter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LocalTranscriptionPlugin _transcriptionPlugin;
    internal LocalTranscriptionPlugin Models => _transcriptionPlugin;
    internal CloudTranscriptionPlugin Groq { get; }
    internal PortablePluginRuntimeRegistry PluginRuntime { get; }
    internal IReadOnlyList<PortableLlmProvider> LlmProviders => PluginRuntime.LlmProviders;
    internal Task<string> ProcessLlmAsync(string selectionId, string systemPrompt, string text, string model, CancellationToken ct) =>
        PluginRuntime.UseLlmAsync(selectionId, (provider, token) => provider.ProcessAsync(systemPrompt, text, model, token), ct);
    private string _providerId = "local";
    internal bool UsesRegistryProvider => _providerId != "local";
    private static string RegistrySelectionId(string id) => id == "groq" ? CloudTranscriptionPlugin.PluginId : id;
    private PortableTranscriptionProvider? ActiveRegistryProvider => PluginRuntime.TranscriptionProviders.FirstOrDefault(provider => provider.SelectionId == RegistrySelectionId(_providerId));
    private readonly Dictionary<string, bool> _packageLocality = new(StringComparer.Ordinal);
    private bool PackageIsLocal(string id)
    {
        if (!_packageLocality.TryGetValue(id, out var local))
            _packageLocality[id] = local = PortablePluginPackage.ReadManifest(Packages.Store.Resolve(id)).IsLocal;
        return local;
    }
    private readonly VocabularyHostServices _selection = new(WinUIProfile.DataPath("Dictation"));
    internal bool UsesGroq => _providerId == "groq";
    internal string ActiveModelName => UsesRegistryProvider ? ActiveRegistryProvider is { } provider
        ? provider.Name + " · " + (provider.Models.FirstOrDefault(model => model.Id == provider.SelectedModelId)?.DisplayName ?? "No model selected")
        : "Selected provider unavailable" : Models.ActiveModelName;
    internal string? ActiveModelId => UsesRegistryProvider ? ActiveRegistryProvider?.SelectedModelId : Models.ActiveModelId;
    internal string ActiveChoiceId => UsesRegistryProvider ? _providerId + ":" + ActiveModelId : Models.ActiveModelId ?? "";
    internal string ActiveProviderId => _providerId;
    internal string ActiveEngineId => UsesRegistryProvider ? ActiveRegistryProvider?.EngineId ?? RegistrySelectionId(_providerId) : "sherpa-onnx";
    internal IReadOnlyList<DictationProviderOption> DictationProviders => new DictationProviderOption[]
    {
        new("local", LocalTranscriptionPlugin.PluginId, "NVIDIA Parakeet", Models.Enabled, true, false,
            Models.ActiveModelId, Models.Models.Select(model => new DictationModelOption(model.Model.Id, model.Model.DisplayName, model.Downloaded)).ToArray()),
        new("groq", CloudTranscriptionPlugin.PluginId, "Groq", Groq.Enabled, Groq.Ready, true,
            Groq.ModelId, Groq.Models.Select(model => new DictationModelOption(model.Id, model.DisplayName, Groq.Ready)).ToArray())
    }.Where(provider => Packages.Store.IsInstalled(provider.PluginId)).Concat(PluginRuntime.TranscriptionProviders
        .Where(provider => provider.PluginId != CloudTranscriptionPlugin.PluginId || provider.SelectionId != CloudTranscriptionPlugin.PluginId)
        .Select(provider => new DictationProviderOption(provider.SelectionId, provider.PluginId, provider.Name, true, provider.Ready, !PackageIsLocal(provider.PluginId),
            provider.SelectedModelId, provider.Models.Select(model => new DictationModelOption(model.Id, model.DisplayName, provider.Ready)).ToArray()))).ToArray();
    internal Task<string?> SelectProviderModelAsync(string providerId, string modelId) => providerId switch
    {
        "local" => SelectModelAsync(modelId),
        "groq" => SelectGroqModelAsync(modelId),
        _ => SelectRegistryModelAsync(providerId, modelId)
    };
    internal IReadOnlyList<string> SupportedLanguages => UsesRegistryProvider ? ActiveRegistryProvider?.SupportedLanguages ?? [] : Models.SupportedLanguages;
    internal string Language => UsesRegistryProvider ? ActiveRegistryProvider is { } provider
        ? WinUIPluginPackages.CreateServices(provider.PluginId).GetSetting<string>("Language") ?? "auto" : "auto" : Models.Language;
    internal bool CanChangeProvider => !_disposed && !IsRecording && _phase is not (DictationPhase.Processing or DictationPhase.Configuring) && !Groq.Busy && !PluginRuntime.IsBusy;
    internal bool CanSelectModel => !_disposed && !IsRecording && _phase is not (DictationPhase.Processing or DictationPhase.Configuring) && !Models.Busy && Models.Enabled && !Groq.Busy;
    private IntPtr _target;
    private DateTime _started;
    private bool _disposed;
    private DictationPhase _phase;
    private TimeSpan _lastDuration;
    private string _targetApp = "";
    private uint _targetProcessId;
    internal DictationOverlayState OverlayState => new(_phase,
        _audio.IsRecording ? _audio.RecordingDuration : _lastDuration, Status, _targetApp, _targetProcessId, RecordingModePreferences.Current);
    internal string Status { get; private set; } = "Loading local transcription plugin…";
    internal string Shortcut { get; set; } = "Ctrl+Shift+F9";
    internal bool IsRecording => _audio.IsRecording;
    internal bool IsReady => UsesRegistryProvider ? ActiveRegistryProvider?.Ready == true : _transcriptionPlugin.Ready;
    internal string? LocalPluginError { get; private set; }

    internal async Task<string?> SetLocalPluginEnabledAsync(bool enabled)
    {
        if (enabled && !Packages.Store.IsInstalled(LocalTranscriptionPlugin.PluginId)) return "Install NVIDIA Parakeet under Integrations first.";
        if (_disposed || !await _gate.WaitAsync(0)) return "Wait until dictation is ready before changing the plugin.";
        try
        {
            if (_audio.IsRecording) return "Finish recording before changing the plugin.";
            SetStatus(enabled ? "Loading local transcription plugin…" : "Unloading local transcription plugin…", DictationPhase.Configuring);
            await _livePreview.StopAsync();
            await _transcriptionPlugin.SetEnabledAsync(enabled);
            var vocabularyError = await CtcVocabulary.SetEnabledAsync(Models.Enabled);
            LocalPluginError = Models.Error ?? vocabularyError;
            SetStatus(enabled ? IsReady ? $"{ActiveModelName} ready" : Models.Error ?? "Download a model in plugin settings, then select it in Dictation." : "Local transcription plugin disabled", DictationPhase.Idle);
            return LocalPluginError;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LocalPluginError = "Could not update local transcription plugin: " + ex.Message;
            SetStatus(LocalPluginError, DictationPhase.Idle);
            return LocalPluginError;
        }
        finally { _gate.Release(); }
    }
    internal async Task<string?> SelectModelAsync(string modelId)
    {
        if (!CanSelectModel || !await _gate.WaitAsync(0)) return "Finish recording or the current model operation before changing models.";
        try
        {
            SetStatus("Loading model…", DictationPhase.Configuring);
            await _livePreview.StopAsync();
            await Models.ActivateAsync(modelId);
            _selection.SetSetting("Provider", "local");
            _providerId = "local";
            LocalPluginError = null;
            SetStatus($"{ActiveModelName} ready", DictationPhase.Idle);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LocalPluginError = "Could not select model: " + ex.Message;
            SetStatus(LocalPluginError, DictationPhase.Idle);
            return LocalPluginError;
        }
        finally { _gate.Release(); }
    }
    internal async Task<string?> SetGroqEnabledAsync(bool enabled) => await ChangeGroqAsync(() => Groq.SetEnabledAsync(enabled));
    internal Task<string?> SaveGroqKeyAsync(string key) => ChangeGroqAsync(() => Groq.SaveKeyAsync(key));
    internal Task<string?> ValidateGroqAsync() => ChangeGroqAsync(Groq.ValidateAsync);
    internal Task<string?> SelectGroqModelAsync(string modelId) => ChangeGroqAsync(async () =>
    {
        if (!Groq.Ready) throw new InvalidOperationException("Save a Groq API key before choosing this model.");
        await Groq.SelectModelAsync(modelId);
        _selection.SetSetting("Provider", "groq");
        _providerId = "groq";
    });
    internal Task<string?> SelectDictationModelAsync(string id)
    {
        foreach (var provider in DictationProviders)
            foreach (var model in provider.Models)
                if (id == provider.Id + ":" + model.Id) return SelectProviderModelAsync(provider.Id, model.Id);
        return SelectModelAsync(id);
    }
    internal async Task<string?> UninstallPluginAsync(string id, IProgress<PluginInstallationProgress>? progress = null)
    {
        if (!CanChangeProvider || Models.Busy || CtcVocabulary.Busy || !await _gate.WaitAsync(0))
            return "Finish dictation and model operations before uninstalling a plugin.";
        try
        {
            SetStatus("Uninstalling plugin…", DictationPhase.Configuring);
            progress?.Report(new("Finishing running plugin operations…"));
            await _livePreview.StopAsync();
            progress?.Report(new("Unloading plugin resources…"));
            if (id == LocalTranscriptionPlugin.PluginId)
            {
                await Models.SetEnabledAsync(false);
                var error = await CtcVocabulary.SetEnabledAsync(false);
                if (error is not null) return error;
                LocalPluginError = null;
            }
            else if (id == CloudTranscriptionPlugin.PluginId) await Groq.SetEnabledAsync(false);
            else if (await PluginRuntime.SetEnabledAsync(id, false) is { } runtimeError) return runtimeError;
            await Packages.Store.UninstallAsync(id, progress);
            // Keep the selected provider explicit; removing it never switches audio to a cloud service.
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "Could not uninstall plugin: " + ex.Message; }
        finally
        {
            SetStatus(IsReady ? $"{ActiveModelName} ready" : "Choose an installed provider in Dictation.", DictationPhase.Idle);
            _gate.Release(); Changed?.Invoke();
        }
    }
    private async Task<string?> ChangeGroqAsync(Func<Task> action)
    {
        if (!Packages.Store.IsInstalled(CloudTranscriptionPlugin.PluginId)) return "Install Groq under Integrations first.";
        if (!CanChangeProvider || !await _gate.WaitAsync(0)) return "Finish dictation before changing Groq settings.";
        try
        {
            var previousStatus = Status;
            SetStatus("Updating Groq settings…", DictationPhase.Configuring);
            await _livePreview.StopAsync();
            await action();
            SetStatus(IsReady ? $"{ActiveModelName} ready" : UsesGroq ? "Configure or enable Groq in Plugins, or select a local model." : previousStatus, DictationPhase.Idle);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { SetStatus(ex.Message, DictationPhase.Idle); return ex.Message; }
        finally { _gate.Release(); Changed?.Invoke(); }
    }
    internal string? SelectLanguage(string language)
    {
        if (!(UsesRegistryProvider ? CanChangeProvider && IsReady : CanSelectModel) || !_gate.Wait(0)) return "Finish dictation before changing the language.";
        try
        {
            if (UsesRegistryProvider)
            {
                if (language != "auto" && !SupportedLanguages.Contains(language)) return "This provider does not support that language.";
                if (ActiveRegistryProvider is not { } provider) return "The selected provider is unavailable.";
                WinUIPluginPackages.CreateServices(provider.PluginId).SetSetting("Language", language);
            }
            else Models.SelectLanguage(language);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "Could not save language: " + ex.Message; }
        finally { _gate.Release(); }
    }
    internal float CurrentLevel => _audio.CurrentRmsLevel;
    internal ManagedPluginBinding? GetPluginBinding(string id) => id switch
    {
        LocalTranscriptionPlugin.PluginId => new(id, () => Models.Enabled, () => Models.Busy || CtcVocabulary.Busy,
            () => LocalPluginError ?? CtcVocabulary.Error, SetLocalPluginEnabledAsync),
        LocalCtcVocabulary.PluginId => null,
        CloudTranscriptionPlugin.PluginId => new(id, () => Groq.Enabled, () => Groq.Busy || PluginRuntime.IsBusy,
            () => Groq.Error ?? PluginRuntime.Snapshot().FirstOrDefault(state => state.PluginId == id)?.Error, SetGroqEnabledAsync),
        _ => new(id, () => PluginRuntime.Snapshot().Any(state => state.PluginId == id && state.Enabled), () => PluginRuntime.IsBusy,
            () => PluginRuntime.Snapshot().FirstOrDefault(state => state.PluginId == id)?.Error, enabled => SetRegistryPluginEnabledAsync(id, enabled))
    };
    internal Task<string?> SetRegistryPluginEnabledAsync(string id, bool enabled) => ChangeRegistryPluginAsync(id, async () =>
    {
        if (await PluginRuntime.SetEnabledAsync(id, enabled) is { } error) throw new InvalidOperationException(error);
    });
    internal Task<string?> SaveRegistryKeyAsync(string id, string key) => ChangeRegistryPluginAsync(id, async () =>
    {
        if (!PluginRuntime.Snapshot().Any(state => state.PluginId == id && state.Enabled))
            if (await PluginRuntime.SetEnabledAsync(id, true) is { } error) throw new InvalidOperationException(error);
        await PluginRuntime.UseConfigurationAsync(id, async (plugin, _) =>
        {
            if (plugin is not IApiKeyPlugin settings) throw new NotSupportedException("This plugin does not expose API-key settings.");
            await settings.SetApiKeyAsync(key); return true;
        });
        await PluginRuntime.RefreshCapabilitiesAsync();
    });
    internal Task<string?> ValidateRegistryKeyAsync(string id) => ChangeRegistryPluginAsync(id, async () =>
    {
        await PluginRuntime.UseConfigurationAsync(id, async (plugin, ct) =>
        {
            if (plugin is not IApiKeyPlugin settings) throw new NotSupportedException("This plugin does not expose API-key settings.");
            await settings.ValidateConfigurationAsync(ct); return true;
        });
        await PluginRuntime.RefreshCapabilitiesAsync();
    });
    private async Task<string?> SelectRegistryModelAsync(string providerId, string modelId)
    {
        var provider = PluginRuntime.TranscriptionProviders.FirstOrDefault(item => item.SelectionId == RegistrySelectionId(providerId));
        if (provider is null) return "Enable this transcription provider in Integrations first.";
        return await ChangeRegistryPluginAsync(provider.PluginId, async () =>
        {
            await PluginRuntime.UseTranscriptionAsync(provider.SelectionId, async (engine, ct) =>
            {
                if (!engine.TranscriptionModels.Any(model => model.Id == modelId)) throw new ArgumentException("This model is no longer available.");
                if (engine.SupportsModelDownload)
                {
                    if (!engine.IsModelDownloaded(modelId)) throw new InvalidOperationException("Download the model before selecting it.");
                    await engine.LoadModelAsync(modelId, ct);
                }
                engine.SelectModel(modelId); return true;
            });
            await PluginRuntime.RefreshCapabilitiesAsync();
            _selection.SetSetting("Provider", providerId);
            _providerId = providerId;
        });
    }
    private async Task<string?> ChangeRegistryPluginAsync(string id, Func<Task> action)
    {
        if (!Packages.Store.IsInstalled(id)) return "Install this plugin in Integrations first.";
        if (!CanChangeProvider || Models.Busy || !await _gate.WaitAsync(0)) return "Finish dictation and model operations before changing plugins.";
        try
        {
            SetStatus("Updating plugin…", DictationPhase.Configuring);
            await _livePreview.StopAsync();
            await action();
            SetStatus(IsReady ? $"{ActiveModelName} ready" : "Choose and configure a transcription provider in Dictation.", DictationPhase.Idle);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var message = "Plugin operation could not be completed (" + ex.GetType().Name + "). Check the plugin's configuration and try again.";
            SetStatus(message, DictationPhase.Idle); return message;
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }
    private async Task<(string Text, VocabularyTokenTiming[] Timings, string? DetectedLanguage)> DecodeRegistryAsync(float[] samples)
    {
        var language = Language == "auto" ? null : Language;
        var translate = _taskAtStart == TranscriptionTask.Translate;
        var result = await PluginRuntime.UseTranscriptionAsync(RegistrySelectionId(_providerId), (engine, ct) =>
        {
            if (translate && !engine.SupportsTranslation) throw new NotSupportedException("This provider cannot translate audio to English.");
            return engine is IPcmTranscriptionEnginePlugin pcm ? pcm.TranscribePcmAsync(samples, language, translate, ct)
                : engine.TranscribeAsync(CloudTranscriptionPlugin.EncodeWav(samples, int.MaxValue), language, translate, null, ct);
        });
        return (result.Text, result.TokenTimings.ToArray(), result.DetectedLanguage);
    }
    internal event Action? Changed;
    private List<MicrophonePriorityItem> _microphones = [];
    private static readonly string MicrophonePath = WinUIProfile.DataPath("microphone.json");
    internal IReadOnlyList<MicrophonePriorityItem> MicrophonePriority => _microphones.AsReadOnly();
    internal string SelectedMicrophoneId => _microphones.FirstOrDefault()?.Id ?? "default";
    internal string SelectedMicrophoneName => _microphones.FirstOrDefault()?.Name ?? "System default";
    internal IReadOnlyList<AudioInputDeviceInfo> GetMicrophones() => _audio.GetAvailableInputDeviceInfos();

    internal string? SelectMicrophone(string id)
    {
        var device = GetMicrophones().FirstOrDefault(item => item.Id == id);
        if (id != "default" && device is null) return "This microphone is no longer available. Reopen Audio to refresh.";
        return SetMicrophonePriority(device is null ? [] :
            new[] { new MicrophonePriorityItem(device.Id, device.Name) }.Concat(_microphones.Where(item => item.Id != id)).ToArray());
    }

    internal string? SetMicrophonePriority(IReadOnlyList<MicrophonePriorityItem> devices)
    {
        if (!_gate.Wait(0)) return "Please wait until dictation is ready.";
        try
        {
            if (_audio.IsRecording) return "Finish the current recording before changing microphones.";
            var selected = devices.DistinctBy(item => item.Id).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(MicrophonePath)!);
            var pending = MicrophonePath + ".tmp";
            File.WriteAllText(pending, System.Text.Json.JsonSerializer.Serialize(selected));
            File.Move(pending, MicrophonePath, true);
            _microphones = selected;
            _audio.SetMicrophonePriorityList(selected);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "Could not apply microphone: " + ex.Message; }
        finally { _gate.Release(); }
    }

    internal LocalDictationSession(IHistoryService history, IntPtr owner)
    {
        _transcriptionPlugin = new(packageDirectory: () => Packages.Store.Resolve(LocalTranscriptionPlugin.PluginId));
        CtcVocabulary = new(packageDirectory: () => Path.Combine(Packages.Store.Resolve(LocalTranscriptionPlugin.PluginId), "Dependencies", LocalCtcVocabulary.PluginId));
        PluginRuntime = new(Packages.Store, LocalCtcVocabulary.HostVersion, WinUIPluginPackages.CreateServices,
            id => id is not (LocalTranscriptionPlugin.PluginId or LocalCtcVocabulary.PluginId));
        Groq = new(WinUIPluginPackages.CreateServices(CloudTranscriptionPlugin.PluginId), PluginRuntime);
        PluginRuntime.Changed += () => Changed?.Invoke();
        _history = history;
        HistoryRetention = new(history, new HistoryRetentionPreferencesStore(WinUIProfile.DataPath("history-retention.json")));
        _inserter = new(owner);
        _effects = new(_ducking, new MediaPauseService());
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _retentionTimer = dispatcher.CreateTimer();
        _retentionTimer.Interval = TimeSpan.FromMinutes(1);
        _retentionTimer.Tick += async (_, _) => await ApplyHistoryRetentionAsync();
        _silenceTimer = dispatcher.CreateTimer();
        _silenceTimer.Interval = TimeSpan.FromMilliseconds(100);
        _silenceTimer.Tick += async (_, _) =>
        {
            if (_disposed || !_audio.IsRecording) { StopSilenceMonitoring(); return; }
            if (_silence?.ShouldStop(_silenceClock.Elapsed, ModifiersHeld()) == true)
                await StopAsync();
        };
        _audio.AudioLevelChanged += (_, level) => _silence?.Observe(_silenceClock.Elapsed, level.RmsLevel);
        _audio.DeviceLost += (_, _) => dispatcher.TryEnqueue(() =>
        {
            if (_disposed || _audio.IsRecording) return;
            StopSilenceMonitoring();
            _livePreview.Cancel();
            _effects.End();
            if (_phase == DictationPhase.Recording) SetStatus("Microphone disconnected · recording stopped", DictationPhase.Error);
        });
        try
        {
            if (File.Exists(AudioPreferencesPath))
                AudioPreferences = (System.Text.Json.JsonSerializer.Deserialize<DictationAudioPreferences>(File.ReadAllText(AudioPreferencesPath)) ?? new()).Validated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { AudioPreferencesError = "Audio preferences could not be loaded. Defaults are in use: " + ex.Message; }
    }

    internal async Task InitializeAsync()
    {
        await ApplyHistoryRetentionAsync();
        _retentionTimer.Start();
        await _gate.WaitAsync();
        try
        {
            await Packages.InitializeAsync();
            if (File.Exists(MicrophonePath))
            {
                var json = File.ReadAllText(MicrophonePath);
                using var document = System.Text.Json.JsonDocument.Parse(json);
                // Preserve the previous single-device preference on upgrade.
                _microphones = document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? System.Text.Json.JsonSerializer.Deserialize<List<MicrophonePriorityItem>>(json) ?? []
                    : System.Text.Json.JsonSerializer.Deserialize<MicrophonePriorityItem>(json) is { } previous ? [previous] : [];
                _audio.SetMicrophonePriorityList(_microphones);
            }
            _providerId = _selection.GetSetting<string>("Provider") ?? "local";
            await PluginRuntime.InitializeAsync();
            try { if (Packages.Store.IsInstalled(CloudTranscriptionPlugin.PluginId)) await Groq.InitializeAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine("Groq initialization failed: " + CloudTranscriptionPlugin.DescribeError(ex)); }
            try { if (Packages.Store.IsInstalled(LocalTranscriptionPlugin.PluginId)) await _transcriptionPlugin.InitializeAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { LocalPluginError = ex.Message; }
            // Prepare the device without starting capture, so key-down need not initialize it.
            var prepared = _audio.WarmUp();
            await CtcVocabulary.SetEnabledAsync(Models.Enabled);
            LocalPluginError ??= Models.Error;
            SetStatus(!IsReady ? UsesRegistryProvider ? "The selected provider is unavailable or not configured. Open Integrations, then select a ready model in Dictation." : !Models.Enabled ? "Local transcription plugin disabled" : Models.Error ?? "Download a model in plugin settings, then select it in Dictation." : prepared ? $"{ActiveModelName} ready · {Shortcut} to dictate" : $"{ActiveModelName} ready · microphone preparation failed; check the device");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LocalPluginError = ex.Message;
            SetStatus("Local transcription unavailable: " + ex.Message);
        }
        finally { _gate.Release(); }
    }

    internal Task ToggleAsync() => SetRecordingAsync(null);
    internal Task StartAsync() => SetRecordingAsync(true);
    internal Task StopAsync() => SetRecordingAsync(false);
    internal async Task CancelAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0)) return;
        try
        {
            StopSilenceMonitoring();
            _livePreview.Cancel();
            if (_audio.IsRecording) await _audio.StopRecordingAsync();
            _effects.End();
            await _livePreview.StopAsync();
            SetStatus($"Shortcut cancelled · {ActiveModelName} ready");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SetStatus("Could not cancel recording: " + ex.Message); }
        finally { _effects.End(); _gate.Release(); }
    }

    private async Task SetRecordingAsync(bool? recording)
    {
        if (_disposed || !await _gate.WaitAsync(0)) return;
        try
        {
            if (recording.HasValue && recording.Value == _audio.IsRecording) return;
            if (!IsReady) { SetStatus("No model is ready. Download a model or configure a cloud provider in plugin settings, then select it in Dictation."); return; }
            if (!_audio.IsRecording)
            {
                if (TranscriptionTaskPreferences.Current == TranscriptionTask.Translate && !SupportsTranslation)
                {
                    SetStatus("This model cannot translate to English. Choose Transcribe or a translation-capable model in Dictation.");
                    return;
                }
                _taskAtStart = TranscriptionTaskPreferences.Current;
                _engineAtStart = ActiveEngineId;
                _modelAtStart = ActiveModelId;
                _target = GetForegroundWindow();
                GetWindowThreadProcessId(_target, out var processId);
                if (_target == IntPtr.Zero || processId == Environment.ProcessId)
                {
                    SetStatus($"Focus a text field in another app, then press {Shortcut}.");
                    return;
                }
                var preferences = AudioPreferences;
                await _livePreview.StopAsync();
                if (_disposed) return;
                _audio.WhisperModeEnabled = preferences.WhisperModeEnabled;
                _outputAtStart = OutputPreferences.Current;
                _textAtStart = TextPreferences.Current;
                _audio.StartRecording(enableRecovery: false);
                if (!_audio.IsRecording) { SetStatus("Microphone could not start. Check the input device and microphone access."); return; }
                _dictionarySnapshot = Task.Run(() => DictationDictionarySnapshot.Load(DictationDictionarySnapshot.StoragePath));
                _snippetSnapshot = Task.Run(() => DictationSnippetSnapshot.Load(DictationSnippetSnapshot.StoragePath));
                _boostVocabulary = DictionaryBoostingPreferences.Load();
                _ctcAtStart = _taskAtStart == TranscriptionTask.Transcribe && !UsesRegistryProvider && Models.ActiveModelId == "parakeet-tdt-0.6b" && CtcVocabulary.Enabled;
                LivePreviewText = "";
                if (LivePreviewEnabled && !UsesRegistryProvider && _taskAtStart == TranscriptionTask.Transcribe)
                    _livePreview.Start(() => _audio.HasSpeechEnergy ? _audio.GetCurrentBuffer() : null,
                        DecodeAsync,
                        text => { LivePreviewText = text; LivePreviewChanged?.Invoke(); },
                        error => { LivePreviewText = "Live preview unavailable · final transcription will continue."; LivePreviewChanged?.Invoke(); System.Diagnostics.Debug.WriteLine(error); });
                if (preferences.SilenceAutoStopEnabled)
                {
                    _silenceClock.Restart();
                    _silence = new(TimeSpan.FromSeconds(preferences.SilenceAutoStopSeconds));
                    _silenceTimer.Start();
                }
                _sounds.IsEnabled = preferences.SoundFeedbackEnabled;
                _sounds.OutputDeviceId = preferences.OutputDeviceId;
                _ducking.OutputDeviceId = preferences.OutputDeviceId;
                _effects.Begin(preferences);
                _sounds.PlayStartSound();
                _started = DateTime.UtcNow;
                _lastDuration = TimeSpan.Zero;
                _targetProcessId = processId;
                try { using var process = System.Diagnostics.Process.GetProcessById((int)processId); _targetApp = process.ProcessName; }
                catch (ArgumentException) { _targetApp = "Target app"; }
                SetStatus($"Recording · {Shortcut} to finish");
                return;
            }

            StopSilenceMonitoring();
            _livePreview.Cancel();
            _lastDuration = _audio.RecordingDuration;
            SetStatus("Finishing recording…", DictationPhase.Processing);
            var samples = await _audio.StopRecordingAsync();
            _effects.End();
            _sounds.PlayStopSound();
            // Native decoding cannot be interrupted; drain the cancelled preview
            // before the final decode uses the same recognizer.
            await _livePreview.StopAsync();
            if (samples is null || samples.Length < 1600) { SetStatus("No usable audio captured. Try again."); return; }
            SetStatus($"Transcribing with {ActiveModelName}…", DictationPhase.Processing);
            var decoded = await DecodeFinalAsync(samples);
            var rawText = decoded.Text;
            if (string.IsNullOrWhiteSpace(rawText)) { SetStatus("No speech recognized. Ready to try again."); return; }
            var dictionary = _dictionarySnapshot is null ? null : await _dictionarySnapshot;
            var refinedText = rawText;
            var recordingId = Guid.NewGuid();
            if (_ctcAtStart && dictionary is not null && CtcVocabulary.Enabled)
            {
                SetStatus("Checking vocabulary with CTC…", DictationPhase.Processing);
                var refined = await CtcVocabulary.RefineAsync(recordingId, rawText, samples, decoded.Timings, dictionary.EnabledCtcEntries);
                refinedText = refined.Text;
                if (refined.Error is not null) System.Diagnostics.Debug.WriteLine(refined.Error);
            }
            else CtcVocabulary.Trace($"{recordingId} host-skipped enabledAtStart={_ctcAtStart} enabledNow={CtcVocabulary.Enabled} dictionaryLoaded={dictionary is not null}");
            var boostVocabulary = _boostVocabulary && !_ctcAtStart;
            var snippets = _snippetSnapshot is null ? null : await _snippetSnapshot;
            var notices = new List<string>();
            if (dictionary?.Error is { } dictionaryError) notices.Add(dictionaryError);
            string[] appliedSnippetIds = [];
            var processed = await DictationTextPipeline.ProcessAsync(refinedText, _textAtStart, Language,
                detectedLanguage: DictationProvenance.ResolveLanguage(decoded.DetectedLanguage, Language),
                expandSnippets: snippets is null ? null : async (input, ct) =>
                {
                    string? clipboardText = null;
                    if (await Task.Run(() => snippets.NeedsClipboard(input), ct))
                    {
                        try
                        {
                            var clipboard = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                            clipboardText = clipboard.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)
                                ? await clipboard.GetTextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct) : "";
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        { System.Diagnostics.Debug.WriteLine("Snippet clipboard text unavailable: " + ex.Message); }
                    }
                    var expansion = await Task.Run(() => snippets.ApplyWithUsage(input, clipboardText is null ? null : () => clipboardText), ct);
                    if (expansion.Error is { } error) notices.Add(error);
                    appliedSnippetIds = expansion.AppliedIds;
                    return expansion.Text;
                },
                boostVocabulary: boostVocabulary && dictionary is not null ? dictionary.ApplyBoosting : null,
                correctDictionary: dictionary is not null ? dictionary.ApplyCorrections : null,
                task: _taskAtStart, targetProcessName: _targetApp, engineId: _engineAtStart, modelId: _modelAtStart);
            notices.AddRange(processed.Warnings);
            var text = processed.Text;
            if (_disposed) return;
            try { TypeWhisper.Core.Services.SnippetUsageRecorder.Record(DictationSnippetSnapshot.StoragePath, appliedSnippetIds); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
            { notices.Add("Snippet usage could not be saved. Your transcript is unchanged."); }
            var snippetError = notices.Count == 0 ? null : string.Join(" · ", notices);
            var record = new TranscriptionRecord
            {
                Id = recordingId.ToString(), Timestamp = _started, CreatedAt = DateTime.UtcNow,
                SourceKind = "dictation",
                RawText = rawText, FinalText = text, DurationSeconds = samples.Length / 16000.0,
                EngineUsed = _engineAtStart, ModelUsed = _modelAtStart, TranscriptionTaskUsed = _taskAtStart == TranscriptionTask.Translate ? "translate" : "transcribe",
                Language = DictationProvenance.ResolveLanguage(decoded.DetectedLanguage, Language),
                AppName = _targetApp == "Target app" ? null : _targetApp,
                AppProcessName = _targetApp == "Target app" ? null : _targetApp
            };
            var delivery = new DictationOutputDelivery(_history);
            var outcome = await delivery.DeliverAsync(record, _outputAtStart,
                () => OutputPreferences.Current, async () =>
                {
                    // Recheck after waiting: settings can change while modifiers are held.
                    for (var attempt = 0; attempt < 40 && ModifiersHeld(); attempt++) await Task.Delay(25);
                    if (_disposed || !_outputAtStart.RestrictedBy(OutputPreferences.Current).AutoPaste ||
                        ModifiersHeld() || GetForegroundWindow() != _target) return false;
                    return await _inserter.InsertAsync(text, _target);
                });
            if (_disposed) return;
            LastUnsavedText = outcome.Saved ? null : text;
            SetStatus(snippetError is null ? outcome.Message : outcome.Message + " · " + snippetError);
            if (outcome.NeedsReview) ReviewRequested?.Invoke(outcome);

        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StopSilenceMonitoring();
            _livePreview.Cancel();
            try { if (_audio.IsRecording) await _audio.StopRecordingAsync(); }
            catch (Exception stopError) when (stopError is not OutOfMemoryException)
            { System.Diagnostics.Debug.WriteLine(stopError); }
            finally { _effects.End(); await _livePreview.StopAsync(); }
            _sounds.PlayErrorSound();
            SetStatus("Dictation failed: " + ex.Message, DictationPhase.Error);
        }
        finally { if (!_audio.IsRecording) _effects.End(); _gate.Release(); }
    }

    internal string? LastUnsavedText { get; private set; }
    private async Task<string> DecodeAsync(float[] samples) => (await DecodeFinalAsync(samples, false)).Text;
    private Task<(string Text, VocabularyTokenTiming[] Timings, string? DetectedLanguage)> DecodeFinalAsync(float[] samples, bool includeTimings = true) =>
        UsesGroq ? Groq.DecodeAsync(samples, _taskAtStart == TranscriptionTask.Translate)
            : UsesRegistryProvider ? DecodeRegistryAsync(samples)
            : _transcriptionPlugin.DecodeAsync(samples, includeTimings, _taskAtStart == TranscriptionTask.Translate);
    private void StopSilenceMonitoring()
    {
        _silence = null;
        _silenceTimer.Stop();
        _silenceClock.Stop();
    }
    private void SetStatus(string status, DictationPhase? phase = null)
    {
        Status = status;
        _phase = phase ?? (_audio.IsRecording ? DictationPhase.Recording : DictationPhase.Idle);
        Changed?.Invoke();
    }

    private static bool ModifiersHeld() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    public void Dispose()
    {
        _disposed = true;
        _retentionTimer.Stop();
        _ = CtcVocabulary.DisposeAsync();
        _ = Groq.DisposeAsync();
        _ = PluginRuntime.DisposeAsync();
        _livePreview.Dispose();
        StopSilenceMonitoring();
        _effects.End();
        _audio.Dispose();
        _inserter.Dispose();
        // Decode cannot be interrupted safely; process shutdown releases it if busy.
        // The preview may still be inside native inference. Drain it before disposing.
        _ = DisposeRecognizerAsync();
    }

    private async Task DisposeRecognizerAsync()
    {
        await _livePreview.StopAsync();
        await _gate.WaitAsync();
        try { await _transcriptionPlugin.DisposeAsync(); }
        finally { _gate.Release(); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
