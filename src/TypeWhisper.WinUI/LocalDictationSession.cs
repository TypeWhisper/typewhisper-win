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
internal sealed partial class LocalDictationSession : IAsyncDisposable
{
    private readonly AudioRecordingService _audio;
    private readonly SoundService _sounds = new();
    private readonly AudioDuckingService _ducking = new();
    private readonly RecordingAudioEffects _effects;
    private readonly LocalLivePreview _livePreview = new();
    private StreamingDictation? _cloudStream;
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
    internal bool SupportsLanguageHints => UsesRegistryProvider && ActiveRegistryProvider?.SupportsLanguageHints == true;
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
    internal event Action<Guid>? OutputCompleted;
    internal bool LivePreviewEnabled { get; set; } = true;
    // Availability describes the host's connected preview path, not just an SDK streaming declaration.
    internal bool SupportsLiveTranscription => (UsesRegistryProvider
        ? ActiveRegistryProvider is { SupportsStreaming: true } || (ActiveRegistryProvider is { SupportsPcm: true, SupportsLocalLivePreview: true } preview && PackageIsLocal(preview.PluginId))
        : Models.SupportsLocalLivePreview) &&
        TranscriptionTaskPreferences.Current == TranscriptionTask.Transcribe;
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
        if (_disposed) return "Audio preferences are unavailable during shutdown.";
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
    internal HistoryActions HistoryActions => new(_history);
    private readonly ClipboardTextInserter _inserter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LocalTranscriptionPlugin _transcriptionPlugin;
    internal LocalTranscriptionPlugin Models => _transcriptionPlugin;
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
            Models.ActiveModelId, Models.Models.Select(model => new DictationModelOption(model.Model.Id, model.Model.DisplayName, model.Downloaded)).ToArray())
    }.Where(provider => Packages.Store.IsInstalled(provider.PluginId)).Concat(PluginRuntime.TranscriptionProviders
        .Select(provider => new DictationProviderOption(SessionProviderId(provider.SelectionId), provider.PluginId, provider.Name, true, provider.Ready, !PackageIsLocal(provider.PluginId),
            provider.SelectedModelId, provider.ModelStates.Select(model => new DictationModelOption(model.ModelId, model.DisplayName,
                model.SupportsDownload ? model.Downloaded : provider.Ready)).ToArray()))).ToArray();
    internal Task<string?> SelectProviderModelAsync(string providerId, string modelId) => providerId switch
    {
        "local" => SelectModelAsync(modelId),
        _ => SelectRegistryModelAsync(providerId, modelId)
    };
    internal IReadOnlyList<string> SupportedLanguages => UsesRegistryProvider ? ActiveRegistryProvider?.SupportedLanguages ?? [] : Models.SupportedLanguages;
    internal string Language => UsesRegistryProvider ? ActiveRegistryProvider is { } provider
        ? WinUIPluginPackages.CreateServices(provider.PluginId).GetSetting<string>("Language") ?? "auto" : "auto" : Models.Language;
    internal bool CanChangeProvider => !_disposed && !_fileBusy && !_recorderReserved && !_workflowReserved && !IsRecording && _phase is not (DictationPhase.Processing or DictationPhase.Configuring or DictationPhase.LoadingModel) && !PluginRuntime.IsBusy;
    internal bool CanSelectModel => !_disposed && !_fileBusy && !_recorderReserved && !_workflowReserved && !IsRecording && _phase is not (DictationPhase.Processing or DictationPhase.Configuring or DictationPhase.LoadingModel) && !Models.Busy && Models.Enabled && !PluginRuntime.IsBusy;
    private IntPtr _target;
    private OriginalDictationField? _originalField;
    // Setup alone can accept dictation inside this process, while its test field has focus.
    internal Func<IntPtr, Action<string>?>? SetupTestTarget { get; set; }
    private Action<string>? _setupOutputAtStart;
    private DateTime _started;
    private bool _disposed;
    private DictationPhase _phase;
    private TimeSpan _lastDuration;
    private bool _hasConfirmedPreviewText;
    private string _targetApp = "";
    private uint _targetProcessId;
    private bool _showModelLoadingForDictation;
    internal void ShowLoadingForDictationAttempt()
    {
        if (_phase != DictationPhase.LoadingModel || _disposed) return;
        _showModelLoadingForDictation = true;
        Changed?.Invoke();
    }
    internal DictationOverlayState OverlayState => new(
        DictationOverlayState.VisiblePhase(_phase, _showModelLoadingForDictation),
        _audio.IsRecording ? _audio.RecordingDuration : _lastDuration, Status, _targetApp, _targetProcessId, RecordingModePreferences.Current);
    internal string Status { get; private set; } = "Loading local transcription plugin…";
    internal const string DefaultShortcut = "Ctrl+Shift";
    internal string Shortcut { get; set; } = DefaultShortcut;
    internal bool IsRecording => !_recorderReserved && _audio.IsRecording;
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
            if (_disposed) return "The application is shutting down.";
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
            SetStatus("Loading model… Wait until the model is ready before dictating.", DictationPhase.LoadingModel);
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
    private Task<string?> SelectRegistryModelAsync(string providerId, string modelId)
    {
        if (_disposed || !CanChangeProvider || Models.Busy)
            return Task.FromResult<string?>("Finish dictation and model operations before selecting a model.");
        var model = PluginRuntime.TranscriptionProviders
            .FirstOrDefault(provider => provider.SelectionId == RegistrySelectionId(providerId))?
            .ModelStates.FirstOrDefault(model => model.ModelId == modelId);
        return model is null ? Task.FromResult<string?>("This model is no longer available. Refresh its provider settings.")
            : UseRegistryModelAsync(model);
    }
    private async Task<string?> ChangeRegistryPluginAsync(string id, Func<Task> action, bool loadingModel = false)
    {
        if (!Packages.Store.IsInstalled(id)) return "Install this plugin in Integrations first.";
        if (!CanChangeProvider || Models.Busy || !await _gate.WaitAsync(0)) return "Finish dictation and model operations before changing plugins.";
        try
        {
            SetStatus(loadingModel ? "Loading model…" : "Updating plugin…", loadingModel ? DictationPhase.LoadingModel : DictationPhase.Configuring);
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
    private async Task<(string Text, VocabularyTokenTiming[] Timings, string? DetectedLanguage, float? NoSpeechProbability)> DecodeRegistryAsync(float[] samples)
    {
        var dictionary = _dictionarySnapshot is null ? null : await _dictionarySnapshot;
        var language = _languageAtStart == "auto" ? null : _languageAtStart;
        var translate = _taskAtStart == TranscriptionTask.Translate;
        var result = await PluginRuntime.UseTranscriptionAsync(RegistrySelectionId(_providerId), (engine, ct) =>
        {
            return LanguageHintTranscription.DecodeAsync(engine, samples,
                () => PcmWaveEncoder.Encode(samples, engine.MaximumAudioUploadBytes), language,
                _textAtStart.PreferredLanguageHints.Split(',', StringSplitOptions.RemoveEmptyEntries), translate, ct, dictionary?.EnabledTerms);
        }, _operationCancellation.Token);
        return (result.Text, result.TokenTimings.ToArray(), result.DetectedLanguage, result.NoSpeechProbability);
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
        _audio = new(_recoveryAudio);
        Recovery = new(_recoveryAudio, DecodeRecoveryAudioAsync);
        _transcriptionPlugin = new(packageDirectory: () => Packages.Store.Resolve(LocalTranscriptionPlugin.PluginId));
        CtcVocabulary = new(packageDirectory: () => Path.Combine(Packages.Store.Resolve(LocalTranscriptionPlugin.PluginId), "Dependencies", LocalCtcVocabulary.PluginId));
        PluginRuntime = new(Packages.Store, LocalCtcVocabulary.HostVersion, WinUIPluginPackages.CreateServices,
            id => id is not (LocalTranscriptionPlugin.PluginId or LocalCtcVocabulary.PluginId));
        _speechBackend = new(PluginRuntime, new WindowsSystemVoiceBackend());
        SpokenFeedback = new(_speechBackend);
        PluginRuntime.Changed += () => Changed?.Invoke();
        _history = history;
        HistoryRetention = new(history, new HistoryRetentionPreferencesStore(WinUIProfile.DataPath("history-retention.json")));
        _inserter = new(owner);
        _effects = new(_ducking, new MediaPauseService());
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _recoveryAudio.Changed += () => dispatcher.TryEnqueue(ReportRecoveryStorageError);
        _fileDispatcher = dispatcher;
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
        _audio.SamplesAvailable += (_, args) => _cloudStream?.Append(args.Samples);
        _audio.AudioLevelChanged += (_, level) => _silence?.Observe(_silenceClock.Elapsed, level.RmsLevel);
        _audio.DeviceLost += (_, _) => dispatcher.TryEnqueue(() =>
        {
            if (_disposed || _audio.IsRecording) return;
            StopSilenceMonitoring();
            _livePreview.Cancel();
            _cloudStream?.Cancel();
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
        if (_disposed) return;
        await ApplyHistoryRetentionAsync();
        if (_disposed) return;
        _retentionTimer.Start();
        await _gate.WaitAsync();
        try
        {
            await _recoveryAudio.InitializeAsync();
            await _recoveryAudio.SetRetentionAsync(RecoveryPreferences.Current.Enabled ? RecoveryPreferences.Current.RetentionDays : 0);
            if (_disposed) return;
            ReportRecoveryStorageError();
            await Packages.InitializeAsync();
            if (_disposed) return;
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
            _providerId = SessionProviderId(_selection.GetSetting<string>("Provider") ?? "local");
            SetStatus("Loading model…", DictationPhase.LoadingModel);
            await PluginRuntime.InitializeAsync();
            if (_disposed) return;
            try { if (Packages.Store.IsInstalled(LocalTranscriptionPlugin.PluginId)) await _transcriptionPlugin.InitializeAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { LocalPluginError = ex.Message; }
            if (_disposed) return;
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
    internal bool CanStartFromShortcut => IsReady
#if DEBUG
        || CorrectionProbeEnabled
#endif
        ;
#if DEBUG
    internal static bool CorrectionProbeEnabled => WinUIProfile.IsTestProfile && Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_CORRECTION_PROBE") == "1";
#endif
    internal Task StartAsync() => SetRecordingAsync(true);
    internal Task StartAsync(AutomaticWorkflowSnapshot workflow) => SetRecordingAsync(true, workflow);
    internal Task StopAsync() => SetRecordingAsync(false);
    internal async Task CancelAsync()
    {
        RequestCancel();
        if (_disposed) return;
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            StopSilenceMonitoring();
            _livePreview.Cancel();
            _cloudStream?.Cancel();
            if (_audio.IsRecording) await _audio.StopRecordingAsync();
            await StopCloudStreamAsync();
            _effects.End();
            await _livePreview.StopAsync();
            SetStatus($"Shortcut cancelled · {ActiveModelName} ready");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SetStatus("Could not cancel recording: " + ex.Message); }
        finally { _effects.End(); _gate.Release(); }
    }

    private async Task SetRecordingAsync(bool? recording, AutomaticWorkflowSnapshot? workflow = null)
    {
        if (_disposed || !await _gate.WaitAsync(0)) return;
#if DEBUG
        if (CorrectionProbeEnabled && !_audio.IsRecording)
        {
            try
            {
                await CorrectionLearning.Cancel();
                var target = GetForegroundWindow();
                const string sample = "We use teh tool every day.";
                for (var attempt = 0; attempt < 80 && ModifiersHeld(); attempt++) await Task.Delay(25);
                var inserted = await _inserter.InsertAsync(sample, target);
                File.WriteAllText(WinUIProfile.DataPath("correction-probe.txt"), inserted ? "inserted" : "paste_failed");
                if (inserted) CorrectionLearning.Observe(sample, target);
            }
            finally { _gate.Release(); }
            return;
        }
        if (WorkflowProbeEnabled && !_audio.IsRecording)
        {
            try { if (recording != false) await RunWorkflowProbeAsync(); }
            finally { _gate.Release(); }
            return;
        }
#endif
        TypeWhisper.Core.Services.RecoveryRecordingLease? recoveryLease = null;
        var preserveRecovery = false;
        try
        {
            if (recording.HasValue && recording.Value == _audio.IsRecording) return;
            if (!IsReady) { SetStatus("No model is ready. Download a model or configure a cloud provider in plugin settings, then select it in Dictation."); return; }
            if (!_audio.IsRecording)
            {
                await CorrectionLearning.Cancel();
                _operationCancellation.Begin();
                if (TranscriptionTaskPreferences.Current == TranscriptionTask.Translate && !SupportsTranslation)
                {
                    SetStatus("This model cannot translate to English. Choose Transcribe or a translation-capable model in Dictation.");
                    return;
                }
                _taskAtStart = TranscriptionTaskPreferences.Current;
                _engineAtStart = ActiveEngineId;
                _modelAtStart = ActiveModelId;
                _originalField?.Dispose(); _originalField = null;
                _target = GetForegroundWindow();
                GetWindowThreadProcessId(_target, out var processId);
                _setupOutputAtStart = processId == Environment.ProcessId ? SetupTestTarget?.Invoke(_target) : null;
                if (_target == IntPtr.Zero || (processId == Environment.ProcessId && _setupOutputAtStart is null))
                {
                    SetStatus($"Focus a text field in another app, then press {Shortcut}.");
                    return;
                }
                _targetProcessId = processId;
                PasteDiagnostics.Write("dictation.start");
                if (OutputPreferences.Current is { AutoPaste: true, LockPasteToFocusedField: true } && _setupOutputAtStart is null)
                    _originalField = OriginalDictationField.Capture(_target, processId);
                try { using var process = System.Diagnostics.Process.GetProcessById((int)processId); _targetApp = process.ProcessName; }
                catch (ArgumentException) { _targetApp = "Target app"; }
                if (_setupOutputAtStart is not null) { _targetHostAtStart = null; _workflowAtStart = null; }
                else if (workflow is null) await CaptureWorkflowAtStartAsync();
                else { _targetHostAtStart = null; _workflowAtStart = workflow; }
                _operationCancellation.Token.ThrowIfCancellationRequested();
                if (_disposed) return;
                var preferences = AudioPreferences;
                _spokenFeedbackAtStart = preferences;
                StopHistoryPlayback?.Invoke();
                await SpokenFeedback.CancelAndDrainAsync();
                await _livePreview.StopAsync();
                await StopCloudStreamAsync();
                _operationCancellation.Token.ThrowIfCancellationRequested();
                if (_disposed) return;
                GetWindowThreadProcessId(_target, out var currentTargetProcessId);
                if (GetForegroundWindow() != _target || currentTargetProcessId != processId)
                {
                    SetStatus("The target changed before recording. Focus your text field and try again.");
                    return;
                }
                _audio.WhisperModeEnabled = preferences.WhisperModeEnabled;
                _outputAtStart = OutputPreferences.Current;
                _textAtStart = TextPreferences.Current;
                _processorsAtStart = PluginRuntime.PostProcessors.ToArray();
                _languageAtStart = Language;
                _recoveryAtStart = RecoveryPreferences.Current;
                LivePreviewText = "";
                _hasConfirmedPreviewText = false;
                _dictionarySnapshot = Task.Run(() => DictationDictionarySnapshot.Load(DictationDictionarySnapshot.StoragePath));
                await StartCloudStreamAsync();
                _operationCancellation.Token.ThrowIfCancellationRequested();
                if (_disposed) return;
                GetWindowThreadProcessId(_target, out currentTargetProcessId);
                if (GetForegroundWindow() != _target || currentTargetProcessId != processId)
                {
                    await StopCloudStreamAsync();
                    SetStatus("The target changed before recording. Focus your text field and try again.");
                    return;
                }
                _audio.StartRecording(enableRecovery: _recoveryAtStart.Enabled && _recoveryAtStart.IsValid);
                if (!_audio.IsRecording) { SetStatus("Microphone could not start. Check the input device and microphone access."); return; }
                BeginApiDictationGeneration();
                _snippetSnapshot = Task.Run(() => DictationSnippetSnapshot.Load(DictationSnippetSnapshot.StoragePath));
                _boostVocabulary = DictionaryBoostingPreferences.Load();
                _ctcAtStart = _taskAtStart == TranscriptionTask.Transcribe && !UsesRegistryProvider && Models.ActiveModelId == "parakeet-tdt-0.6b" && CtcVocabulary.Enabled;
                if (_cloudStream is null && LivePreviewEnabled && SupportsLiveTranscription &&
                    (!UsesRegistryProvider || ActiveRegistryProvider is { SupportsPcm: true, SupportsLocalLivePreview: true } preview && PackageIsLocal(preview.PluginId)))
                    _livePreview.Start(() => _audio.HasSpeechEnergy ? _audio.GetCurrentBuffer() : null,
                        DecodeAsync,
                        text => { _hasConfirmedPreviewText |= !string.IsNullOrWhiteSpace(text); LivePreviewText = text; LivePreviewChanged?.Invoke(); },
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
                SetStatus($"Recording · {Shortcut} to finish");
                return;
            }

            StopSilenceMonitoring();
            _livePreview.Cancel();
            _lastDuration = _audio.RecordingDuration;
            var preGainPeakRms = _audio.PreGainPeakRmsLevel;
            SetStatus("Finishing recording…", DictationPhase.Processing);
            var captured = await _audio.StopRecordingWithRecoveryAsync();
            recoveryLease = captured.RecoveryLease;
            var samples = captured.Samples;
            _effects.End();
            _sounds.PlayStopSound();
            // Native decoding cannot be interrupted; drain the cancelled preview
            // before the final decode uses the same recognizer.
            await _livePreview.StopAsync();
            // Use captured samples for the policy and history, never decoder padding or elapsed stop time.
            _operationCancellation.Token.ThrowIfCancellationRequested();
            var rawDuration = (samples?.Length ?? 0) / 16000.0;
            var captureDecision = ShortClipCapturePolicy.Classify(rawDuration, preGainPeakRms,
                _hasConfirmedPreviewText, _textAtStart.TranscribeShortQuietClipsAggressively);
            if (samples is null || captureDecision == ShortClipCaptureDecision.TooShort)
            { SetStatus("Recording was too short. Hold the shortcut a little longer."); return; }
            if (captureDecision == ShortClipCaptureDecision.NoSpeech)
            { SetStatus("No speech energy detected. Speak closer to the microphone or enable Recognize short, quiet clips."); return; }
            SetStatus($"Transcribing with {ActiveModelName}…", DictationPhase.Processing);
            var streamedText = _cloudStream is null ? null : await _cloudStream.FinishAsync(samples.Length);
            _operationCancellation.Token.ThrowIfCancellationRequested();
            var decoded = streamedText is null
                ? await DecodeFinalAsync(ShortClipCapturePolicy.PadForFinalDecode(samples))
                : (Text: streamedText, Timings: Array.Empty<VocabularyTokenTiming>(), DetectedLanguage: _cloudStream?.DetectedLanguage, NoSpeechProbability: (float?)null);
            _operationCancellation.Token.ThrowIfCancellationRequested();
            var rawText = decoded.Text;
            if (FinalSpeechPolicy.ShouldReject(rawText, decoded.NoSpeechProbability,
                _hasConfirmedPreviewText, _textAtStart.TranscribeShortQuietClipsAggressively))
            { SetStatus("No speech recognized. Ready to try again."); return; }
            // Empty final output does not reuse preview text or its unrelated token timings.
            if (string.IsNullOrWhiteSpace(rawText)) { SetStatus("No speech recognized. Ready to try again."); return; }
            if (_setupOutputAtStart is { } setupOutput)
            {
                // A setup sample never pastes into another app or creates a history entry.
                _operationCancellation.Token.ThrowIfCancellationRequested();
                if (_disposed) return;
                setupOutput(rawText);
                SetStatus("Setup dictation completed.", DictationPhase.Completed);
                return;
            }
            var dictionary = _dictionarySnapshot is null ? null : await _dictionarySnapshot;
            var refinedText = rawText;
            var recordingId = Guid.NewGuid();
            if (_ctcAtStart && dictionary is not null && CtcVocabulary.Enabled)
            {
                SetStatus("Checking vocabulary with CTC…", DictationPhase.Processing);
                var refined = await CtcVocabulary.RefineAsync(recordingId, rawText, samples, decoded.Timings, dictionary.EnabledCtcEntries, _operationCancellation.Token);
                refinedText = refined.Text;
                if (refined.Error is not null) System.Diagnostics.Debug.WriteLine(refined.Error);
            }
            else CtcVocabulary.Trace($"{recordingId} host-skipped enabledAtStart={_ctcAtStart} enabledNow={CtcVocabulary.Enabled} dictionaryLoaded={dictionary is not null}");
            var boostVocabulary = _boostVocabulary && !_ctcAtStart;
            var snippets = _snippetSnapshot is null ? null : await _snippetSnapshot;
            var processed = await new DictationLexiconSnapshot(dictionary, snippets).ProcessAsync(refinedText, _textAtStart, _languageAtStart,
                DictationProvenance.ResolveLanguage(decoded.DetectedLanguage, _languageAtStart), boostVocabulary,
                ReadSnippetClipboardAsync, _operationCancellation.Token, _taskAtStart, _targetApp, _engineAtStart, _modelAtStart,
                WorkflowProcessor(_languageAtStart, decoded.DetectedLanguage),
                BindTextProcessors(_processorsAtStart, DictationProvenance.ResolveLanguage(decoded.DetectedLanguage, _languageAtStart),
                    _targetApp, _workflowAtStart?.Name, rawDuration));
            var notices = processed.Warnings.ToList();
            if (processed.WorkflowError is { } workflowError) notices.Add(workflowError);
            var text = processed.Text;
            _operationCancellation.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            if (DictationLexiconSnapshot.RecordUsage(DictationSnippetSnapshot.StoragePath, processed.AppliedSnippetIds) is { } usageError)
                notices.Add(usageError);
            var snippetError = notices.Count == 0 ? null : string.Join(" · ", notices);
            var record = new TranscriptionRecord
            {
                Id = recordingId.ToString(), Timestamp = _started, CreatedAt = DateTime.UtcNow,
                SourceKind = "dictation",
                WorkflowId = _workflowAtStart?.Id, ProfileName = _workflowAtStart?.Name,
                Status = processed.WorkflowError is not null ? TranscriptionRecordStatus.WorkflowPostProcessingFailed
                    : processed.TextProcessors?.Any(item => item.Status == "failed") == true ? TranscriptionRecordStatus.TextProcessorFailed : TranscriptionRecordStatus.Succeeded,
                TextProcessors = processed.TextProcessors?.ToArray(),
                WorkflowFailureMessage = processed.WorkflowError,
                RawText = rawText, FinalText = text, DurationSeconds = rawDuration,
                EngineUsed = _engineAtStart, ModelUsed = _modelAtStart, TranscriptionTaskUsed = _taskAtStart == TranscriptionTask.Translate ? "translate" : "transcribe",
                Language = DictationProvenance.ResolveLanguage(decoded.DetectedLanguage, _languageAtStart),
                AppName = _targetApp == "Target app" ? null : _targetApp,
                AppProcessName = _targetApp == "Target app" ? null : _targetApp,
                AppUrl = _targetHostAtStart
            };
            var delivery = new DictationOutputDelivery(_history);
            PasteDiagnostics.Write("delivery.begin");
            var outcome = await delivery.DeliverAsync(record, processed.WorkflowError is null ? _outputAtStart : _outputAtStart with { AutoPaste = false },
                () => OutputPreferences.Current, async () =>
                {
                    // Recheck after waiting: settings can change while modifiers are held.
                    for (var attempt = 0; attempt < 40 && ModifiersHeld(); attempt++) await Task.Delay(25, _operationCancellation.Token);
                    _operationCancellation.Token.ThrowIfCancellationRequested();
                    if (_disposed || !_outputAtStart.RestrictedBy(OutputPreferences.Current).AutoPaste ||
                        ModifiersHeld()) { PasteDiagnostics.Write("delivery.blocked-settings-modifiers-or-disposed"); return false; }
                    var lockField = _outputAtStart.RestrictedBy(OutputPreferences.Current).LockPasteToFocusedField;
                    if (lockField)
                    {
                        if (_originalField is null) { PasteDiagnostics.Write("delivery.no-captured-field"); return false; }
                        if (OutputPreferences.Current.LockPasteToFocusedField &&
                            !await _originalField.RestoreAsync(_operationCancellation.Token)) { PasteDiagnostics.Write("delivery.restore-failed"); return false; }
                        if (!_originalField.IsCurrent()) { PasteDiagnostics.Write("delivery.field-not-current"); return false; }
                    }
                    if (GetForegroundWindow() != _target) { PasteDiagnostics.Write("delivery.target-not-foreground"); return false; }
                    var inserted = await _inserter.InsertAsync(text, _target, () =>
                        !_disposed && !_operationCancellation.Token.IsCancellationRequested &&
                        _outputAtStart.RestrictedBy(OutputPreferences.Current).AutoPaste &&
                        (!_outputAtStart.RestrictedBy(OutputPreferences.Current).LockPasteToFocusedField || _originalField?.IsCurrent() == true));
                    if (inserted && !_disposed && !_operationCancellation.Token.IsCancellationRequested && record.Status == TranscriptionRecordStatus.Succeeded)
                        CorrectionLearning.Observe(text, _target);
                    return inserted;
                }, _operationCancellation.Token, samples, 16000);
            preserveRecovery = outcome.Failed || record.Status != TranscriptionRecordStatus.Succeeded;
            PasteDiagnostics.Write(outcome.NeedsReview ? "delivery.review" : "delivery.completed");
            if (_disposed) return;
            _operationCancellation.Token.ThrowIfCancellationRequested();
            if (_lastCompletedDictation.TryPublish(outcome, _operationCancellation.Token)) PublishApiDictationRecord(outcome.Record);
            LastUnsavedText = outcome.Saved ? null : text;
            if (!outcome.NeedsReview) LivePreviewText = text;
            SetStatus(snippetError is null ? outcome.Message : outcome.Message + " · " + snippetError,
                outcome.NeedsReview ? DictationPhase.Idle : DictationPhase.Completed);
            if (outcome.NeedsReview) ReviewRequested?.Invoke(outcome);
            else OutputCompleted?.Invoke(recordingId);
            ReadCompletedDictation(record, outcome, processed.Warnings.Count == 0);

        }
        catch (Exception ex) when (ex is not OutOfMemoryException && _operationCancellation.Token.IsCancellationRequested)
        {
            preserveRecovery = _disposed;
            StopSilenceMonitoring();
            await StopRecoveryCaptureAsync(preserve: _disposed);
            _effects.End();
            await _livePreview.StopAsync();
            if (!_disposed) SetStatus("Dictation canceled. Ready to try again.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            preserveRecovery = true;
            StopSilenceMonitoring();
            _livePreview.Cancel();
            try { await StopRecoveryCaptureAsync(preserve: true); }
            catch (Exception stopError) when (stopError is not OutOfMemoryException)
            { System.Diagnostics.Debug.WriteLine(stopError); }
            finally { _effects.End(); await _livePreview.StopAsync(); }
            _sounds.PlayErrorSound();
            SetStatus("Dictation failed: " + ex.Message, DictationPhase.Error);
        }
        finally
        {
            await FinishRecoveryLeaseAsync(recoveryLease, preserveRecovery || _disposed);
            if (!_audio.IsRecording) { _originalField?.Dispose(); _originalField = null; _setupOutputAtStart = null; _effects.End(); await StopCloudStreamAsync(); }
            _gate.Release();
        }
    }

    internal string? LastUnsavedText { get; private set; }
    private async Task<string> DecodeAsync(float[] samples) => (await DecodeFinalAsync(samples, false)).Text;
    private Task<(string Text, VocabularyTokenTiming[] Timings, string? DetectedLanguage, float? NoSpeechProbability)> DecodeFinalAsync(float[] samples, bool includeTimings = true) =>
        UsesRegistryProvider ? DecodeRegistryAsync(samples)
            : _transcriptionPlugin.DecodeAsync(samples, includeTimings, _taskAtStart == TranscriptionTask.Translate, _operationCancellation.Token);
    private void StopSilenceMonitoring()
    {
        _silence = null;
        _silenceTimer.Stop();
        _silenceClock.Stop();
    }
    private void SetStatus(string status, DictationPhase? phase = null)
    {
        if (_disposed) return;
        if (phase != DictationPhase.LoadingModel) _showModelLoadingForDictation = false;
        Status = status;
        _phase = phase ?? (_audio.IsRecording ? DictationPhase.Recording : DictationPhase.Idle);
        Changed?.Invoke();
    }

    private static bool ModifiersHeld() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
