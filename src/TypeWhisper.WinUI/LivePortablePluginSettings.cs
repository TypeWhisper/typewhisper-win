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
    private readonly StackPanel _models = new() { Spacing = 10 };
    private readonly HandCursorButton _enable;
    private readonly HandCursorButton _save;
    private readonly HandCursorButton _remove;
    private readonly HandCursorButton _check;
    private bool _working;
    private string? _message;

    internal LivePortablePluginSettings(LocalDictationSession session, string id)
    {
        _session = session; _id = id;
        var content = new StackPanel { Spacing = 14, Margin = new Thickness(0, 0, 14, 14) };
        content.Children.Add(_status);
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _enable = Button("Enable plugin", () => session.SetRegistryPluginEnabledAsync(id, true));
        content.Children.Add(_enable);
        _credentials.Children.Add(Label("API key", 16));
        AutomationProperties.SetName(_key, "Plugin API key");
        _credentials.Children.Add(_key);
        _credentials.Children.Add(Label("The plugin stores the key through encrypted Windows user storage. An empty field keeps the saved key."));
        _save = Button("Save key", async () =>
        {
            var error = await session.SaveRegistryKeyAsync(id, _key.Password);
            if (error is null) _key.Password = "";
            return error;
        });
        _remove = Button("Remove saved key", () => session.SaveRegistryKeyAsync(id, ""));
        _check = Button("Check connection", () => session.ValidateRegistryKeyAsync(id));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_save); actions.Children.Add(_remove); actions.Children.Add(_check);
        _credentials.Children.Add(actions); content.Children.Add(_credentials); content.Children.Add(_models);
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
        _credentials.Visibility = state?.HasApiKeySettings == true ? Visibility.Visible : Visibility.Collapsed;
        _models.Children.Clear();
        foreach (var provider in _session.PluginRuntime.TranscriptionProviders.Where(item => item.PluginId == _id))
        {
            _models.Children.Add(Label(provider.Name, 16));
            foreach (var model in provider.Models)
            {
                var selected = _session.ActiveProviderId == provider.SelectionId && provider.SelectedModelId == model.Id;
                var button = Button((selected ? "Active: " : "Use model: ") + model.DisplayName,
                    () => _session.SelectProviderModelAsync(provider.SelectionId, model.Id));
                button.IsEnabled = !_working && _session.CanChangeProvider && provider.Ready && !selected;
                _models.Children.Add(button);
            }
            if (!provider.Ready) _models.Children.Add(Label("This provider is not ready. Complete its configuration before choosing a dictation model."));
        }
        foreach (var provider in _session.LlmProviders.Where(item => item.PluginId == _id))
        {
            _models.Children.Add(Label(provider.Name + " · Text processing", 16));
            _models.Children.Add(Label(string.Join(", ", provider.Models.Select(model => model.DisplayName))));
        }
        if (state?.Enabled == true && state.HasApiKeySettings == false)
            _models.Children.Add(Label("This plugin does not expose API-key settings. Other configuration methods are not available on this page."));
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        var available = !_working && _session.CanChangeProvider;
        _enable.IsEnabled = _key.IsEnabled = _remove.IsEnabled = _check.IsEnabled = available;
        _save.IsEnabled = available && !string.IsNullOrWhiteSpace(_key.Password);
    }
    private HandCursorButton Button(string text, Func<Task<string?>> action)
    {
        var button = new HandCursorButton { Content = text, HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
        button.Click += async (_, _) =>
        {
            if (_working) return;
            _working = true; UpdateButtons();
            try { _message = await action() ?? "Saved."; }
            finally { _working = false; Refresh(); }
        };
        return button;
    }
    private static TextBlock Label(string text, double size = 12) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
}
