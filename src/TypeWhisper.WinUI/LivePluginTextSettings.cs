using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.WinUI;

internal sealed partial class LivePluginTextSettings : UserControl
{
    internal event Action<bool>? ProfileLayoutChanged;
    private readonly StackPanel _content = new() { Spacing = 10 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly LocalDictationSession _session;
    private readonly string _id;
    private readonly UIElement _credentials;
    private readonly UIElement _models;
    private readonly Panel _connectionActions;
    private readonly List<UIElement> _profileConnectionButtons = [];
    private readonly Action<string?, string?> _connectionChanged;
    private readonly Func<Task<bool>> _canLeaveConnection;
    private readonly Func<string?> _pendingApiKey;
    private readonly Action _clearApiKey;
    private bool _loaded;
    private int _generation;

    internal LivePluginTextSettings(LocalDictationSession session, string id, UIElement credentials, UIElement models,
        Action<string?, string?> connectionChanged, Func<Task<bool>> canLeaveConnection,
        Func<string?> pendingApiKey, Action clearApiKey, Panel connectionActions)
    {
        _session = session; _id = id; _credentials = credentials; _models = models; _connectionChanged = connectionChanged;
        _canLeaveConnection = canLeaveConnection;
        _pendingApiKey = pendingApiKey; _clearApiKey = clearApiKey;
        _connectionActions = connectionActions;
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _content.Children.Add(_status); Content = _content;
        Unloaded += (_, _) => { _generation++; _lifetime.Cancel(); _loaded = false; };
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _lifetime.Dispose(); _lifetime = new();
            await ReloadAsync();
        };
    }

    internal void DetachHostControls()
    {
        foreach (var button in _profileConnectionButtons) _connectionActions.Children.Remove(button);
        _profileConnectionButtons.Clear();
        if (_credentials is FrameworkElement { Parent: Panel credentialsParent }) credentialsParent.Children.Remove(_credentials);
        if (_models is FrameworkElement { Parent: Panel modelsParent }) modelsParent.Children.Remove(_models);
    }

    private CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, string> _drafts = new();
    private bool _busy;
    private bool _refreshRequested;

    internal async void RequestRefresh()
    {
        if (!IsLoaded) return;
        if (_busy) { _refreshRequested = true; return; }
        _refreshRequested = false;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var generation = ++_generation;
        try
        {
            var snapshot = await _session.PluginRuntime.UseConfigurationAsync(_id, (plugin, _) =>
                Task.FromResult((Fields: plugin is IPluginTextSettings settings ? settings.TextSettings.ToArray() : [],
                    Actions: plugin is IPluginSettingsActions actions ? actions.SettingsActions.ToArray() : [],
                    ShowKey: plugin is not IPluginConnectionSettings connection || connection.ShowApiKeySettings,
                    ConnectionId: (plugin as IPluginConnectionSettings)?.ConnectionIdentity,
                    ProfileSelector: (plugin as IPluginProfileSettings)?.ProfileSelectorId,
                    AddProfile: (plugin as IPluginProfileSettings)?.AddProfileActionId,
                    RemoveProfile: (plugin as IPluginProfileSettings)?.RemoveProfileActionId)), _lifetime.Token);
            if (!IsLoaded || generation != _generation) return;
            var selector = snapshot.Fields.FirstOrDefault(f => f.Id == snapshot.ProfileSelector);
            _connectionChanged(snapshot.ConnectionId, selector?.Choices.FirstOrDefault(c => c.Value == selector.Value)?.Title);
            DetachHostControls();
            if (_status.Parent is Panel statusParent) statusParent.Children.Remove(_status);
            _content.Children.Clear(); _content.Children.Add(_status);
            ProfileLayoutChanged?.Invoke(true);
            if (selector is not null)
            {
                RenderProfileEditor(selector, snapshot.Fields, snapshot.Actions, snapshot.AddProfile, snapshot.RemoveProfile, snapshot.ShowKey);
                _loaded = true;
                return;
            }
            var single = new PluginTextSetting("__host_settings", "Settings", "", "default")
            { Choices = [new("default", "Settings")] };
            RenderProfileEditor(single, snapshot.Fields, snapshot.Actions, null, null, snapshot.ShowKey, generic: true);
            _loaded = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (IsLoaded && generation == _generation)
                SetStatus("Plugin settings could not be loaded. Reopen this page to retry.");
        }
    }

    private void SetStatus(string message)
    {
        _status.Text = message;
        _status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
