using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.WinUI;

internal sealed class LivePluginTextSettings : UserControl
{
    private readonly StackPanel _content = new() { Spacing = 10 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly LocalDictationSession _session;
    private readonly string _id;
    private bool _loaded;
    private int _generation;

    internal LivePluginTextSettings(LocalDictationSession session, string id)
    {
        _session = session; _id = id;
        _content.Children.Add(_status); Content = _content;
        Unloaded += (_, _) => _generation++;
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            var generation = ++_generation;
            try
            {
                var fields = await session.PluginRuntime.UseConfigurationAsync(id, (plugin, _) =>
                    Task.FromResult(plugin is IPluginTextSettings settings ? settings.TextSettings.ToArray() : []));
                if (!IsLoaded || generation != _generation) return;
                foreach (var field in fields) AddField(field);
                _loaded = true;
                SetStatus(string.Empty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (IsLoaded && generation == _generation)
                    SetStatus("Plugin settings could not be loaded. Reopen this page to retry.");
            }
        };
    }

    private void AddField(PluginTextSetting field)
    {
        _content.Children.Add(new TextBlock { Text = field.Title, FontSize = 16 });
        _content.Children.Add(new TextBlock { Text = field.Description, TextWrapping = TextWrapping.Wrap });
        var textInput = new TextBox
        {
            AcceptsReturn = field.IsMultiline,
            TextWrapping = field.IsMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = field.IsMultiline ? 100 : 40,
            MaxHeight = field.IsMultiline ? 240 : 40,
            Padding = new Thickness(10, 8, 10, 8),
            Style = (Style)Application.Current.Resources[field.IsMultiline
                ? "LexiconMultilineStyle" : "SearchTextBoxStyle"],
            MaxLength = Math.Clamp(field.MaxLength, 1, 32768),
            Text = field.Value
        };
        var choiceInput = field.Choices.Count == 0 ? null : new ComboBox
        {
            ItemsSource = field.Choices,
            DisplayMemberPath = nameof(PluginSettingChoice.Title),
            SelectedValuePath = nameof(PluginSettingChoice.Value),
            SelectedValue = field.Value,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 40
        };
        Control input = choiceInput is null ? textInput : choiceInput;
        AutomationProperties.SetName(input, field.Title);
        AutomationProperties.SetHelpText(input, field.Description);
        var fieldBorder = new Border
        {
            Child = input,
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        input.GotFocus += (_, _) => fieldBorder.BorderBrush = (Brush)Application.Current.Resources["AccentBrush"];
        input.LostFocus += (_, _) => fieldBorder.BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"];
        _content.Children.Add(fieldBorder);
        var save = new HandCursorButton { Content = "Save " + field.Title, HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        var saving = false;
        input.Loaded += (_, _) => input.IsEnabled = !saving;
        save.Loaded += (_, _) => save.IsEnabled = !saving;
        save.Click += async (_, _) =>
        {
            if (saving || !IsLoaded) return;
            saving = true;
            var generation = _generation;
            save.IsEnabled = input.IsEnabled = false;
            SetStatus(string.Empty);
            try
            {
                var value = choiceInput?.SelectedValue as string;
                if (choiceInput is not null && value is null)
                {
                    SetStatus($"Select a valid value for {field.Title}.");
                    return;
                }
                var error = await _session.SavePluginTextSettingAsync(_id, field.Id, value ?? textInput.Text);
                if (IsLoaded && generation == _generation)
                    SetStatus(error ?? "Saved. The setting applies the next time this plugin runs.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (IsLoaded && generation == _generation)
                    SetStatus("The setting could not be saved. The previous saved value is unchanged.");
            }
            finally
            {
                saving = false;
                if (IsLoaded) save.IsEnabled = input.IsEnabled = true;
            }
        };
        _content.Children.Add(save);
    }

    private void SetStatus(string message)
    {
        _status.Text = message;
        _status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
