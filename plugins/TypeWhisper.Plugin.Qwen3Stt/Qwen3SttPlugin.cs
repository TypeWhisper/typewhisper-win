using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Qwen3Stt;

public sealed class Qwen3SttPlugin : ITranscriptionEnginePlugin
{
    private const string SelectedModelSettingName = "selectedModel";
    private const string HuggingFaceTokenSecretName = "hf-token";

    private readonly HttpClient _httpClient;
    private readonly Qwen3ModelStore _modelStore;
    private readonly IQwen3TranscriberFactory _transcriberFactory;
    private IPluginHostServices? _host;
    private IQwen3Transcriber? _transcriber;
    private string? _selectedModelId;
    private string? _loadedModelId;
    private Qwen3ModelDefinition? _loadedModel;
    private string? _loadedModelDirectory;
    private string? _huggingFaceToken;

    public Qwen3SttPlugin()
        : this(new HttpClient(), new Qwen3OnnxTranscriberFactory())
    {
    }

    internal Qwen3SttPlugin(HttpClient httpClient)
        : this(httpClient, new Qwen3OnnxTranscriberFactory())
    {
    }

    internal Qwen3SttPlugin(HttpClient httpClient, IQwen3TranscriberFactory transcriberFactory)
    {
        _httpClient = httpClient;
        _transcriberFactory = transcriberFactory;
        _modelStore = new Qwen3ModelStore(httpClient, () => _huggingFaceToken, Log);
    }

    public string PluginId => Qwen3ModelCatalog.PluginId;
    public string PluginName => "Qwen3 ASR (ONNX)";
    public string PluginVersion => "1.1.0";

