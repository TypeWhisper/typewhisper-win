using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

// Settings derive only from portable capabilities; no provider IDs or custom settings schemas.
internal sealed class LivePortablePluginSettings : UserControl
{
    internal event Action<bool>? ProfileLayoutChanged;
    private readonly LocalDictationSession _session;
    private readonly string _id;
    private readonly bool _showEnableAction;
    private readonly TextBlock _status = Label("");
    private readonly PasswordBox _key = new() { PlaceholderText = "Enter an API key" };
    private readonly StackPanel _credentials = new() { Spacing = 8 };
    private readonly LivePortableModelSettings _models;
    private readonly ContentControl _textSettings = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly HandCursorButton _enable;
    private readonly HandCursorButton _save;
    private readonly HandCursorButton _remove;
    private readonly HandCursorButton _check;
    private bool _working;
    private string? _message;
    private string? _connectionIdentity;
    private string? _connectionTitle;
    private readonly ContentControl _keyLabel = new();

    private void OnConnectionChanged(string? identity, string? title)
    {
        _connectionTitle = title;
        _keyLabel.Content = SettingsHelp.Label("API key",
            title is null ? "The key is stored through encrypted Windows user storage. Leave empty to keep the saved key."
                : "Saved with this profile. Leave empty to keep the saved key. Local servers may not need a key.");
        _save.Visibility = title is null ? Visibility.Visible : Visibility.Collapsed;
        _check.Visibility = title is null ? Visibility.Visible : Visibility.Collapsed;
        if (_connectionIdentity == identity) return;
        _connectionIdentity = identity;
        _key.Password = "";
        _message = null;
        _status.Text = ""; _status.Visibility = Visibility.Collapsed;
    }

    private async Task<bool> CanLeaveConnectionAsync()
    {
        if (string.IsNullOrEmpty(_key.Password)) return true;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Unsaved API key",
            Content = "The key entered for “" + _connectionTitle + "” has not been saved. Use Save profile before switching, or discard the entered key.",
            PrimaryButtonText = "Discard entered key", CloseButtonText = "Keep editing", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        _key.Password = "";
        return true;
    }

    internal LivePortablePluginSettings(LocalDictationSession session, string id, bool showEnableAction = true)
    {
        _session = session; _id = id;
        _showEnableAction = showEnableAction;
        _models = new(session, id);
        var content = new Grid { RowSpacing = 6 };
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(_status);
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _enable = Button("Enable plugin", () => session.SetRegistryPluginEnabledAsync(id, true));
        Grid.SetRow(_enable, 1); Grid.SetRow(_textSettings, 2);
        content.Children.Add(_enable);
        _keyLabel.Content = SettingsHelp.Label("API key", "The plugin stores the key through encrypted Windows user storage. An empty field keeps the saved key.", 14);
        _credentials.Children.Add(_keyLabel);
        AutomationProperties.SetName(_key, "Plugin API key");
        _credentials.Children.Add(_key);
        _save = Button("Save key", async () =>
        {
            var error = await session.SaveRegistryKeyAsync(id, _key.Password);
            if (error is null && IsLoaded) _key.Password = "";
            return error;
        }, "API key saved. Check connection to verify it.");
        _remove = Button("Remove saved key", () => session.SaveRegistryKeyAsync(id, ""), "API key removed.");
        _check = Button("Check connection", () => session.ValidateRegistryKeyAsync(id), "Connection verified. No audio was uploaded.");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_save); actions.Children.Add(_remove); actions.Children.Add(_check);
        _credentials.SizeChanged += (_, e) => actions.Orientation = e.NewSize.Width < 460 ? Orientation.Vertical : Orientation.Horizontal;
        _credentials.Children.Add(actions); content.Children.Add(_textSettings);
        Content = content;
        _key.PasswordChanged += (_, _) =>
        {
            UpdateButtons();
            if (_textSettings.Content is LivePluginTextSettings settings) settings.NotifyProfileKeyChanged();
        };
        Loaded += (_, _) => { session.Changed += OnChanged; Refresh(); };
        Unloaded += (_, _) => { session.Changed -= OnChanged; _key.Password = ""; };
        Refresh();
    }
    private void OnChanged() => DispatcherQueue.TryEnqueue(() => { if (IsLoaded) Refresh(); });
    private void Refresh()
    {
        var state = _session.PluginRuntime.Snapshot().FirstOrDefault(item => item.PluginId == _id);
        _status.Text = _message ?? state?.Error ?? (state?.Enabled == true ? "" : "Enable this plugin to configure its providers.");
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
        _enable.Visibility = _showEnableAction && state?.Enabled != true ? Visibility.Visible : Visibility.Collapsed;
        _key.PlaceholderText = state?.ApiKeyConfigured == true ? "Key saved - enter a replacement" : "Enter an API key";
        _remove.Visibility = state?.ApiKeyConfigured == true ? Visibility.Visible : Visibility.Collapsed;
        _credentials.Visibility = state?.HasApiKeySettings == true ? Visibility.Visible : Visibility.Collapsed;
        if (state?.Enabled == true && state.HasTextSettings)
        {
            if (_textSettings.Content is not LivePluginTextSettings)
            {
                if (_textSettings.Content is StackPanel old) old.Children.Clear();
                var editor = new LivePluginTextSettings(_session, _id, _credentials, _models, OnConnectionChanged, CanLeaveConnectionAsync,
                    () => string.IsNullOrWhiteSpace(_key.Password) ? null : _key.Password, () => _key.Password = "");
                editor.ProfileLayoutChanged += profile => ProfileLayoutChanged?.Invoke(profile);
                _textSettings.Content = editor;
            }
        }
        else if (_textSettings.Content is not StackPanel)
        {
            if (_textSettings.Content is LivePluginTextSettings old) old.DetachHostControls();
            var fallback = new StackPanel { Spacing = 14 };
            fallback.Children.Add(_credentials); fallback.Children.Add(_models);
            _textSettings.Content = fallback;
            ProfileLayoutChanged?.Invoke(false);
        }
        _models.Visibility = _session.PluginRuntime.TranscriptionProviders.Any(provider => provider.PluginId == _id) ||
            _session.LlmProviders.Any(provider => provider.PluginId == _id) ||
            _session.ActiveRegistryModelDownload?.PluginId == _id ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        var available = !_working && _session.CanChangeProvider;
        _enable.IsEnabled = _key.IsEnabled = _remove.IsEnabled = _check.IsEnabled = available;
        _save.IsEnabled = available && !string.IsNullOrWhiteSpace(_key.Password);
        _textSettings.IsEnabled = !_working;
    }
    private HandCursorButton Button(string text, Func<Task<string?>> action, string success = "Saved.")
    {
        var button = new HandCursorButton { Content = text, HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        button.Click += async (_, _) =>
        {
            if (_working || !IsLoaded) return;
            _working = true; UpdateButtons();
            var context = _connectionTitle;
            try { var error = await action(); if (IsLoaded) _message = (context is null ? "" : "“" + context + "”: ") + (error ?? success); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (IsLoaded) _message = "The plugin operation could not finish. Check its configuration and try again."; }
            finally
            {
                _working = false;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!IsLoaded) return;
                    Refresh(); _models.RequestRefresh();
                    if (_textSettings.Content is LivePluginTextSettings textSettings) textSettings.RequestRefresh();
                });
            }
        };
        return button;
    }
    private static TextBlock Label(string text, double size = 12) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
}
