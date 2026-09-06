using System.Runtime.InteropServices;
using TypeWhisper.Core.Interfaces;
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
    internal bool LivePreviewEnabled { get; set; } = true;
    internal string LivePreviewText { get; private set; } = "";
    internal event Action? LivePreviewChanged;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _silenceTimer;
    private volatile SilenceAutoStop? _silence;
    private readonly System.Diagnostics.Stopwatch _silenceClock = new();
    internal DictationAudioPreferences AudioPreferences { get; private set; } = new();
    internal string? AudioPreferencesError { get; private set; }
    private static readonly string AudioPreferencesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeWhisper-WinUI-DevUserData", "audio.json");

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
    private readonly IHistoryService _history;
    private readonly ClipboardTextInserter _inserter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LocalTranscriptionPlugin _transcriptionPlugin;
    internal LocalTranscriptionPlugin Models => _transcriptionPlugin;
    internal CloudTranscriptionPlugin Groq { get; }
    private readonly VocabularyHostServices _selection = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeWhisper-WinUI-DevUserData", "Dictation"));
    internal bool UsesGroq { get; private set; }
    internal string ActiveModelName => UsesGroq ? "Groq · " + Groq.ModelName : Models.ActiveModelName;
    internal string? ActiveModelId => UsesGroq ? Groq.ModelId : Models.ActiveModelId;
    internal string ActiveChoiceId => UsesGroq ? "groq:" + Groq.ModelId : Models.ActiveModelId ?? "";
    internal string ActiveProviderId => UsesGroq ? "groq" : "local";
    internal IReadOnlyList<DictationProviderOption> DictationProviders => new DictationProviderOption[]
    {
        new("local", LocalTranscriptionPlugin.PluginId, "NVIDIA Parakeet", Models.Enabled, true, false,
            Models.ActiveModelId, Models.Models.Select(model => new DictationModelOption(model.Model.Id, model.Model.DisplayName, model.Downloaded)).ToArray()),
        new("groq", CloudTranscriptionPlugin.PluginId, "Groq", Groq.Enabled, Groq.Ready, true,
            Groq.ModelId, Groq.Models.Select(model => new DictationModelOption(model.Id, model.DisplayName, Groq.Ready)).ToArray())
    }.Where(provider => Packages.Store.IsInstalled(provider.PluginId)).ToArray();
    internal Task<string?> SelectProviderModelAsync(string providerId, string modelId) => providerId switch
    {
        "local" => SelectModelAsync(modelId),
        "groq" => SelectGroqModelAsync(modelId),
        _ => Task.FromResult<string?>("This transcription provider is not available.")
    };
    internal IReadOnlyList<string> SupportedLanguages => UsesGroq ? Groq.Languages : Models.SupportedLanguages;
    internal string Language => UsesGroq ? Groq.Language : Models.Language;
    internal bool CanChangeProvider => !_disposed && !IsRecording && _phase is not (DictationPhase.Processing or DictationPhase.Configuring) && !Groq.Busy;
    internal bool CanSelectModel => !_disposed && !IsRecording && _phase is not (DictationPhase.Processing or DictationPhase.Configuring) && !Models.Busy && Models.Enabled && !Groq.Busy;
    private IntPtr _target;
    private DateTime _started;
    private bool _disposed;
    private DictationPhase _phase;
    private TimeSpan _lastDuration;
    private string _targetApp = "";
    private uint _targetProcessId;
    internal DictationOverlayState OverlayState => new(_phase,
        _audio.IsRecording ? _audio.RecordingDuration : _lastDuration, Status, _targetApp, _targetProcessId);
    internal string Status { get; private set; } = "Loading local transcription plugin…";
    internal string Shortcut { get; set; } = "Ctrl+Shift+F9";
    internal bool IsRecording => _audio.IsRecording;
    internal bool IsReady => UsesGroq ? Groq.Ready : _transcriptionPlugin.Ready;
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
            UsesGroq = false;
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
        UsesGroq = true;
    });
    internal Task<string?> SelectDictationModelAsync(string id) => id.StartsWith("groq:", StringComparison.Ordinal)
        ? SelectGroqModelAsync(id[5..]) : SelectModelAsync(id);
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
        if (!(UsesGroq ? CanChangeProvider && Groq.Ready : CanSelectModel) || !_gate.Wait(0)) return "Finish dictation before changing the language.";
        try { if (UsesGroq) Groq.SelectLanguage(language); else Models.SelectLanguage(language); return null; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "Could not save language: " + ex.Message; }
        finally { _gate.Release(); }
    }
    internal float CurrentLevel => _audio.CurrentRmsLevel;
    internal event Action? Changed;
    private List<MicrophonePriorityItem> _microphones = [];
    private static readonly string MicrophonePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeWhisper-WinUI-DevUserData", "microphone.json");
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
        var groqDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeWhisper-WinUI-DevUserData", "PluginData", CloudTranscriptionPlugin.PluginId);
        var groqHost = new VocabularyHostServices(groqDirectory, secrets: new WindowsPluginSecretStore(groqDirectory));
        Groq = new(groqHost, async () =>
        {
            var package = await PortablePluginPackage.LoadAsync(Packages.Store.Resolve(CloudTranscriptionPlugin.PluginId), groqHost, LocalCtcVocabulary.HostVersion);
            if (package.Plugin is ITranscriptionEnginePlugin engine && package.Plugin is IApiKeyPlugin configuration)
                return new CloudTranscriptionLease(engine, configuration, package);
            await package.DisposeAsync();
            throw new NotSupportedException("This cloud plugin does not provide transcription and API key settings.");
        });
        _history = history;
        _inserter = new(owner);
        _effects = new(_ducking, new MediaPauseService());
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
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
            UsesGroq = _selection.GetSetting<string>("Provider") == "groq";
            try { if (Packages.Store.IsInstalled(CloudTranscriptionPlugin.PluginId)) await Groq.InitializeAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine("Groq initialization failed: " + CloudTranscriptionPlugin.DescribeError(ex)); }
            try { if (Packages.Store.IsInstalled(LocalTranscriptionPlugin.PluginId)) await _transcriptionPlugin.InitializeAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { LocalPluginError = ex.Message; }
            // Prepare the device without starting capture, so key-down need not initialize it.
            var prepared = _audio.WarmUp();
            await CtcVocabulary.SetEnabledAsync(Models.Enabled);
            LocalPluginError ??= Models.Error;
            SetStatus(!IsReady ? UsesGroq ? Groq.Error ?? "Configure or enable Groq in Plugins, or select a local model." : !Models.Enabled ? "Local transcription plugin disabled" : Models.Error ?? "Download a model in plugin settings, then select it in Dictation." : prepared ? $"{ActiveModelName} ready · {Shortcut} to dictate" : $"{ActiveModelName} ready · microphone preparation failed; check the device");
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
                _audio.StartRecording(enableRecovery: false);
                if (!_audio.IsRecording) { SetStatus("Microphone could not start. Check the input device and microphone access."); return; }
                _dictionarySnapshot = Task.Run(() => DictationDictionarySnapshot.Load(DictationDictionarySnapshot.StoragePath));
                _snippetSnapshot = Task.Run(() => DictationSnippetSnapshot.Load(DictationSnippetSnapshot.StoragePath));
                _boostVocabulary = DictionaryBoostingPreferences.Load();
                _ctcAtStart = !UsesGroq && Models.ActiveModelId == "parakeet-tdt-0.6b" && CtcVocabulary.Enabled;
                LivePreviewText = "";
                if (LivePreviewEnabled && !UsesGroq)
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
            var text = dictionary is null ? refinedText : await Task.Run(() => dictionary.Apply(refinedText, boostVocabulary));
            var snippets = _snippetSnapshot is null ? null : await _snippetSnapshot;
            string? snippetError = null;
            if (snippets is not null)
            {
                string? clipboardText = null;
                if (await Task.Run(() => snippets.NeedsClipboard(text)))
                {
                    try
                    {
                        var clipboard = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                        clipboardText = clipboard.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)
                            ? await clipboard.GetTextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)) : "";
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    { System.Diagnostics.Debug.WriteLine("Snippet clipboard text unavailable: " + ex.Message); }
                }
                var input = text;
                var expansion = await Task.Run(() => snippets.Apply(input, clipboardText is null ? null : () => clipboardText));
                text = expansion.Text;
                snippetError = expansion.Error;
            }
            if (_disposed) return;
            var record = new TranscriptionRecord
            {
                Id = recordingId.ToString(), Timestamp = _started, CreatedAt = DateTime.UtcNow,
                RawText = rawText, FinalText = text, DurationSeconds = samples.Length / 16000.0,
                EngineUsed = UsesGroq ? "groq" : "sherpa-onnx", ModelUsed = ActiveModelId, TranscriptionTaskUsed = "transcribe"
            };
            await _history.EnsureLoadedAsync();
            if (!_history.TryAddRecord(record))
            {
                LastUnsavedText = text;
                SetStatus("History could not be saved. Result retained in memory; no text inserted.");
                return;
            }
            // Never change applications or send Enter automatically. If focus
            // moved during decoding, keep the result in History for manual use.
            for (var attempt = 0; attempt < 40 && ModifiersHeld(); attempt++) await Task.Delay(25);
            if (ModifiersHeld() || GetForegroundWindow() != _target)
            {
                SetStatus("Saved to History · target changed, text was not inserted");
                return;
            }
            try
            {
                var inserted = await _inserter.InsertAsync(text, _target);
                var completion = inserted ? $"Paste sent and saved · {ActiveModelName} ready" : "Saved to History · paste not completed; copy the result manually";
                SetStatus(snippetError is null ? completion : completion + " · " + snippetError);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                SetStatus("Saved to History · clipboard operation failed; check the target and clipboard: " + ex.Message, DictationPhase.Error);
            }
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
    private Task<(string Text, VocabularyTokenTiming[] Timings)> DecodeFinalAsync(float[] samples, bool includeTimings = true) =>
        UsesGroq ? Groq.DecodeAsync(samples) : _transcriptionPlugin.DecodeAsync(samples, includeTimings);
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
        _ = CtcVocabulary.DisposeAsync();
        _ = Groq.DisposeAsync();
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
