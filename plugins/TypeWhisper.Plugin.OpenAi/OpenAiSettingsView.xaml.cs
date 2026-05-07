using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.OpenAi;

public partial class OpenAiSettingsView : UserControl
{
    private readonly OpenAiPlugin _plugin;
    private bool _isInitializing;
    private readonly AuthModeOption[] _authModeOptions;
    private readonly ReasoningEffortOption[] _reasoningEffortOptions;

    public OpenAiSettingsView(OpenAiPlugin plugin)
    {
        _plugin = plugin;
        _authModeOptions =
        [
            new(OpenAiAuthMode.ApiKey, L("Settings.AuthModeApiKey")),
            new(OpenAiAuthMode.ChatGpt, L("Settings.AuthModeChatGpt")),
        ];
        _reasoningEffortOptions =
        [
            new("low", L("Settings.ReasoningLow")),
            new("medium", L("Settings.ReasoningMedium")),
            new("high", L("Settings.ReasoningHigh")),
            new("xhigh", L("Settings.ReasoningXHigh")),
        ];

        _isInitializing = true;
        InitializeComponent();
        ConnectionMethodLabel.Text = L("Settings.ConnectionMethod");
        AuthModeComboBox.ItemsSource = _authModeOptions;
        AuthModeComboBox.SelectedValue = plugin.AuthMode;
        ApiKeyLabel.Text = L("Settings.ApiKey");
        TestButton.Content = L("Settings.Test");
        ChatGptLabel.Text = L("Settings.ChatGptLogin");
        ChatGptDescriptionText.Text = L("Settings.ChatGptDescription");
        SignInBrowserButton.Content = L("Settings.SignInBrowser");
        ImportLoginButton.Content = L("Settings.ImportExistingLogin");
        RemoveLoginButton.Content = L("Settings.Remove");
        TranscriptionModelLabel.Text = L("Settings.TranscriptionModel");
        TranscriptionModelComboBox.ItemsSource = plugin.TranscriptionModels;
        TranscriptionModelComboBox.SelectedValue = plugin.SelectedModelId ?? plugin.TranscriptionModels.FirstOrDefault()?.Id;
        LlmModelLabel.Text = L("Settings.LlmModel");
        RefreshLlmModelsButton.Content = L("Settings.Refresh");
        ReasoningEffortLabel.Text = L("Settings.ReasoningEffort");
        ReasoningEffortComboBox.ItemsSource = _reasoningEffortOptions;
        ReasoningEffortComboBox.SelectedValue = plugin.ReasoningEffort;
        LlmHelpText.Text = L("Settings.LlmHelpApiKey");
        VoiceLabel.Text = L("Settings.TtsVoice");
        InstructionsLabel.Text = L("Settings.VoiceInstructions");
        VoiceComboBox.ItemsSource = plugin.AvailableVoices;
        VoiceComboBox.SelectedValue = plugin.SelectedVoiceId;
        TtsInstructionsBox.Text = plugin.TtsInstructions;
        RefreshLlmModels();

        // Pre-fill password box if API key is already set
        if (!string.IsNullOrEmpty(plugin.ApiKey))
        {
            ApiKeyBox.Password = plugin.ApiKey;
        }

        _isInitializing = false;
        UpdateTranscriptionModelHelp();
        UpdateAuthModePanels();
        UpdateChatGptStatus();
    }

