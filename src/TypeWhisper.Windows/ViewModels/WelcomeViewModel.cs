using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Cloud;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.Windows.ViewModels;

public partial class WelcomeViewModel : ObservableObject
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioRecordingService _audio;

    [ObservableProperty] private int _currentStep; // 0=Model, 1=Cloud, 2=Mic, 3=Hotkey, 4=Done
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private string _selectedModelId = "parakeet-tdt-0.6b";
    [ObservableProperty] private float _micLevel;
    [ObservableProperty] private bool _micWorking;
    [ObservableProperty] private string _toggleHotkey;
    [ObservableProperty] private string _pushToTalkHotkey;

    // Cloud provider properties
    [ObservableProperty] private string _groqApiKey = "";
    [ObservableProperty] private string _openAiApiKey = "";
    [ObservableProperty] private string? _cloudTestResult;
    [ObservableProperty] private bool _isTestingKey;

    public ObservableCollection<MicrophoneItem> Microphones { get; } = [];
    public IReadOnlyList<CloudProviderBase> CloudProviders { get; }
    public event EventHandler? Completed;

    public WelcomeViewModel(
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioRecordingService audio)
    {
        _modelManager = modelManager;
        _settings = settings;
        _audio = audio;

        _toggleHotkey = settings.Current.ToggleHotkey;
        _pushToTalkHotkey = settings.Current.PushToTalkHotkey;
        CloudProviders = [.. modelManager.CloudProviders];

        // Load existing API keys if any
        _groqApiKey = ApiKeyProtection.Decrypt(settings.Current.GroqApiKey ?? "");
        _openAiApiKey = ApiKeyProtection.Decrypt(settings.Current.OpenAiApiKey ?? "");

        RefreshMicrophones();
    }

    [RelayCommand]
    private async Task DownloadModel()
    {
        IsDownloading = true;
        DownloadStatus = "Herunterladen...";

        _modelManager.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ModelManagerService.GetStatus))
            {
                var status = _modelManager.GetStatus(SelectedModelId);
                if (status.Type == ModelStatusType.Downloading)
                {
                    DownloadProgress = status.Progress;
                    DownloadStatus = $"Download: {status.Progress:P0}";
                }
                else if (status.Type == ModelStatusType.Loading)
                {
                    DownloadStatus = "Modell laden...";
                }
            }
        };

        try
        {
            await _modelManager.DownloadAndLoadModelAsync(SelectedModelId);
            DownloadStatus = "Fertig!";

            // Save selected model
            _settings.Save(_settings.Current with { SelectedModelId = SelectedModelId });
        }
        catch (Exception ex)
        {
            DownloadStatus = $"Fehler: {ex.Message}";
            return;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private async Task TestCloudKey(string providerId)
    {
        var apiKey = providerId switch
        {
            "groq" => GroqApiKey,
            "openai" => OpenAiApiKey,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            CloudTestResult = "Bitte zuerst einen API-Key eingeben";
            return;
        }

        var provider = CloudProviders.FirstOrDefault(p => p.Id == providerId);
        if (provider is null) return;

        IsTestingKey = true;
        CloudTestResult = "Teste...";
        try
        {
            using var httpClient = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{provider.BaseUrl}/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var response = await httpClient.SendAsync(request);

            CloudTestResult = response.IsSuccessStatusCode
                ? $"{provider.DisplayName}: API-Key gültig!"
                : $"{provider.DisplayName}: Ungültiger Key (HTTP {(int)response.StatusCode})";
        }
        catch (Exception ex)
        {
            CloudTestResult = $"Verbindungsfehler: {ex.Message}";
        }
        finally
        {
            IsTestingKey = false;
        }
    }

    public void SaveApiKey(string providerId, string password)
    {
        if (providerId == "groq")
            GroqApiKey = password;
        else if (providerId == "openai")
            OpenAiApiKey = password;
    }

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep < 4)
            CurrentStep++;

        if (CurrentStep == 1)
            SaveCloudKeys();
        else if (CurrentStep == 2)
            StartMicTest();
        else if (CurrentStep == 4)
            Finish();
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep > 0)
        {
            if (CurrentStep == 2)
                StopMicTest();
            CurrentStep--;
        }
    }

    [RelayCommand]
    private void Skip()
    {
        Finish();
    }

    private void SaveCloudKeys()
    {
        var groqEncrypted = string.IsNullOrWhiteSpace(GroqApiKey) ? null : ApiKeyProtection.Encrypt(GroqApiKey);
        var openAiEncrypted = string.IsNullOrWhiteSpace(OpenAiApiKey) ? null : ApiKeyProtection.Encrypt(OpenAiApiKey);

        _settings.Save(_settings.Current with
        {
            GroqApiKey = groqEncrypted,
            OpenAiApiKey = openAiEncrypted
        });
    }

    private void StartMicTest()
    {
        _audio.AudioLevelChanged += OnMicLevel;
        _audio.WarmUp();
        _audio.StartRecording();
    }

    private void StopMicTest()
    {
        _audio.AudioLevelChanged -= OnMicLevel;
        _audio.StopRecording();
    }

    private void OnMicLevel(object? sender, AudioLevelEventArgs e)
    {
        MicLevel = e.RmsLevel;
        if (e.RmsLevel > 0.01f)
            MicWorking = true;
    }

    private void RefreshMicrophones()
    {
        Microphones.Clear();
        Microphones.Add(new MicrophoneItem(null, "Standard"));
        foreach (var (number, name) in AudioRecordingService.GetAvailableDevices())
            Microphones.Add(new MicrophoneItem(number, name));
    }

    private void Finish()
    {
        StopMicTest();
        SaveCloudKeys();

        // Save hotkey settings
        _settings.Save(_settings.Current with
        {
            ToggleHotkey = ToggleHotkey,
            PushToTalkHotkey = PushToTalkHotkey
        });

        Completed?.Invoke(this, EventArgs.Empty);
    }
}
