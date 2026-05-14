using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Diagnostics;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenRouter;

public partial class OpenRouterSettingsView : UserControl
{
    private readonly OpenRouterPlugin _plugin;
    private readonly List<ModelPickerItem> _modelItems = [];
    private bool _isLoading;
    private bool _showApiKey;

    public OpenRouterSettingsView(OpenRouterPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
        ApiKeyLabel.Text = L("Settings.ApiKey");
        ShowApiKeyButton.Content = L("Settings.Show");
        SaveButton.Content = L("Settings.Save");
        RemoveButton.Content = L("Settings.Remove");
        GetApiKeyLink.Inlines.Clear();
        GetApiKeyLink.Inlines.Add(L("Settings.GetApiKey"));
        LlmModelLabel.Text = L("Settings.LlmModel");
        RefreshButton.Content = L("Settings.Refresh");
        SearchBox.Text = "";
        SearchBox.ToolTip = L("Settings.SearchModels");
        ModelHintText.Text = L("Settings.DefaultModelsHint");
        TemperatureLabel.Text = L("Settings.Temperature");
        TemperatureValueLabel.Text = L("Settings.TemperatureValue");

        if (!string.IsNullOrEmpty(plugin.ApiKey))
        {
            ApiKeyBox.Password = plugin.ApiKey;
            ApiKeyTextBox.Text = plugin.ApiKey;
        }

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoading = true;
        TemperatureModePicker.ItemsSource = new[]
        {
            new TemperatureModeOption("providerDefault", L("Settings.TemperatureProviderDefault")),
            new TemperatureModeOption("custom", L("Settings.TemperatureCustom")),
        };
        TemperatureModePicker.SelectedItem = ((IEnumerable<TemperatureModeOption>)TemperatureModePicker.ItemsSource)
            .FirstOrDefault(option => option.Value == _plugin.TemperatureMode);
        TemperatureSlider.Value = _plugin.TemperatureValue;
        _isLoading = false;

        PopulateModels();
        UpdateModelSectionVisibility();
        UpdateTemperatureVisibility();
        await RefreshCreditsAsync();
    }

    private void OnToggleApiKeyVisibilityClick(object sender, RoutedEventArgs e)
    {
        _showApiKey = !_showApiKey;
        if (_showApiKey)
        {
            ApiKeyTextBox.Text = ApiKeyBox.Password;
            ApiKeyTextBox.Visibility = Visibility.Visible;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            ShowApiKeyButton.Content = L("Settings.Hide");
        }
        else
        {
            ApiKeyBox.Password = ApiKeyTextBox.Text;
            ApiKeyBox.Visibility = Visibility.Visible;
            ApiKeyTextBox.Visibility = Visibility.Collapsed;
            ShowApiKeyButton.Content = L("Settings.Show");
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var key = CurrentApiKey().Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            StatusText.Text = L("Settings.EnterApiKey");
            StatusText.Foreground = Brushes.Orange;
            return;
        }

        await _plugin.SetApiKeyAsync(key);
        SetApiKeyText(key);
        await ValidateAndLoadAsync(key);
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        await _plugin.SetApiKeyAsync("");
        SetApiKeyText("");
        StatusText.Text = L("Settings.Removed");
        StatusText.Foreground = Brushes.Gray;
        CreditsText.Text = "";
        UpdateModelSectionVisibility();
    }

