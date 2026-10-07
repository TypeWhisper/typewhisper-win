using TypeWhisper.Core.Interfaces;
using TypeWhisper.Presentation;
using RecordingMode = TypeWhisper.Presentation.RecordingMode;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI.Platform;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginHost;

namespace TypeWhisper.WinUI;

// Initial local vertical slice: reuses the existing capture implementation and
// Parakeet configuration. Uses portable providers and host-rendered settings.
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
        if (!CanChangeProvider || !_gate.Wait(0)) return Loc.T("Finish dictation before changing recording mode.");
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
        if (!CanChangeProvider || !_gate.Wait(0)) return Loc.T("Finish dictation before changing the task.");
        try
        {
            if (task == TranscriptionTask.Translate && !SupportsTranslation)
                return Loc.T("This model does not support translation to English. Choose a compatible model first.");
            return TranscriptionTaskPreferences.Save(task);
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }
    private DictationTextPreferences _textAtStart = new();
    private DictationOutputPreferences _outputAtStart = new();
    internal event Action<DictationOutputResult>? ReviewRequested;
    internal event Action<string>? OutputWarning;
    internal event Action<Guid>? OutputCompleted;
    internal bool LivePreviewEnabled { get; set; } = true;
    // Availability describes the host's connected preview path, not just an SDK streaming declaration.
    private bool ModelSupportsLiveTranscription => UsesRegistryProvider
        ? ActiveRegistryProvider is { SupportsStreaming: true } || (ActiveRegistryProvider is { SupportsPcm: true, SupportsLocalLivePreview: true } preview && PackageIsLocal(preview.PluginId))
        : Models.SupportsLocalLivePreview;
    // The captured task applies from recording through processing and its error state.
    internal bool SupportsLiveTranscription => ModelSupportsLiveTranscription &&
        (_audio.IsRecording || _phase is DictationPhase.Processing or DictationPhase.Error ? _taskAtStart : TranscriptionTaskPreferences.Current)
            == TranscriptionTask.Transcribe;
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
        if (_disposed) return Loc.T("Audio preferences are unavailable during shutdown.");
        try
        {
            preferences = preferences.Validated();
            AtomicFileWriter.WriteAllText(AudioPreferencesPath, System.Text.Json.JsonSerializer.Serialize(preferences));
            AudioPreferences = preferences;
            AudioPreferencesError = null;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return AudioPreferencesError = Loc.T("Could not save audio preferences: {0}", ex.Message); }
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
    internal async Task<string> ProcessLlmAsync(string selectionId, string systemPrompt, string text, string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Give foreground workflows priority over cancellable settings downloads.
        await LocalLlmDownload.CancelAndDrainAsync();
        return await PluginRuntime.UseLlmAsync(selectionId, (provider, token) => provider.ProcessAsync(systemPrompt, text, model, token), ct);
    }
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
        ? provider.Name + " · " + (provider.Models.FirstOrDefault(model => model.Id == provider.SelectedModelId)?.DisplayName ?? Loc.T("No model selected"))
        : Loc.T("Selected provider unavailable") : Models.ActiveModelName;
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
    /// <summary>Languages the spoken-language picker offers; a local model without its own list offers every language.</summary>
    /// <summary>Whether the spoken-language picker offers automatic detection for the active model.</summary>
    internal bool DetectsLanguage => UsesRegistryProvider || SupportedLanguages.Count == 0;
    internal IReadOnlyList<string> LanguageChoices => UsesRegistryProvider ? SupportedLanguages : SpokenLanguageChoices.For(SupportedLanguages);
    internal string Language => !UsesRegistryProvider ? Models.Language : SupportedLanguages.Count == 0 ? "auto" : ActiveRegistryProvider is { } provider
        ? WinUIPluginPackages.CreateServices(provider.PluginId).GetSetting<string>("Language") ?? "auto" : "auto";
    private bool CanStartSessionOperation => !_disposed && !_fileBusy && !_recorderReserved && !_workflowReserved && !IsRecording && _phase is not (DictationPhase.Processing or DictationPhase.Configuring or DictationPhase.LoadingModel);
    internal bool CanChangeProvider => CanStartSessionOperation && !PluginRuntime.IsBusy;
    internal bool CanStartPluginSettingsAction => CanChangeProvider && _gate.CurrentCount > 0;
    internal event Action? RecordingStarting;
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
        // An attempt that can capture right away shows its recording instead.
        if (_phase != DictationPhase.LoadingModel || _disposed || CanCaptureWhileModelLoads) return;
        _showModelLoadingForDictation = true;
        Changed?.Invoke();
    }
    internal DictationOverlayState OverlayState => new(
        DictationOverlayState.VisiblePhase(_phase, _showModelLoadingForDictation, _earlyCapture, _earlyStopSamples is not null),
        _audio.IsRecording ? _audio.RecordingDuration : _lastDuration, Status, _targetApp, _targetProcessId, RecordingModePreferences.Current, CancelWarning, Cancelled);
    internal string Status { get; private set; } = Loc.T("Loading local transcription plugin…");
    internal const string DefaultShortcut = "Ctrl+Shift";
    internal string Shortcut { get; set; } = DefaultShortcut;
    internal bool IsRecording => !_recorderReserved && _audio.IsRecording;
    internal bool IsReady => UsesRegistryProvider ? ActiveRegistryProvider?.Ready == true : _transcriptionPlugin.Ready;
    internal string? LocalPluginError { get; private set; }

    internal async Task<string?> SetLocalPluginEnabledAsync(bool enabled)
    {
        if (enabled && !Packages.Store.IsInstalled(LocalTranscriptionPlugin.PluginId)) return Loc.T("Install NVIDIA Parakeet under Integrations first.");
        if (_disposed || !await _gate.WaitAsync(0)) return Loc.T("Wait until dictation is ready before changing the plugin.");
        try
        {
            if (_audio.IsRecording) return Loc.T("Finish recording before changing the plugin.");
            SetStatus(enabled ? Loc.T("Loading local transcription plugin…") : Loc.T("Unloading local transcription plugin…"), DictationPhase.Configuring);
            await _livePreview.StopAsync();
            await _transcriptionPlugin.SetEnabledAsync(enabled);
            if (_disposed) return Loc.T("The application is shutting down.");
            var vocabularyError = await CtcVocabulary.SetEnabledAsync(Models.Enabled);
            LocalPluginError = Models.Error ?? vocabularyError;
            SetStatus(enabled ? IsReady ? ModelReadyStatus() : Models.Error ?? Loc.T("Download a model in plugin settings, then select it in Dictation.") : Loc.T("Local transcription plugin disabled"), DictationPhase.Idle);
            return LocalPluginError;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LocalPluginError = Loc.T("Could not update local transcription plugin: {0}", ex.Message);
            SetStatus(LocalPluginError, DictationPhase.Idle);
            return LocalPluginError;
        }
        finally { _gate.Release(); }
    }
    internal async Task<string?> SelectModelAsync(string modelId)
    {
        if (!CanSelectModel || !await _gate.WaitAsync(0)) return Loc.T("Finish recording or the current model operation before changing models.");
        try
        {
            SetStatus(Loc.T("Loading model… Wait until the model is ready before dictating."), DictationPhase.LoadingModel);
            await _livePreview.StopAsync();
            await Models.ActivateAsync(modelId);
            _selection.SetSetting("Provider", "local");
            _providerId = "local";
            LocalPluginError = null;
            SetStatus(ModelReadyStatus(), DictationPhase.Idle);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LocalPluginError = Loc.T("Could not select model: {0}", ex.Message);
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
            return Loc.T("Finish dictation and model operations before uninstalling a plugin.");
        try
        {
            SetStatus(Loc.T("Uninstalling plugin…"), DictationPhase.Configuring);
            progress?.Report(new(Loc.T("Finishing running plugin operations…")));
            await _livePreview.StopAsync();
            progress?.Report(new(Loc.T("Unloading plugin resources…")));
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
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Loc.T("Could not uninstall plugin: {0}", ex.Message); }
        finally
        {
            SetStatus(IsReady ? ModelReadyStatus() : Loc.T("Choose an installed provider in Dictation."), DictationPhase.Idle);
            _gate.Release(); Changed?.Invoke();
        }
    }
    internal string? SelectLanguage(string language)
    {
        if (!(UsesRegistryProvider ? CanChangeProvider && IsReady : CanSelectModel) || !_gate.Wait(0)) return Loc.T("Finish dictation before changing the language.");
        try
        {
            if (UsesRegistryProvider)
            {
                if (language != "auto" && !SupportedLanguages.Contains(language)) return Loc.T("This provider does not support that language.");
                if (ActiveRegistryProvider is not { } provider) return Loc.T("The selected provider is unavailable.");
                WinUIPluginPackages.CreateServices(provider.PluginId).SetSetting("Language", language);
            }
            else Models.SelectLanguage(language);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Loc.T("Could not save language: {0}", ex.Message); }
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
            return Task.FromResult<string?>(Loc.T("Finish dictation and model operations before selecting a model."));
        var model = PluginRuntime.TranscriptionProviders
            .FirstOrDefault(provider => provider.SelectionId == RegistrySelectionId(providerId))?
            .ModelStates.FirstOrDefault(model => model.ModelId == modelId);
        return model is null ? Task.FromResult<string?>(Loc.T("This model is no longer available. Refresh its provider settings."))
            : UseRegistryModelAsync(model);
    }
    private async Task<string?> ChangeRegistryPluginAsync(string id, Func<Task> action, bool loadingModel = false)
    {
        if (!Packages.Store.IsInstalled(id)) return Loc.T("Install this plugin in Integrations first.");
        if (!CanChangeProvider || Models.Busy || !await _gate.WaitAsync(0)) return Loc.T("Finish dictation and model operations before changing plugins.");
        try
        {
            SetStatus(loadingModel ? Loc.T("Loading model…") : Loc.T("Updating plugin…"), loadingModel ? DictationPhase.LoadingModel : DictationPhase.Configuring);
            await _livePreview.StopAsync();
            await action();
            SetStatus(IsReady ? ModelReadyStatus() : Loc.T("Choose and configure a transcription provider in Dictation."), DictationPhase.Idle);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var message = Loc.T("Plugin operation could not be completed ({0}). Check the plugin's configuration and try again.", ex.GetType().Name);
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
    internal string SelectedMicrophoneName => _microphones.FirstOrDefault()?.Name ?? Loc.T("System default");
    internal IReadOnlyList<AudioInputDeviceInfo> GetMicrophones() => _audio.GetAvailableInputDeviceInfos();
    internal MicrophoneTestSnapshot? MicrophoneTest => _audio.MicrophoneTest;
    internal string? StartMicrophoneTest()
    {
        if (!_gate.Wait(0)) return Loc.T("Please wait until dictation is ready.");
        try
        {
            if (!CanStartSessionOperation) return Loc.T("Finish the current operation before testing the microphone.");
            if (_audio.IsPreviewing) return Loc.T("A microphone test is already running.");
            _audio.StartPreview(null);
            var error = _audio.MicrophoneTest?.Error;
            if (error is not null) _audio.StopPreview();
            return error;
        }
        catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex)) { return MicrophoneFailure.Describe(ex); }
        finally { _gate.Release(); }
    }
    internal void StopMicrophoneTest() => _audio.StopPreview();
    // Raised on the UI thread when microphones are connected, removed or switched.
    internal event Action? MicrophonesChanged;
    // The notice last reflected in a status. A change that could not be shown yet still differs from it.
    private string? _microphoneNotice;
    // The last idle status built from the microphone state; other idle messages, such as operation errors, are kept.
    private string? _microphoneStatus;

    // Why dictation cannot use the preferred microphone right now; null when it can.
    internal string? MicrophoneNotice()
    {
        if (!_audio.HasDevice) return Loc.T("No microphone connected. Connect one to dictate.");
        if (_audio.CaptureFailure is { } failure) return failure;
        return MicrophoneFailure.PriorityNotice(_microphones, GetMicrophones());
    }

    private string ReadyStatus(bool prepared)
    {
        _microphoneNotice = MicrophoneNotice();
        return _microphoneStatus = !IsReady ? UsesRegistryProvider ? Loc.T("The selected provider is unavailable or not configured. Open Integrations, then select a ready model in Dictation.") : !Models.Enabled ? Loc.T("Local transcription plugin disabled") : Models.Error ?? Loc.T("Download a model in plugin settings, then select it in Dictation.")
            : _microphoneNotice is { } notice ? Loc.T("{0} ready · {1}", ActiveModelName, notice)
            : prepared ? Loc.T("{0} ready · {1} to dictate", ActiveModelName, Loc.T(Shortcut)) : Loc.T("{0} ready · microphone preparation failed; check the device", ActiveModelName);
    }

    // Every idle "ready" status keeps a current microphone warning visible.
    private string ModelReadyStatus() => _microphoneStatus = (_microphoneNotice = MicrophoneNotice()) is { } notice ? Loc.T("{0} ready · {1}", ActiveModelName, notice) : Loc.T("{0} ready", ActiveModelName);

    // Resume may recreate the capture without a device-list change, so publish its result explicitly.
    internal void RefreshMicrophoneAfterResume() =>
        Task.Run(_audio.RefreshAfterDisplayOrPowerChange).ContinueWith(_ => _fileDispatcher.TryEnqueue(OnMicrophonesChanged), TaskScheduler.Default);

    private void OnMicrophonesChanged()
    {
        if (_disposed) return;
        MicrophonesChanged?.Invoke();
        RefreshMicrophoneStatus();
    }

    private void RefreshMicrophoneStatus()
    {
        var notice = MicrophoneNotice();
        if (notice == _microphoneNotice || _audio.IsRecording) return;
        // An error or review outcome with an appended notice keeps the outcome and only updates the notice.
        // A completed dictation follows the microphone like before, whether or not a notice was appended.
        if (_phase != DictationPhase.Completed && _noticeOutcome is { } outcome && Status == _noticeStatus)
        {
            _microphoneNotice = notice;
            SetStatus(_noticeStatus = notice is null ? outcome : outcome + " · " + notice, _phase);
        }
        // Recording, processing, errors and other idle messages stay; a microphone-derived idle status follows the microphone.
        else if (_phase == DictationPhase.Completed || (_phase == DictationPhase.Idle && Status == _microphoneStatus))
            SetStatus(ReadyStatus(prepared: true));
    }

    // The dictation outcome a notice was appended to, and the status that shows both.
    private string? _noticeOutcome;
    private string? _noticeStatus;

    // A change during recording or processing is not shown over the dictation's own status.
    // Once the dictation settles, add it to the outcome so it does not wait for another device event.
    private void PublishMicrophoneNoticeAfterDictation()
    {
        if (_disposed || _audio.IsRecording) return;
        var notice = MicrophoneNotice();
        if (notice == _microphoneNotice) return;
        _microphoneNotice = notice;
        // A notice that has cleared needs no mention; the outcome never showed the old one.
        if (notice is null) return;
        _noticeOutcome = Status;
        SetStatus(_noticeStatus = Status + " · " + notice, _phase);
    }

    internal string? SelectMicrophone(string id)
    {
        var device = GetMicrophones().FirstOrDefault(item => item.Id == id);
        if (id != "default" && device is null) return Loc.T("This microphone is no longer available. Reopen Audio to refresh.");
        return SetMicrophonePriority(device is null ? [] :
            new[] { new MicrophonePriorityItem(device.Id, device.Name) }.Concat(_microphones.Where(item => item.Id != id)).ToArray());
    }

    internal string? SetMicrophonePriority(IReadOnlyList<MicrophonePriorityItem> devices)
    {
        if (!_gate.Wait(0)) return Loc.T("Please wait until dictation is ready.");
        try
        {
            if (_audio.IsRecording) return Loc.T("Finish the current recording before changing microphones.");
            _audio.StopPreview();
            var selected = devices.DistinctBy(item => item.Id).ToList();
            AtomicFileWriter.WriteAllText(MicrophonePath, System.Text.Json.JsonSerializer.Serialize(selected));
            _microphones = selected;
            _audio.SetMicrophonePriorityList(selected);
            RefreshMicrophoneStatus();
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Loc.T("Could not apply microphone: {0}", ex.Message); }
        finally { _gate.Release(); }
    }

    internal LocalDictationSession(IHistoryService history, IntPtr owner)
    {
        _audio = new(_recoveryAudio) { ReleaseCaptureBetweenRecordings = Platform.RemoteSession.IsActive };
        Recovery = new(_recoveryAudio, DecodeRecoveryAudioAsync);
        var isolation = CreateTranscriptionIsolation();
        _transcriptionPlugin = new(packageDirectory: () => Packages.Store.Resolve(LocalTranscriptionPlugin.PluginId), isolation: isolation);
        CtcVocabulary = new(packageDirectory: () => Path.Combine(Packages.Store.Resolve(LocalTranscriptionPlugin.PluginId), "Dependencies", LocalCtcVocabulary.PluginId));
        PluginRuntime = new(Packages.Store, LocalCtcVocabulary.HostVersion, WinUIPluginPackages.CreateServices,
            id => id is not (LocalTranscriptionPlugin.PluginId or LocalCtcVocabulary.PluginId)) { TranscriptionIsolation = isolation, IdleUnloadPolicy = ModelIdlePolicy };
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
        _audio.SamplesAvailable += (_, args) => _streamAudio.Append(args.Samples);
        _audio.AudioLevelChanged += (_, level) => _silence?.Observe(_silenceClock.Elapsed, level.RmsLevel);
        _audio.DevicesChanged += (_, _) => dispatcher.TryEnqueue(OnMicrophonesChanged);
        _audio.DeviceAvailable += (_, _) => dispatcher.TryEnqueue(OnMicrophonesChanged);
        _audio.DeviceLost += (_, _) => dispatcher.TryEnqueue(OnMicrophonesChanged);
        _audio.DeviceLost += (_, _) => dispatcher.TryEnqueue(() =>
        {
            if (_disposed || _audio.IsRecording) return;
            StopSilenceMonitoring();
            _livePreview.Cancel();
            _cloudStream?.Cancel();
            _effects.End();
            if (_phase == DictationPhase.Recording) SetStatus(Loc.T("Microphone disconnected · recording stopped"), DictationPhase.Error);
        });
        try
        {
            if (File.Exists(AudioPreferencesPath))
                AudioPreferences = (System.Text.Json.JsonSerializer.Deserialize<DictationAudioPreferences>(File.ReadAllText(AudioPreferencesPath)) ?? new()).Validated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { AudioPreferencesError = Loc.T("Audio preferences could not be loaded. Defaults are in use: {0}", ex.Message); }
    }

    internal async Task InitializeAsync()
    {
        if (_disposed) return;
        // Retention loads the whole history; the model load need not wait for it.
        _ = ApplyHistoryRetentionAsync();
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
            // Prepare the device without starting capture, so key-down need not initialize it. It is opened
            // beside the model load, so a dictation started meanwhile does not wait for the device either.
            var warmUp = Task.Run(_audio.WarmUp);
            SetStatus(Loc.T("Loading model…"), DictationPhase.LoadingModel);
            await PluginRuntime.InitializeAsync();
            if (_disposed) return;
            try { if (Packages.Store.IsInstalled(LocalTranscriptionPlugin.PluginId)) await _transcriptionPlugin.InitializeAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { LocalPluginError = ex.Message; }
            if (_disposed) return;
            var prepared = await warmUp;
            await CtcVocabulary.SetEnabledAsync(Models.Enabled);
            LocalPluginError ??= Models.Error;
            SetStatus(ReadyStatus(prepared));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LocalPluginError = ex.Message;
            SetStatus(Loc.T("Local transcription unavailable: {0}", ex.Message));
        }
        finally { _gate.Release(); }
    }

    internal Task ToggleAsync() => SetRecordingAsync(null);
    // Interactive settings actions and spoken feedback are cancellable at recording startup. Admit the
    // hotkey while one is active so it can reach that cancellation before using a provider.
    internal bool CanStartFromShortcut => CanStartShortcut(IsReady);
    // A workflow with its own model can start while the selected model is unavailable; its model loads at the start.
    internal bool CanStartWorkflowModelShortcut(string? model) => CanStartShortcut(IsReady || !string.IsNullOrWhiteSpace(model));
    private bool CanStartShortcut(bool ready) => CanCaptureWhileModelLoads || CanStartSessionOperation
        && (!PluginRuntime.IsBusy || RecordingStarting is not null || SpokenFeedback.IsBusy || LocalLlmDownload.State.IsBusy) && (ready
#if DEBUG
        || CorrectionProbeEnabled
#endif
        );
#if DEBUG
    internal static bool CorrectionProbeEnabled => WinUIProfile.IsTestProfile && Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_CORRECTION_PROBE") == "1";
#endif
    internal nint TrayMenuHandle { get; set; }
    // Each start returns only its own task rejection; a concurrent or ignored start cannot see or erase another's.
    internal Task<string?> StartForApiAsync(AutomaticWorkflowSnapshot? workflow, Action<long> captureStarted) => StartWithRejectionAsync(workflow, captureStarted);
    internal Task<string?> StartAsync() => StartWithRejectionAsync(null, null);
    internal Task<string?> StartAsync(AutomaticWorkflowSnapshot workflow) => StartWithRejectionAsync(workflow, null);
    private async Task<string?> StartWithRejectionAsync(AutomaticWorkflowSnapshot? workflow, Action<long>? captureStarted)
    {
        string? rejection = null;
        await SetRecordingAsync(true, workflow, captureStarted, error => rejection = error);
        return rejection;
    }
    private bool _stopPending;
    // The stop itself waits for the model; remember where the user finished speaking.
    internal void MarkEarlyStop()
    {
        if (_disposed || !_audio.IsRecording || !_earlyCapture || _earlyStopSamples is not null) return;
        _earlyStopSamples = _audio.SampleCountAfterStopDrain;
        AppDiagnostics.Write("dictation.early-stop");
        Changed?.Invoke();
    }
    internal async Task StopAsync()
    {
        if (_disposed || !_audio.IsRecording || _stopPending) return;
        MarkEarlyStop();
        _stopPending = true;
        try { await SetRecordingAsync(false); }
        finally { _stopPending = false; }
    }
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
            if (_audio.IsRecording)
            {
                await _audio.StopRecordingAsync();
                AppDiagnostics.Write("dictation.canceled");
            }
            await StopCloudStreamAsync();
            _effects.End();
            await _livePreview.StopAsync();
            if (!_audio.IsRecording) await RestoreWorkflowModelAsync();
            // Like every ready status, the cancellation keeps the current microphone notice visible.
            SetStatus(_microphoneStatus = (_microphoneNotice = MicrophoneNotice()) is { } notice
                ? Loc.T("Shortcut cancelled · {0} ready · {1}", ActiveModelName, notice) : Loc.T("Shortcut cancelled · {0} ready", ActiveModelName));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SetStatus(Loc.T("Could not cancel recording: {0}", ex.Message)); }
        finally
        {
            // A canceled recording never reaches the stop path that ends its dictation context.
            if (!_audio.IsRecording) AppDiagnostics.EndDictation();
            _effects.End(); _gate.Release();
        }
    }

    private async Task SetRecordingAsync(bool? recording, AutomaticWorkflowSnapshot? workflow = null, Action<long>? captureStarted = null,
        Action<string>? rejected = null, bool adoptEarlyCapture = false)
    {
        // Shutdown stops a capture that is never adopted.
        if (_disposed) { if (adoptEarlyCapture) { _earlyCapture = false; _gate.Release(); } return; }
        // An API-started capture can bypass the input coordinator. Preserve explicit
        // stop intent while its setup holds the gate; never queue a new start.
        if (adoptEarlyCapture) { }
        else if (recording == false)
        {
            await _gate.WaitAsync();
            if (_disposed) { _gate.Release(); return; }
        }
        else if (!await _gate.WaitAsync(0))
        {
            // Return once the microphone is open, so a stop or cancel reaches the capture while the model
            // loads. API starts report their outcome to the caller and are not deferred.
            if (captureStarted is null && BeginEarlyCapture()) _ = AdoptEarlyCaptureAsync(workflow);
            return;
        }
#if DEBUG
        if (CorrectionProbeEnabled && !_audio.IsRecording)
        {
            try
            {
                await CorrectionLearning.Cancel();
                var target = NativeMethods.GetForegroundWindow();
                const string sample = "We use teh tool every day.";
                for (var attempt = 0; attempt < 80 && ModifiersHeld(); attempt++) await Task.Delay(25);
                var inserted = await _inserter.InsertAsync(sample, target);
                File.WriteAllText(WinUIProfile.DataPath("correction-probe.txt"), inserted ? "inserted" : "paste_failed");
                await _inserter.Restored;
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
        var preparingRecording = false;
        var finishingRecording = false;
        Task previousRecordingWork = Task.CompletedTask;
        try
        {
            if (adoptEarlyCapture)
            {
                // Every exit before startup completes discards the capture, like any other aborted start.
                preparingRecording = true;
                if (_earlyCancelled) return;
                if (!_audio.IsRecording)
                {
                    _microphoneNotice = MicrophoneNotice() ?? MicrophoneFailure.Generic;
                    SetStatus(_microphoneStatus = _microphoneNotice);
                    return;
                }
            }
            else if (recording.HasValue && recording.Value == _audio.IsRecording) return;
            if (!IsReady && !SwitchesWorkflowModel(workflow?.TranscriptionModel))
            {
                AppDiagnostics.Write("dictation.not-ready");
                SetStatus(Loc.T("No model is ready. Download a model or configure a cloud provider in plugin settings, then select it in Dictation."));
                return;
            }
            if (!_audio.IsRecording || adoptEarlyCapture)
            {
                if (!adoptEarlyCapture) _earlyStopSamples = null;
                // A dictation workflow's own model applies before its task and language are checked against it.
                if (!adoptEarlyCapture && SwitchesWorkflowModel(workflow?.TranscriptionModel) && BeginWorkflowModelCapture())
                {
                    adoptEarlyCapture = true;
                    preparingRecording = true;
                }
                if (await RejectWorkflowModelAsync(workflow?.TranscriptionModel, rejected)) return;
                var globalTaskAtStart = TranscriptionTaskPreferences.Current;
                _taskAtStart = globalTaskAtStart;
                // Unsupported tasks fail before microphone capture. When the model cannot translate
                // and an automatic rule decides the task, that rule is matched before capture too.
                // Otherwise matching follows capture, and its task is resolved below, before any
                // preview, streaming connection or final decoding.
                var matchRuleBeforeCapture = workflow is null && AutomaticRuleDecidesTask(globalTaskAtStart);
                var ruleMatched = false;
                if (!matchRuleBeforeCapture && (RejectTask(workflow?.SelectedTask, globalTaskAtStart, rejected)
                    || RejectLanguage(workflow?.InputLanguage, rejected))) return;
                _engineAtStart = ActiveEngineId;
                _modelAtStart = ActiveModelId;
                _originalField?.Dispose(); _originalField = null;
                _target = adoptEarlyCapture ? _earlyTarget : NativeMethods.GetForegroundWindow();
                NativeMethods.GetWindowThreadProcessId(_target, out var processId);
                _setupOutputAtStart = processId == Environment.ProcessId ? SetupTestTarget?.Invoke(_target) : null;
                if (_target == IntPtr.Zero || (processId == Environment.ProcessId && _setupOutputAtStart is null))
                {
                    AppDiagnostics.Write("dictation.no-target");
                    SetStatus(Loc.T("Focus a text field in another app, then press {0}.", Shortcut));
                    return;
                }
                // Capture the microphone before field inspection, workflow lookup or provider setup.
                // Signal prior work to stop now; only audible feedback must drain before capture.
                _operationCancellation.Begin();
                var downloadStopped = LocalLlmDownload.CancelAndDrainAsync();
                RecordingStarting?.Invoke();
                var correctionStopped = CorrectionLearning.Cancel();
                StopHistoryPlayback?.Invoke();
                var speechStopped = SpokenFeedback.CancelAndDrainAsync();
                var previewStopped = _livePreview.StopAsync();
                // An adopted capture has no earlier stream, and its buffered beginning must survive.
                var streamStopped = adoptEarlyCapture ? Task.CompletedTask : StopCloudStreamAsync();
                previousRecordingWork = Task.WhenAll(downloadStopped, correctionStopped, speechStopped, previewStopped, streamStopped);
                // A canceled TTS backend can still be playing until its drain completes.
                // Avoid recording its tail; model, provider and other cleanup remain deferred.
                await speechStopped;
                _operationCancellation.Token.ThrowIfCancellationRequested();
                if (_disposed) return;
                if (matchRuleBeforeCapture)
                {
                    _targetProcessId = processId;
                    _targetApp = TargetProcessName(processId);
                    if (_setupOutputAtStart is null) { await CaptureWorkflowAtStartAsync(); ruleMatched = true; }
                    if (_disposed) return;
                    if (RejectTask(ruleMatched ? WorkflowTranscriptionTask.SelectedTaskFor(_workflowAtStart) : null, globalTaskAtStart, rejected))
                    {
                        await previousRecordingWork;
                        return;
                    }
                }
                var preferences = AudioPreferences;
                _spokenFeedbackAtStart = preferences;
                _audio.WhisperModeEnabled = preferences.WhisperModeEnabled;
                _outputAtStart = OutputPreferences.Current;
                _textAtStart = TextPreferences.Current;
                _processorsAtStart = PluginRuntime.PostProcessors.ToArray();
                _languageAtStart = InheritedLanguage();
                _recoveryAtStart = RecoveryPreferences.Current;
                LivePreviewText = "";
                _hasConfirmedPreviewText = false;
                preparingRecording = true;
                // Buffer locally while context matching is pending, even if the global task
                // is Translate: the matched workflow may explicitly request Transcribe.
                if (!(LivePreviewEnabled && ModelSupportsLiveTranscription && UsesRegistryProvider &&
                    ActiveRegistryProvider is { SupportsStreaming: true })) _streamAudio.Reset();
                else if (!adoptEarlyCapture) _streamAudio.Begin();
                if (preferences.SilenceAutoStopEnabled)
                {
                    _silenceClock.Restart();
                    _silence = new(TimeSpan.FromSeconds(preferences.SilenceAutoStopSeconds));
                    _silenceTimer.Start();
                }
                if (!adoptEarlyCapture) AppDiagnostics.BeginDictation();
                AppDiagnostics.Write($"dictation.capture.{(adoptEarlyCapture ? "adopted" : "start")} engine={_engineAtStart} model={_modelAtStart} task={_taskAtStart} setup={_setupOutputAtStart is not null}");
                if (!adoptEarlyCapture) _audio.StartRecording(enableRecovery: _recoveryAtStart.Enabled && _recoveryAtStart.IsValid);
                if (!_audio.IsRecording)
                {
                    AppDiagnostics.Write("dictation.capture.failed");
                    await previousRecordingWork;
                    // Cache what is shown so a later recovery rewrites this failure.
                    _microphoneNotice = MicrophoneNotice() ?? MicrophoneFailure.Generic;
                    SetStatus(_microphoneStatus = _microphoneNotice);
                    return;
                }
                AppDiagnostics.Write("dictation.capture.active");
                PrepareTranscriptionModel();
                _targetProcessId = processId;
                _targetApp = TargetProcessName(processId);
                BeginApiDictationGeneration();
                captureStarted?.Invoke(ApiDictationGeneration);
                _started = DateTime.UtcNow - _audio.RecordingDuration;
                _lastDuration = TimeSpan.Zero;
                if (!adoptEarlyCapture) BeginRecordingFeedback(preferences);
                SetStatus(Loc.T("Recording · {0} to finish", Shortcut));
                await previousRecordingWork;
                _operationCancellation.Token.ThrowIfCancellationRequested();
                if (_disposed) return;
                AppDiagnostics.Write("dictation.start");
                if (OutputPreferences.Current is { AutoPaste: true, LockPasteToFocusedField: true } && _setupOutputAtStart is null)
                    _originalField = await OriginalDictationField.CaptureAsync(_target, processId, _operationCancellation.Token);
                if (_setupOutputAtStart is not null) { _targetHostAtStart = null; _workflowAtStart = null; }
                else if (workflow is null) { if (!ruleMatched) await CaptureWorkflowAtStartAsync(); }
                else { _targetHostAtStart = null; _workflowAtStart = workflow; }
                var resolvedTask = WorkflowTranscriptionTask.Resolve(WorkflowTranscriptionTask.SelectedTaskFor(_workflowAtStart), globalTaskAtStart, SupportsTranslation);
                // The overlay configured live preview for the global task when recording began.
                if (resolvedTask != _taskAtStart)
                {
                    _taskAtStart = resolvedTask; Changed?.Invoke();
                    AppDiagnostics.Write($"dictation.task-resolved task={resolvedTask}");
                }
                // The spoken language follows the same rule, before any streaming connection or preview decodes.
                string resolvedLanguage;
                try { resolvedLanguage = WorkflowSpokenLanguage.Resolve(WorkflowSpokenLanguage.SelectedLanguageFor(_workflowAtStart), _languageAtStart, LanguageChoices, DetectsLanguage); }
                catch (NotSupportedException) when (workflow is null && _workflowAtStart is { Error: null })
                {
                    // An automatic rule matched after capture began: keep the speech for review in the global language.
                    _workflowAtStart = AutomaticWorkflowSnapshot.Rejected(Loc.T("This workflow's spoken language is not supported by the current transcription model. Review your transcript; nothing was pasted."));
                    resolvedLanguage = _languageAtStart;
                    AppDiagnostics.Write("dictation.language-rejected");
                }
                if (resolvedLanguage != _languageAtStart)
                {
                    _languageAtStart = resolvedLanguage;
                    AppDiagnostics.Write($"dictation.language-resolved language={resolvedLanguage}");
                }
                _workflowActionAtStart = FindWorkflowAction(_workflowAtStart?.TargetActionPluginId);
                _workflowMemoryAtStart = FindWorkflowMemory(_workflowAtStart?.MemoryPluginId);
                _operationCancellation.Token.ThrowIfCancellationRequested();
                if (_disposed) return;
                NativeMethods.GetWindowThreadProcessId(_target, out var currentTargetProcessId);
                if (!DictationStartupTarget.IsValid(_target, NativeMethods.GetForegroundWindow(), TrayMenuHandle, processId, currentTargetProcessId))
                {
                    AppDiagnostics.Write("dictation.target-changed");
                    SetStatus(Loc.T("The target changed during recording setup. Focus your text field and try again."), DictationPhase.Idle);
                    return;
                }
                _dictionarySnapshot = Task.Run(() => DictationDictionarySnapshot.Load(DictationDictionarySnapshot.StoragePath));
                // A recording the user already finished needs neither a live stream nor a preview.
                if (_earlyStopSamples is null) await StartCloudStreamAsync();
                _operationCancellation.Token.ThrowIfCancellationRequested();
                if (_disposed) return;
                NativeMethods.GetWindowThreadProcessId(_target, out currentTargetProcessId);
                if (!DictationStartupTarget.IsValid(_target, NativeMethods.GetForegroundWindow(), TrayMenuHandle, processId, currentTargetProcessId))
                {
                    await StopCloudStreamAsync();
                    AppDiagnostics.Write("dictation.target-changed");
                    SetStatus(Loc.T("The target changed during recording setup. Focus your text field and try again."), DictationPhase.Idle);
                    return;
                }
                _snippetSnapshot = Task.Run(() => DictationSnippetSnapshot.Load(DictationSnippetSnapshot.StoragePath));
                _boostVocabulary = DictionaryBoostingPreferences.Load();
                _ctcAtStart = _taskAtStart == TranscriptionTask.Transcribe && !UsesRegistryProvider && TypeWhisper.Core.Models.ParakeetModels.IsParakeetTdt(Models.ActiveModelId) && CtcVocabulary.Enabled;
                if (_cloudStream is null && _earlyStopSamples is null && LivePreviewEnabled && SupportsLiveTranscription &&
                    (!UsesRegistryProvider || ActiveRegistryProvider is { SupportsPcm: true, SupportsLocalLivePreview: true } preview && PackageIsLocal(preview.PluginId)))
                    _livePreview.Start(() => _audio.HasSpeechEnergy ? _audio.GetCurrentBuffer() : null,
                        DecodeAsync,
                        text => { _hasConfirmedPreviewText |= !string.IsNullOrWhiteSpace(text); LivePreviewText = text; LivePreviewChanged?.Invoke(); },
                        error => { LivePreviewText = Loc.T("Live preview unavailable · final transcription will continue."); LivePreviewChanged?.Invoke(); System.Diagnostics.Debug.WriteLine(error); });
                AppDiagnostics.Write("dictation.startup.complete");
                preparingRecording = false;
                return;
            }

            finishingRecording = true;
            AppDiagnostics.Write("dictation.stop");
            StopSilenceMonitoring();
            _livePreview.Cancel();
            _lastDuration = _audio.RecordingDuration;
            var preGainPeakRms = _audio.PreGainPeakRmsLevel;
            SetStatus(Loc.T("Finishing recording…"), DictationPhase.Processing);
            var captured = await _audio.StopRecordingWithRecoveryAsync();
            recoveryLease = captured.RecoveryLease;
            var samples = captured.Samples;
            if (_earlyStopSamples is { } spoken)
            {
                _earlyStopSamples = null;
                // The microphone stayed open until the model was ready; keep only what was dictated.
                if (samples?.Length > spoken)
                {
                    samples = samples[..spoken];
                    AppDiagnostics.Write("dictation.early-trim");
                    _lastDuration = TimeSpan.FromSeconds(spoken / 16000.0);
                    await StopCloudStreamAsync();
                }
            }
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
            AppDiagnostics.Write($"dictation.captured decision={captureDecision} streaming={_cloudStream is not null}");
            if (samples is null || captureDecision == ShortClipCaptureDecision.TooShort)
            { SetStatus(Loc.T("Recording was too short. Hold the shortcut a little longer.")); return; }
            if (captureDecision == ShortClipCaptureDecision.NoSpeech)
            { SetStatus(Loc.T("No speech energy detected. Speak closer to the microphone or enable Recognize short, quiet clips.")); return; }
            SetStatus(Loc.T("Transcribing with {0}…", ActiveModelName), DictationPhase.Processing);
            var streamedText = _cloudStream is null ? null : await _cloudStream.FinishAsync(samples.Length);
            _operationCancellation.Token.ThrowIfCancellationRequested();
            var decoded = streamedText is null
                ? await DecodeFinalAsync(ShortClipCapturePolicy.PadForFinalDecode(samples))
                : (Text: streamedText, Timings: Array.Empty<VocabularyTokenTiming>(), DetectedLanguage: _cloudStream?.DetectedLanguage, NoSpeechProbability: (float?)null);
            _operationCancellation.Token.ThrowIfCancellationRequested();
            var rawText = decoded.Text;
            AppDiagnostics.Write($"dictation.transcribed empty={string.IsNullOrWhiteSpace(rawText)}");
            if (FinalSpeechPolicy.ShouldReject(rawText, decoded.NoSpeechProbability,
                _hasConfirmedPreviewText, _textAtStart.TranscribeShortQuietClipsAggressively))
            { AppDiagnostics.Write("dictation.no-speech"); SetStatus(Loc.T("No speech recognized. Ready to try again.")); return; }
            // Empty final output does not reuse preview text or its unrelated token timings.
            if (string.IsNullOrWhiteSpace(rawText)) { SetStatus(Loc.T("No speech recognized. Ready to try again.")); return; }
            if (_setupOutputAtStart is { } setupOutput)
            {
                // A setup sample never pastes into another app or creates a history entry.
                _operationCancellation.Token.ThrowIfCancellationRequested();
                if (_disposed) return;
                setupOutput(rawText);
                SetStatus(Loc.T("Setup dictation completed."), DictationPhase.Completed);
                return;
            }
            var dictionary = _dictionarySnapshot is null ? null : await _dictionarySnapshot;
            var refinedText = rawText;
            var recordingId = Guid.NewGuid();
            if (_ctcAtStart && dictionary is not null && CtcVocabulary.Enabled)
            {
                SetStatus(Loc.T("Checking vocabulary with CTC…"), DictationPhase.Processing);
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
            AppDiagnostics.Write("delivery.begin");
            var outcome = await delivery.DeliverAsync(record, processed.WorkflowError is null ? _outputAtStart : _outputAtStart with { AutoPaste = false },
                () => OutputPreferences.Current, async () =>
                {
                    // Recheck after waiting: settings can change while modifiers are held.
                    for (var attempt = 0; attempt < 40 && ModifiersHeld(); attempt++) await Task.Delay(25, _operationCancellation.Token);
                    _operationCancellation.Token.ThrowIfCancellationRequested();
                    if (_disposed || !_outputAtStart.RestrictedBy(OutputPreferences.Current).AutoPaste ||
                        ModifiersHeld()) { AppDiagnostics.Write("delivery.blocked-settings-modifiers-or-disposed"); return false; }
                    var lockField = _outputAtStart.RestrictedBy(OutputPreferences.Current).LockPasteToFocusedField;
                    if (lockField)
                    {
                        if (_originalField is null) { AppDiagnostics.Write("delivery.no-captured-field"); return false; }
                        if (OutputPreferences.Current.LockPasteToFocusedField &&
                            !await _originalField.RestoreAsync(_operationCancellation.Token)) { AppDiagnostics.Write("delivery.restore-failed"); return false; }
                        if (!_originalField.IsCurrent()) { AppDiagnostics.Write("delivery.field-not-current"); return false; }
                    }
                    if (NativeMethods.GetForegroundWindow() != _target) { AppDiagnostics.Write("delivery.target-not-foreground"); return false; }
                    // Spacing and casing for the cursor position only reach the target field; history, API and
                    // actions keep the final text. A field without readable context gets the text unchanged.
                    var context = _textAtStart.AppAwareFormattingEnabled ? await InsertionContextReader.ReadAsync(_target) : null;
                    _operationCancellation.Token.ThrowIfCancellationRequested();
                    AppDiagnostics.Write($"delivery.context available={context is not null}");
                    // Casing follows the language of the pasted text: English after native translation, unknown
                    // after a translation workflow, otherwise the dictated language.
                    var outputLanguage = _workflowAtStart?.Translates == true ? null
                        : _taskAtStart == TranscriptionTask.Translate ? "en" : record.Language;
                    var pasted = DictationInsertionText.ForPaste(text, context, _textAtStart.StripFinalPeriodFromStandaloneValues, outputLanguage);
                    // A dictation of only a spoken line break must still insert it.
                    if (pasted.Length == 0) pasted = text;
                    var inserted = await _inserter.InsertAsync(pasted, _target, () =>
                        !_disposed && !_operationCancellation.Token.IsCancellationRequested &&
                        _outputAtStart.RestrictedBy(OutputPreferences.Current).AutoPaste &&
                        (!_outputAtStart.RestrictedBy(OutputPreferences.Current).LockPasteToFocusedField || _originalField?.IsCurrent() == true));
                    if (inserted && !_disposed && !_operationCancellation.Token.IsCancellationRequested && record.Status == TranscriptionRecordStatus.Succeeded)
                        _ = ObserveCorrectionsAfterPasteAsync(pasted, _target, _operationCancellation.Token);
                    return inserted;
                }, _operationCancellation.Token, samples, 16000,
                string.IsNullOrWhiteSpace(_workflowAtStart?.TargetActionPluginId) ? null : ct => ExecuteWorkflowActionAsync(
                    _workflowActionAtStart, text, new TypeWhisper.PluginSDK.Models.ActionContext(record.AppName, record.AppProcessName,
                        record.AppUrl, record.Language, rawText), ct));
            preserveRecovery = outcome.Failed || record.Status != TranscriptionRecordStatus.Succeeded;
            AppDiagnostics.Write(outcome.NeedsReview ? "delivery.review" : "delivery.completed");
            if (_disposed) return;
            if (!outcome.Committed) _operationCancellation.Token.ThrowIfCancellationRequested();
            if (_lastCompletedDictation.TryPublish(outcome, outcome.Committed ? CancellationToken.None : _operationCancellation.Token)) PublishApiDictationRecord(outcome.Record);
            LastUnsavedText = outcome.Saved ? null : text;
            if (!outcome.NeedsReview) LivePreviewText = text;
            // A blocked or failed paste leaves the text on the clipboard, as on macOS, and says so where the
            // user looks while dictating. The overlay only claims a copy that succeeded.
            var pasteFailed = outcome.ReviewReason == DictationReviewReason.PasteFailed;
            if (pasteFailed) outcome = outcome with { CopiedToClipboard = ClipboardText.TrySet(outcome.Record.FinalText) };
            var message = !pasteFailed ? outcome.Message : outcome.CopiedToClipboard
                ? Loc.T("Not inserted. The text is on the clipboard.") : Loc.T("Not inserted. Copy the text from the review window.");
            SetStatus(snippetError is null ? message : message + " · " + snippetError,
                pasteFailed ? outcome.CopiedToClipboard ? DictationPhase.Copied : DictationPhase.Error
                    : outcome.NeedsReview ? DictationPhase.Idle : DictationPhase.Completed);
            if (outcome.NeedsReview) ReviewRequested?.Invoke(outcome);
            else
            {
                OutputCompleted?.Invoke(recordingId);
                if (outcome.StorageWarning is not null) OutputWarning?.Invoke(outcome.Message);
            }
            ReadCompletedDictation(record, outcome, processed.Warnings.Count == 0);

        }
        catch (Exception ex) when (ex is not OutOfMemoryException && _operationCancellation.Token.IsCancellationRequested)
        {
            AppDiagnostics.Write("dictation.canceled");
            preserveRecovery = _disposed && !preparingRecording;
            StopSilenceMonitoring();
            await StopRecoveryCaptureAsync(preserve: preserveRecovery);
            _effects.End();
            await _livePreview.StopAsync();
            if (!_disposed) SetStatus(Loc.T("Dictation canceled. Ready to try again."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.Write("dictation.failed", ex);
            preserveRecovery = !preparingRecording;
            StopSilenceMonitoring();
            _livePreview.Cancel();
            try { await StopRecoveryCaptureAsync(preserve: preserveRecovery); }
            catch (Exception stopError) when (stopError is not OutOfMemoryException)
            { System.Diagnostics.Debug.WriteLine(stopError); }
            finally { _effects.End(); await _livePreview.StopAsync(); }
            _sounds.PlayErrorSound();
            SetStatus(Loc.T("Dictation failed: {0}", ex.Message), DictationPhase.Error);
        }
        finally
        {
            try
            {
                if (adoptEarlyCapture) _earlyCapture = false;
                if (preparingRecording)
                {
                    StopSilenceMonitoring();
                    await StopRecoveryCaptureAsync(preserve: false);
                    _effects.End();
                    try { await previousRecordingWork; }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex); }
                    // Status may have been published while early capture was still active.
                    // Refresh the tray and overlay after discarding an aborted startup.
                    if (!_disposed) Changed?.Invoke();
                }
                await FinishRecoveryLeaseAsync(recoveryLease, preserveRecovery || _disposed);
                if (!_audio.IsRecording) { _originalField?.Dispose(); _originalField = null; _setupOutputAtStart = null; _effects.End(); await StopCloudStreamAsync(); await RestoreWorkflowModelAsync(); }
                if (finishingRecording) PublishMicrophoneNoticeAfterDictation();
                if (!_audio.IsRecording) AppDiagnostics.EndDictation();
            }
            finally { _gate.Release(); }
        }
    }

    internal string? LastUnsavedText { get; private set; }
    // Observation needs the pasted text in the field, which the target has consumed once
    // the clipboard is restored. A newer dictation replaces the operation token and wins.
    private async Task ObserveCorrectionsAfterPasteAsync(string text, IntPtr target, CancellationToken operation)
    {
        await _inserter.Restored;
        if (_disposed || _operationCancellation.Token != operation || operation.IsCancellationRequested) return;
        try { CorrectionLearning.Observe(text, target); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { System.Diagnostics.Trace.TraceWarning("Correction learning could not start: {0}", ex.GetType().Name); }
    }
    private async Task<string> DecodeAsync(float[] samples) => (await DecodeFinalAsync(samples, false)).Text;
    private Task<(string Text, VocabularyTokenTiming[] Timings, string? DetectedLanguage, float? NoSpeechProbability)> DecodeFinalAsync(float[] samples, bool includeTimings = true) =>
        UsesRegistryProvider ? DecodeRegistryAsync(samples)
            : _transcriptionPlugin.DecodeAsync(samples, includeTimings, _taskAtStart == TranscriptionTask.Translate, _operationCancellation.Token, _languageAtStart);
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

    private static bool ModifiersHeld() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0);

    public ValueTask DisposeAsync() => new(ShutdownAsync());
}