    private async void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password;
        await _plugin.SetApiKeyAsync(key);
        StatusText.Text = string.IsNullOrWhiteSpace(key) ? "" : L("Settings.Saved");
        StatusText.Foreground = Brushes.Gray;
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            StatusText.Text = L("Settings.EnterApiKey");
            StatusText.Foreground = Brushes.Orange;
            return;
        }

        TestButton.IsEnabled = false;
        StatusText.Text = L("Settings.Testing");
        StatusText.Foreground = Brushes.Gray;

        try
        {
            var valid = await _plugin.ValidateApiKeyAsync(key);
            if (valid)
            {
                var models = await _plugin.RefreshAvailableLlmModelsAsync();
                RefreshLlmModels();
                StatusText.Text = models.Count > 0
                    ? L("Settings.ApiKeyValidModels", models.Count)
                    : L("Settings.ApiKeyValid");
                StatusText.Foreground = Brushes.Green;
            }
            else
            {
                StatusText.Text = L("Settings.ApiKeyInvalid");
                StatusText.Foreground = Brushes.Red;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OnAuthModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || AuthModeComboBox.SelectedValue is not OpenAiAuthMode mode)
            return;

        _plugin.SetAuthMode(mode);
        RefreshLlmModels();
        UpdateAuthModePanels();
        UpdateChatGptStatus();
    }

    private async void OnSignInBrowserClick(object sender, RoutedEventArgs e)
    {
        SetOAuthBusy(true);
        ChatGptStatusText.Text = L("Settings.WaitingForBrowserLogin");
        ChatGptStatusText.Foreground = Brushes.Gray;

        try
        {
            await _plugin.LoginWithChatGptInBrowserAsync();
            AuthModeComboBox.SelectedValue = OpenAiAuthMode.ChatGpt;
            RefreshLlmModels();
            ChatGptStatusText.Text = L("Settings.ChatGptConnected");
            ChatGptStatusText.Foreground = Brushes.Green;
        }
        catch (Exception ex)
        {
            ChatGptStatusText.Text = L("Settings.Error", ex.Message);
            ChatGptStatusText.Foreground = Brushes.Red;
        }
        finally
        {
            SetOAuthBusy(false);
            UpdateAuthModePanels();
            UpdateChatGptStatus(keepMessage: true);
        }
    }

    private async void OnImportLoginClick(object sender, RoutedEventArgs e)
    {
        SetOAuthBusy(true);
        try
        {
            await _plugin.ImportExistingLoginAsync();
            AuthModeComboBox.SelectedValue = OpenAiAuthMode.ChatGpt;
            RefreshLlmModels();
            ChatGptStatusText.Text = L("Settings.ImportedExistingLogin");
            ChatGptStatusText.Foreground = Brushes.Green;
        }
        catch (Exception ex)
        {
            ChatGptStatusText.Text = L("Settings.Error", ex.Message);
            ChatGptStatusText.Foreground = Brushes.Red;
        }
        finally
        {
            SetOAuthBusy(false);
            UpdateAuthModePanels();
            UpdateChatGptStatus(keepMessage: true);
        }
    }

    private async void OnRemoveLoginClick(object sender, RoutedEventArgs e)
    {
        await _plugin.ClearChatGptLoginAsync();
        ChatGptStatusText.Text = "";
        UpdateAuthModePanels();
        UpdateChatGptStatus();
    }

    private void OnLlmModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || LlmModelComboBox.SelectedValue is not string modelId)
            return;

        _plugin.SelectLlmModel(modelId);
    }

    private void OnTranscriptionModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || TranscriptionModelComboBox.SelectedValue is not string modelId)
            return;

        _plugin.SelectModel(modelId);
        UpdateTranscriptionModelHelp();
        StatusText.Text = L("Settings.Saved");
        StatusText.Foreground = Brushes.Gray;
    }

    private void OnReasoningEffortSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || ReasoningEffortComboBox.SelectedValue is not string effort)
            return;

        _plugin.SetReasoningEffort(effort);
    }

    private async void OnRefreshLlmModelsClick(object sender, RoutedEventArgs e)
    {
        RefreshLlmModelsButton.IsEnabled = false;
        StatusText.Text = L("Settings.RefreshingModels");
        StatusText.Foreground = Brushes.Gray;

        try
        {
            var models = await _plugin.RefreshAvailableLlmModelsAsync();
            RefreshLlmModels();
            StatusText.Text = models.Count > 0
                ? L("Settings.FetchedModels", models.Count)
                : L("Settings.RefreshModelsFailed");
            StatusText.Foreground = Brushes.Gray;
        }
        finally
        {
            RefreshLlmModelsButton.IsEnabled = true;
        }
    }

    private void OnVoiceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        _plugin.SelectVoice(VoiceComboBox.SelectedValue as string);
        StatusText.Text = L("Settings.Saved");
        StatusText.Foreground = Brushes.Gray;
    }

    private void OnTtsInstructionsChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        _plugin.SetTtsInstructions(TtsInstructionsBox.Text);
        StatusText.Text = L("Settings.Saved");
        StatusText.Foreground = Brushes.Gray;
    }

    private void RefreshLlmModels()
    {
        LlmModelComboBox.ItemsSource = _plugin.SupportedModels;
        LlmModelComboBox.SelectedValue = _plugin.SelectedLlmModelId ?? _plugin.SupportedModels.FirstOrDefault()?.Id;
        LlmHelpText.Text = _plugin.AuthMode == OpenAiAuthMode.ChatGpt
            ? L("Settings.LlmHelpChatGpt")
            : L("Settings.LlmHelpApiKey");
    }

    private void UpdateTranscriptionModelHelp()
    {
        var selectedModel = TranscriptionModelComboBox.SelectedValue as string ?? _plugin.SelectedModelId ?? "";
        TranscriptionModelHelpText.Text = selectedModel == OpenAiRealtimeStreamingSession.ModelId
            ? L("Settings.TranscriptionRealtimeHelp")
            : selectedModel.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase)
                ? L("Settings.TranscriptionGpt4oHelp")
                : L("Settings.TranscriptionWhisperHelp");
    }

    private void UpdateAuthModePanels()
    {
        var isChatGpt = _plugin.AuthMode == OpenAiAuthMode.ChatGpt;
        ApiKeySection.Visibility = isChatGpt ? Visibility.Collapsed : Visibility.Visible;
        ChatGptSection.Visibility = isChatGpt ? Visibility.Visible : Visibility.Collapsed;
        RefreshLlmModelsButton.Visibility = isChatGpt ? Visibility.Collapsed : Visibility.Visible;
        RemoveLoginButton.Visibility = _plugin.HasChatGptCredentials ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateChatGptStatus(bool keepMessage = false)
    {
        RemoveLoginButton.Visibility = _plugin.HasChatGptCredentials ? Visibility.Visible : Visibility.Collapsed;
        if (keepMessage && !string.IsNullOrWhiteSpace(ChatGptStatusText.Text))
            return;

        if (_plugin.HasChatGptCredentials)
        {
            var plan = string.IsNullOrWhiteSpace(_plugin.ChatGptPlanType)
                ? ""
                : $" {L("Settings.ConnectedPlan", _plugin.ChatGptPlanType)}";
            ChatGptStatusText.Text = L("Settings.ChatGptConnected") + plan;
            ChatGptStatusText.Foreground = Brushes.Green;
        }
        else
        {
            ChatGptStatusText.Text = "";
            ChatGptStatusText.Foreground = Brushes.Gray;
        }
    }

    private void SetOAuthBusy(bool isBusy)
    {
        SignInBrowserButton.IsEnabled = !isBusy;
        ImportLoginButton.IsEnabled = !isBusy;
        RemoveLoginButton.IsEnabled = !isBusy && _plugin.HasChatGptCredentials;
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? key;

    private sealed record AuthModeOption(OpenAiAuthMode Value, string DisplayName);
    private sealed record ReasoningEffortOption(string Value, string DisplayName);
}