    public string ProviderId => "qwen3-stt";
    public string ProviderDisplayName => "Qwen3 ASR (ONNX)";
    public bool IsConfigured => _transcriber is not null && _loadedModelId is not null;
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = Qwen3ModelCatalog.ToPluginModels();
    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => false;
    public bool SupportsModelDownload => true;
    public IReadOnlyList<string> SupportedLanguages => Qwen3LanguageMapper.SupportedLanguageCodes;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _huggingFaceToken = await host.LoadSecretAsync(HuggingFaceTokenSecretName);
        var selected = Qwen3ModelCatalog.NormalizeModelId(host.GetSetting<string>(SelectedModelSettingName));
        _selectedModelId = selected;
        host.SetSetting(SelectedModelSettingName, selected);
        host.NotifyCapabilitiesChanged();
        Log(PluginLogLevel.Info, $"Activated Qwen3 ASR ONNX plugin (selectedModel={selected})");
    }

    public Task DeactivateAsync()
    {
        _transcriber?.Dispose();
        _transcriber = null;
        _loadedModelId = null;
        _loadedModel = null;
        _loadedModelDirectory = null;
        _host = null;
        return Task.CompletedTask;
    }

    public UserControl? CreateSettingsView()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock
        {
            Text = "Hugging Face token (optional)",
            Margin = new Thickness(0, 0, 0, 4),
        });

        var tokenBox = new PasswordBox { MaxLength = 300 };
        if (!string.IsNullOrWhiteSpace(_huggingFaceToken))
            tokenBox.Password = _huggingFaceToken;

        var status = new TextBlock { Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
        var save = new Button
        {
            Content = "Save",
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        save.Click += async (_, _) =>
        {
            var token = tokenBox.Password.Trim();
            _huggingFaceToken = string.IsNullOrWhiteSpace(token) ? null : token;
            if (_host is not null)
            {
                if (_huggingFaceToken is null)
                    await _host.DeleteSecretAsync(HuggingFaceTokenSecretName);
                else
                    await _host.StoreSecretAsync(HuggingFaceTokenSecretName, _huggingFaceToken);
            }

            status.Text = "Saved";
        };

        panel.Children.Add(tokenBox);
        panel.Children.Add(save);
        panel.Children.Add(status);
        return new UserControl { Content = panel };
    }

    public void SelectModel(string modelId)
    {
        var normalized = Qwen3ModelCatalog.NormalizeModelId(modelId);
        _ = Qwen3ModelCatalog.GetModel(normalized);
        _selectedModelId = normalized;
        _host?.SetSetting(SelectedModelSettingName, normalized);
    }

    public bool IsModelDownloaded(string modelId)
    {
        if (_host is null)
            return false;
        return _modelStore.IsModelDownloaded(_host.PluginDataDirectory, modelId);
    }

    public Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        if (_host is null)
            throw new InvalidOperationException("Plugin is not activated.");
        return _modelStore.DownloadModelAsync(_host.PluginDataDirectory, modelId, progress, ct);
    }

    public Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        if (_host is null)
            throw new InvalidOperationException("Plugin is not activated.");

        var model = Qwen3ModelCatalog.GetModel(modelId);
        var modelDir = _modelStore.GetModelDirectory(_host.PluginDataDirectory, model.Id);
        if (!model.RequiredFiles.All(file => File.Exists(Path.Combine(modelDir, file))))
            throw new FileNotFoundException($"Model files not found for {model.Id}. Download the model first.");

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            _transcriber?.Dispose();
            _transcriber = _transcriberFactory.Load(modelDir, model);
            _loadedModelId = model.Id;
            _loadedModel = model;
            _loadedModelDirectory = modelDir;
            SelectModel(model.Id);
            _host?.NotifyCapabilitiesChanged();
            Log(PluginLogLevel.Info, $"Loaded {model.DisplayName}");
        }, ct);
    }

    public Task UnloadModelAsync()
    {
        _transcriber?.Dispose();
        _transcriber = null;
        _loadedModelId = null;
        _loadedModel = null;
        _loadedModelDirectory = null;
        _host?.NotifyCapabilitiesChanged();
        return Task.CompletedTask;
    }

    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new NotSupportedException("Qwen3 ASR ONNX does not support translation.");
        if (_transcriber is null)
            throw new InvalidOperationException("No Qwen3 ASR model loaded. LoadModelAsync must be called first.");

        return Task.Run(() =>
        {
            var transcriber = _transcriber;
            try
            {
                return ToPluginResult(transcriber.Transcribe(wavAudio, language, prompt, ct));
            }
            catch (Exception ex) when (ShouldRetryOnCpu(transcriber, ex))
            {
                Log(PluginLogLevel.Warning, $"DirectML inference failed; retrying Qwen3 ASR on CPU. {ex.Message}");
                var cpuTranscriber = _transcriberFactory.LoadCpu(_loadedModelDirectory!, _loadedModel!);
                _transcriber = cpuTranscriber;
                transcriber.Dispose();
                return ToPluginResult(cpuTranscriber.Transcribe(wavAudio, language, prompt, ct));
            }
        }, ct);
    }

    public void Dispose()
    {
        _transcriber?.Dispose();
        _httpClient.Dispose();
    }

    private void Log(PluginLogLevel level, string message)
    {
        _host?.Log(level, message);
        System.Diagnostics.Debug.WriteLine($"[Qwen3Stt] {message}");
    }

    private bool ShouldRetryOnCpu(IQwen3Transcriber transcriber, Exception ex) =>
        transcriber.UsesDirectMl
        && _loadedModel is not null
        && _loadedModelDirectory is not null
        && IsRecoverableDirectMlException(ex);

    private static bool IsRecoverableDirectMlException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            var text = current.ToString();
            if (text.Contains("DmlExecutionProvider", StringComparison.OrdinalIgnoreCase)
                || text.Contains("DirectML", StringComparison.OrdinalIgnoreCase)
                || (text.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase)
                    && text.Contains("80070057", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static PluginTranscriptionResult ToPluginResult(Qwen3Transcription result) =>
        new(
            result.Text,
            result.DetectedLanguage,
            result.DurationSeconds,
            NoSpeechProbability: null);
}
