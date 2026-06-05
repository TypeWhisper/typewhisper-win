using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.OpenAiCompatible;

/// <summary>
/// Provides OpenAI-compatible settings view behavior.
/// </summary>
public partial class OpenAiCompatibleSettingsView : UserControl
{
    private readonly OpenAiCompatiblePlugin _plugin;
    private bool _isLoadingProfile;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAiCompatibleSettingsView"/> class.
    /// </summary>
    public OpenAiCompatibleSettingsView(OpenAiCompatiblePlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        ProfilesHeader.Text = L("Settings.Profiles");
        AddProfileButton.Content = L("Settings.AddProfile");
        DeleteProfileButton.Content = L("Settings.DeleteProfile");
        RenameProfileButton.Content = L("Settings.RenameProfile");
        ServerUrlHeader.Text = L("Settings.ServerUrl");
        ApiKeyHeader.Text = L("Settings.ApiKey");
        ConnectButton.Content = L("Settings.Connect");
        ApiKeyHintText.Text = L("Settings.ApiKeyHint");
        ModelSelectionHeader.Text = L("Settings.ModelSelection");
        RefreshButton.Content = L("Settings.Refresh");
        TranscriptionModelLabel1.Text = L("Settings.TranscriptionModel");
        LlmModelLabel1.Text = L("Settings.LlmModel");
        NoModelsWarning.Text = L("Settings.NoModelsFound");
        TranscriptionModelLabel2.Text = L("Settings.TranscriptionModel");
        SaveTranscriptionButton.Content = L("Settings.Save");
        LlmModelLabel2.Text = L("Settings.LlmModel");
        SaveLlmButton.Content = L("Settings.Save");

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshProfiles(OpenAiCompatiblePlugin.DefaultProfileId);
    }

    private void RefreshProfiles(string? selectedProfileId = null)
    {
        var requestedProfileId = string.IsNullOrWhiteSpace(selectedProfileId)
            ? SelectedProfileId()
            : selectedProfileId;

        _isLoadingProfile = true;
        try
        {
            ProfilePicker.ItemsSource = _plugin.Profiles.ToList();
            ProfilePicker.SelectedItem = _plugin.Profiles.FirstOrDefault(profile => profile.Id == requestedProfileId)
                                         ?? _plugin.Profiles.FirstOrDefault();
        }
        finally
        {
            _isLoadingProfile = false;
        }

        LoadSelectedProfile();
    }

    private void LoadSelectedProfile()
    {
        var profile = SelectedProfile();
        if (profile is null)
            return;

        _isLoadingProfile = true;
        try
        {
            ProfileNameBox.Text = profile.Name;
            UrlBox.Text = string.IsNullOrWhiteSpace(profile.BaseUrl)
                ? "http://localhost:11434"
                : profile.BaseUrl;
            ApiKeyBox.Password = _plugin.GetApiKey(profile.Id) ?? "";
            ManualTranscriptionBox.Text = profile.SelectedModelId ?? "";
            ManualLlmBox.Text = profile.SelectedLlmModelId ?? "";
            DeleteProfileButton.IsEnabled = profile.Id != OpenAiCompatiblePlugin.DefaultProfileId;

            ConnectionStatusPanel.Visibility = Visibility.Collapsed;
            var models = profile.FetchedModels.ToList();
            if (!string.IsNullOrWhiteSpace(profile.BaseUrl))
            {
                ModelsSection.Visibility = Visibility.Visible;
                PopulateModels(models);
                if (models.Count > 0)
                    ShowConnectionSuccess(models.Count, profile.BaseUrl);
            }
            else
            {
                ModelsSection.Visibility = Visibility.Collapsed;
                PickerSection.Visibility = Visibility.Collapsed;
                ManualSection.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _isLoadingProfile = false;
        }
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        var profile = SelectedProfile();
        if (profile is null)
            return;

        var url = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url))
            return;

        _plugin.SetBaseUrl(url, profile.Id);

        var key = ApiKeyBox.Password.Trim();
        await _plugin.SetApiKeyAsync(key, profile.Id);

        ConnectButton.IsEnabled = false;
        ConnectButton.Content = L("Settings.Connecting");
        ShowConnectionStatus("\u23F3", L("Settings.Connecting"), L("Settings.TryingConnection", url), Brushes.Gray);

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var models = await _plugin.FetchModelsAsync(profile.Id, cts.Token);
            var connected = models.Count > 0;

            if (!connected)
            {
                using var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                connected = await _plugin.ValidateConnectionAsync(profile.Id, cts2.Token);
            }