    private async Task ValidateAndLoadAsync(string key)
    {
        SaveButton.IsEnabled = false;
        StatusText.Text = L("Settings.Validating");
        StatusText.Foreground = Brushes.Gray;

        try
        {
            var valid = await _plugin.ValidateApiKeyAsync(key);
            if (valid)
            {
                var modelsTask = _plugin.FetchModelsAsync();
                var transcriptionModelsTask = _plugin.FetchTranscriptionModelsAsync();
                var creditsTask = _plugin.FetchCreditsAsync();
                var models = await modelsTask;
                var transcriptionModels = await transcriptionModelsTask;
                var credits = await creditsTask;

                if (models.Count > 0)
                    _plugin.SetFetchedModels(models);
                if (transcriptionModels.Count > 0)
                    _plugin.SetFetchedTranscriptionModels(transcriptionModels);

                PopulateModels();
                ShowCredits(credits);
                UpdateModelSectionVisibility();
                var totalModels = models.Count + transcriptionModels.Count;
                StatusText.Text = totalModels > 0
                    ? L("Settings.ApiKeyValidModels", totalModels)
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
            SaveButton.IsEnabled = true;
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (!_plugin.IsAvailable)
        {
            StatusText.Text = L("Settings.EnterApiKey");
            StatusText.Foreground = Brushes.Orange;
            return;
        }

        RefreshButton.IsEnabled = false;
        StatusText.Text = L("Settings.RefreshingModels");
        StatusText.Foreground = Brushes.Gray;

        try
        {
            var modelsTask = _plugin.FetchModelsAsync();
            var transcriptionModelsTask = _plugin.FetchTranscriptionModelsAsync();
            var creditsTask = _plugin.FetchCreditsAsync();
            var models = await modelsTask;
            var transcriptionModels = await transcriptionModelsTask;
            var credits = await creditsTask;

            if (models.Count > 0)
                _plugin.SetFetchedModels(models);
            if (transcriptionModels.Count > 0)
                _plugin.SetFetchedTranscriptionModels(transcriptionModels);

            PopulateModels();
            ShowCredits(credits);
            var totalModels = models.Count + transcriptionModels.Count;
            StatusText.Text = totalModels > 0
                ? L("Settings.ModelsFetched", totalModels)
                : L("Settings.RefreshModelsFailed");
            StatusText.Foreground = totalModels > 0 ? Brushes.Green : Brushes.Orange;
        }
        catch (Exception ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => PopulateFilteredModels();

    private void OnLlmModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
            return;

        if (LlmModelPicker.SelectedItem is ModelPickerItem item)
            _plugin.SelectLlmModel(item.Id);
    }

    private void OnTemperatureModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
            return;

        if (TemperatureModePicker.SelectedItem is TemperatureModeOption option)
            _plugin.SetTemperatureMode(option.Value);

        UpdateTemperatureVisibility();
    }

    private void OnTemperatureValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        TemperatureValueText.Text = TemperatureSlider.Value.ToString("0.0");
        if (_isLoading)
            return;

        _plugin.SetTemperatureValue(TemperatureSlider.Value);
    }

    private void OnGetApiKeyClick(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async Task RefreshCreditsAsync()
    {
        if (!_plugin.IsAvailable)
            return;

        ShowCredits(await _plugin.FetchCreditsAsync());
    }

    private void PopulateModels()
    {
        _modelItems.Clear();

        if (_plugin.FetchedModels.Count > 0)
        {
            foreach (var model in _plugin.FetchedModels)
            {
                _modelItems.Add(new ModelPickerItem(
                    model.Id,
                    model.Name,
                    model.FormattedPricing(L("Settings.Free"))));
            }

            ModelHintText.Text = L("Settings.ModelsFetchedHint", _plugin.FetchedModels.Count);
        }
        else
        {
            foreach (var model in _plugin.SupportedModels)
                _modelItems.Add(new ModelPickerItem(model.Id, model.DisplayName, ""));

            ModelHintText.Text = L("Settings.DefaultModelsHint");
        }

        PopulateFilteredModels();
    }

    private void PopulateFilteredModels()
    {
        var selectedId = (LlmModelPicker.SelectedItem as ModelPickerItem)?.Id
            ?? _plugin.SelectedLlmModelId;
        var query = SearchBox.Text.Trim();
        var items = string.IsNullOrWhiteSpace(query)
            ? _modelItems
            : _modelItems
                .Where(item => item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        _isLoading = true;
        LlmModelPicker.ItemsSource = items;
        LlmModelPicker.SelectedItem = items.FirstOrDefault(item => item.Id == selectedId)
            ?? items.FirstOrDefault();
        _isLoading = false;
    }

    private void ShowCredits(double? credits)
    {
        CreditsText.Text = credits is null
            ? ""
            : L("Settings.CreditsRemaining", FormattableString.Invariant($"${credits.Value:0.00}"));
    }

    private void UpdateModelSectionVisibility()
    {
        ModelsSection.Visibility = _plugin.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.Visibility = _plugin.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTemperatureVisibility()
    {
        var mode = TemperatureModePicker.SelectedItem is TemperatureModeOption option
            ? option.Value
            : _plugin.TemperatureMode;
        TemperatureSliderPanel.Visibility = mode == "custom" ? Visibility.Visible : Visibility.Collapsed;
        TemperatureValueText.Text = TemperatureSlider.Value.ToString("0.0");
    }

    private string CurrentApiKey() =>
        _showApiKey ? ApiKeyTextBox.Text : ApiKeyBox.Password;

    private void SetApiKeyText(string value)
    {
        ApiKeyBox.Password = value;
        ApiKeyTextBox.Text = value;
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? key;

    private sealed record ModelPickerItem(string Id, string DisplayName, string Pricing)
    {
        public string DisplayText =>
            string.IsNullOrWhiteSpace(Pricing) ? DisplayName : $"{DisplayName} - {Pricing}";
    }

    private sealed record TemperatureModeOption(string Value, string DisplayName);
}
