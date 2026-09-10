using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

// Settings derive only from portable capabilities; no provider IDs or custom settings schemas.
internal sealed class LivePortablePluginSettings : UserControl
{
    private readonly LocalDictationSession _session;
    private readonly string _id;
    private readonly TextBlock _status = Label("");
    private readonly PasswordBox _key = new() { PlaceholderText = "Enter an API key" };
    private readonly StackPanel _credentials = new() { Spacing = 8 };
    private readonly LivePortableModelSettings _models;
    private readonly ContentControl _textSettings = new();
    private readonly HandCursorButton _enable;
    private readonly HandCursorButton _save;
    private readonly HandCursorButton _remove;
    private readonly HandCursorButton _check;
    private bool _working;
    private string? _message;

    internal LivePortablePluginSettings(LocalDictationSession session, string id)
    {
        _session = session; _id = id;
        _models = new(session, id);
        var content = new StackPanel { Spacing = 14, Margin = new Thickness(0, 0, 14, 14) };
        content.Children.Add(_status);
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _enable = Button("Enable plugin", () => session.SetRegistryPluginEnabledAsync(id, true));
        content.Children.Add(_enable);
        _credentials.Children.Add(SettingsHelp.Label("API key", "The plugin stores the key through encrypted Windows user storage. An empty field keeps the saved key.", 16));
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
        _credentials.Children.Add(actions); content.Children.Add(_credentials); content.Children.Add(_models); content.Children.Add(_textSettings);
        Content = content;
        _key.PasswordChanged += (_, _) => UpdateButtons();
        Loaded += (_, _) => { session.Changed += OnChanged; Refresh(); };
        Unloaded += (_, _) => { session.Changed -= OnChanged; _key.Password = ""; };
        Refresh();
    }
    private void OnChanged() => DispatcherQueue.TryEnqueue(() => { if (IsLoaded) Refresh(); });
    private void Refresh()
    {
        var state = _session.PluginRuntime.Snapshot().FirstOrDefault(item => item.PluginId == _id);
        _status.Text = _message ?? state?.Error ?? (state?.Enabled == true ? "Plugin enabled." : "Enable this plugin to configure its providers.");
        _enable.Visibility = state?.Enabled == true ? Visibility.Collapsed : Visibility.Visible;
        _key.PlaceholderText = state?.ApiKeyConfigured == true ? "Key saved - enter a replacement" : "Enter an API key";
        _credentials.Visibility = state?.HasApiKeySettings == true ? Visibility.Visible : Visibility.Collapsed;
        if (state?.Enabled == true && state.HasTextSettings)
            _textSettings.Content ??= new LivePluginTextSettings(_session, _id);
        else _textSettings.Content = null;
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
    }
    private HandCursorButton Button(string text, Func<Task<string?>> action, string success = "Saved.")
    {
        var button = new HandCursorButton { Content = text, HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        button.Click += async (_, _) =>
        {
            if (_working || !IsLoaded) return;
            _working = true; UpdateButtons();
            try { var error = await action(); if (IsLoaded) _message = error ?? success; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (IsLoaded) _message = "The plugin operation could not finish. Check its configuration and try again."; }
            finally
            {
                _working = false;
                DispatcherQueue.TryEnqueue(() => { if (IsLoaded) { Refresh(); _models.RequestRefresh(); } });
            }
        };
        return button;
    }
    private static TextBlock Label(string text, double size = 12) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
}