            if (connected)
            {
                _plugin.SetFetchedModelsForProfile(profile.Id, models);
                if (models.Count > 0)
                {
                    if (string.IsNullOrWhiteSpace(profile.SelectedModelId))
                        _plugin.SelectModelForProfile(profile.Id, models[0].Id);
                    if (string.IsNullOrWhiteSpace(profile.SelectedLlmModelId))
                        _plugin.SelectLlmModelForProfile(profile.Id, models[0].Id);
                }

                ModelsSection.Visibility = Visibility.Visible;
                PopulateModels(models);
                ShowConnectionSuccess(models.Count, profile.BaseUrl);
                RefreshProfiles(profile.Id);
            }
            else
            {
                ShowConnectionStatus("\u274C", L("Settings.ConnectionFailed"),
                    L("Settings.ServerNotResponding", url),
                    Brushes.Red);
            }
        }
        catch (OperationCanceledException)
        {
            ShowConnectionStatus("\u274C", L("Settings.Timeout"),
                L("Settings.TimeoutDetail", url),
                Brushes.Red);
        }
        catch (Exception ex)
        {
            var detail = ex.Message;
            if (ex.InnerException is not null)
                detail += $" ({ex.InnerException.Message})";

            ShowConnectionStatus("\u274C", L("Settings.ConnectionError"), detail, Brushes.Red);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = L("Settings.Connect");
        }
    }

    private void ShowConnectionSuccess(int modelCount, string url)
    {
        var detail = L("Settings.ConnectionSuccess", url, modelCount);
        ShowConnectionStatus("\u2705", L("Settings.Connected", modelCount), detail,
            new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)));
    }

    private void ShowConnectionStatus(string icon, string status, string detail, Brush color)
    {
        ConnectionStatusPanel.Visibility = Visibility.Visible;
        ConnectionStatusIcon.Text = icon;
        ConnectionStatus.Text = status;
        ConnectionStatus.Foreground = color;
        ConnectionDetail.Text = detail;
    }

    private async void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isLoadingProfile)
            return;

        if (SelectedProfile() is { } profile)
            await _plugin.SetApiKeyAsync(ApiKeyBox.Password, profile.Id);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var profile = SelectedProfile();
        if (profile is null)
            return;

        RefreshButton.IsEnabled = false;
        try
        {
            var models = await _plugin.FetchModelsAsync(profile.Id);
            _plugin.SetFetchedModelsForProfile(profile.Id, models);
            PopulateModels(models);

            if (models.Count > 0)
                ShowConnectionSuccess(models.Count, profile.BaseUrl);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void PopulateModels(List<FetchedModel> models)
    {
        var profile = SelectedProfile();
        if (profile is null)
            return;

        if (models.Count > 0)
        {
            PickerSection.Visibility = Visibility.Visible;
            ManualSection.Visibility = Visibility.Collapsed;

            TranscriptionModelPicker.ItemsSource = models;
            LlmModelPicker.ItemsSource = models;

            var selectedTranscription = models.FirstOrDefault(m => m.Id == profile.SelectedModelId);
            TranscriptionModelPicker.SelectedItem = selectedTranscription ?? models.FirstOrDefault();

            var selectedLlm = models.FirstOrDefault(m => m.Id == profile.SelectedLlmModelId);
            LlmModelPicker.SelectedItem = selectedLlm ?? models.FirstOrDefault();
        }
        else
        {
            PickerSection.Visibility = Visibility.Collapsed;
            ManualSection.Visibility = Visibility.Visible;
        }
    }

    private void OnTranscriptionModelChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (_isLoadingProfile)
            return;

        if (SelectedProfile() is { } profile && TranscriptionModelPicker.SelectedItem is FetchedModel model)
            _plugin.SelectModelForProfile(profile.Id, model.Id);
    }

    private void OnLlmModelChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (_isLoadingProfile)
            return;

        if (SelectedProfile() is { } profile && LlmModelPicker.SelectedItem is FetchedModel model)
            _plugin.SelectLlmModelForProfile(profile.Id, model.Id);
    }

    private void OnSaveManualTranscription(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (SelectedProfile() is not { } profile)
            return;

        var id = ManualTranscriptionBox.Text.Trim();
        if (!string.IsNullOrEmpty(id))
            _plugin.SelectModelForProfile(profile.Id, id);
    }

    private void OnSaveManualLlm(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (SelectedProfile() is not { } profile)
            return;

        var id = ManualLlmBox.Text.Trim();
        if (!string.IsNullOrEmpty(id))
            _plugin.SelectLlmModelForProfile(profile.Id, id);
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (_isLoadingProfile)
            return;

        LoadSelectedProfile();
    }

    private void OnAddProfileClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var profile = _plugin.AddProfile(L("Settings.NewProfileDefaultName"));
        RefreshProfiles(profile.Id);
    }

    private void OnRenameProfileClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (SelectedProfile() is not { } profile)
            return;

        _plugin.RenameProfile(profile.Id, ProfileNameBox.Text);
        RefreshProfiles(profile.Id);
    }

    private async void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (SelectedProfile() is not { } profile)
            return;

        if (await _plugin.DeleteProfileAsync(profile.Id))
            RefreshProfiles(OpenAiCompatiblePlugin.DefaultProfileId);
    }

    private OpenAiCompatibleProfile? SelectedProfile() =>
        ProfilePicker.SelectedItem as OpenAiCompatibleProfile
        ?? _plugin.Profiles.FirstOrDefault(profile => profile.Id == OpenAiCompatiblePlugin.DefaultProfileId)
        ?? _plugin.Profiles.FirstOrDefault();

    private string SelectedProfileId() =>
        SelectedProfile()?.Id ?? OpenAiCompatiblePlugin.DefaultProfileId;

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? key;
}
