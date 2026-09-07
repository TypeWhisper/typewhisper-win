using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.WinUI;

internal sealed class LivePluginTextSettings : UserControl
{
    private readonly StackPanel _content = new() { Spacing = 10 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly LocalDictationSession _session;
    private readonly string _id;
    private bool _loaded;

    internal LivePluginTextSettings(LocalDictationSession session, string id)
    {
        _session = session; _id = id;
        _content.Children.Add(_status); Content = _content;
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var fields = await session.PluginRuntime.UseConfigurationAsync(id, (plugin, _) =>
                    Task.FromResult(plugin is IPluginTextSettings settings ? settings.TextSettings.ToArray() : []));
                if (!IsLoaded) { _loaded = false; return; }
                foreach (var field in fields) AddField(field);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { _status.Text = "Plugin settings could not be loaded. Reopen this page to retry."; }
        };
    }

    private void AddField(PluginTextSetting field)
    {
        _content.Children.Add(new TextBlock { Text = field.Title, FontSize = 16 });
        _content.Children.Add(new TextBlock { Text = field.Description, TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = field.Value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 100, MaxHeight = 240, MaxLength = Math.Clamp(field.MaxLength, 1, 32768) };
        AutomationProperties.SetName(input, field.Title);
        _content.Children.Add(input);
        var save = new HandCursorButton { Content = "Save " + field.Title, HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = input.IsEnabled = false;
            try
            {
                var error = await _session.SavePluginTextSettingAsync(_id, field.Id, input.Text);
                _status.Text = error ?? "Saved. The setting applies to the next transcription.";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { _status.Text = "The setting could not be saved. The previous saved value is unchanged."; }
            finally { save.IsEnabled = input.IsEnabled = true; }
        };
        _content.Children.Add(save);
    }
}
