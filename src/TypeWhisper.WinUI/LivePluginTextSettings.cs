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
    private readonly UIElement _credentials;
    private readonly UIElement _models;
    private bool _loaded;
    private int _generation;

    internal LivePluginTextSettings(LocalDictationSession session, string id, UIElement credentials, UIElement models)
    {
        _session = session; _id = id; _credentials = credentials; _models = models;
        _content.Children.Add(_status); Content = _content;
        Unloaded += (_, _) => { _generation++; _lifetime.Cancel(); _loaded = false; };
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _lifetime.Dispose(); _lifetime = new();
            await ReloadAsync();
        };
    }

    internal void DetachHostControls() { _content.Children.Remove(_credentials); _content.Children.Remove(_models); }

    private CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, string> _drafts = new();
    private bool _busy;

    private async Task ReloadAsync()
    {
        var generation = ++_generation;
        try
        {
            var snapshot = await _session.PluginRuntime.UseConfigurationAsync(_id, (plugin, _) =>
                Task.FromResult((Fields: plugin is IPluginTextSettings settings ? settings.TextSettings.ToArray() : [],
                    Actions: plugin is IPluginSettingsActions actions ? actions.SettingsActions.ToArray() : [],
                    ShowKey: plugin is not IPluginConnectionSettings connection || connection.ShowApiKeySettings)), _lifetime.Token);
            if (!IsLoaded || generation != _generation) return;
            _content.Children.Clear(); _content.Children.Add(_status);
            void Fields(PluginSettingsSection section)
            {
                foreach (var field in snapshot.Fields.Where(f => f.Section == section))
                    AddField(_drafts.TryGetValue(field.Id, out var draft) ? field with { Value = draft } : field);
            }
            void Actions(PluginSettingsSection section)
            { foreach (var action in snapshot.Actions.Where(a => a.Section == section)) AddAction(action); }
            Fields(PluginSettingsSection.Connection);
            if (snapshot.ShowKey) _content.Children.Add(_credentials);
            Actions(PluginSettingsSection.Connection);
            if (_models is LivePortableModelSettings modelSettings)
                modelSettings.ShowLlmSummary = !snapshot.Fields.Any(f => f.Section == PluginSettingsSection.TextProcessing);
            _content.Children.Add(_models);
            foreach (var section in new[] { PluginSettingsSection.Transcription, PluginSettingsSection.Speech, PluginSettingsSection.TextProcessing, PluginSettingsSection.General })
            {
                if (section is PluginSettingsSection.Speech or PluginSettingsSection.TextProcessing && snapshot.Fields.Any(f => f.Section == section))
                    _content.Children.Add(new Border { Height = 1, Margin = new(0, 12, 0, 6), Background = (Brush)Application.Current.Resources["HairlineBrush"] });
                Actions(section); Fields(section);
            }
            _loaded = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (IsLoaded && generation == _generation)
                SetStatus("Plugin settings could not be loaded. Reopen this page to retry.");
        }
    }

    private void AddAction(PluginSettingsAction action)
    {
        _content.Children.Add(new TextBlock { Text = action.Description, TextWrapping = TextWrapping.Wrap });
        var button = new HandCursorButton { Content = action.Title, HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        button.Click += async (_, _) =>
        {
            if (_busy || !IsLoaded) return;
            if (!_session.CanStartPluginSettingsAction)
            { SetStatus("Finish dictation and other plugin operations before starting this action."); return; }
            _busy = true; IsEnabled = false;
            var generation = _generation;
            SetStatus(action.Title + "…");
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                void CancelForRecording() => timeout.Cancel();
                _session.RecordingStarting += CancelForRecording;
                string? result;
                try
                {
                    result = await _session.PluginRuntime.UseConfigurationAsync(_id, async (plugin, ct) =>
                    {
                        if (plugin is not IPluginSettingsActions actions) throw new InvalidOperationException();
                        return await actions.ExecuteSettingsActionAsync(action.Id, ct);
                    }, timeout.Token);
                }
                finally { _session.RecordingStarting -= CancelForRecording; }
                if (!IsLoaded || generation != _generation) return;
                await ReloadAsync();
                SetStatus(result ?? "Completed.");
            }
            catch (OperationCanceledException)
            { if (IsLoaded && generation == _generation) SetStatus("The action was cancelled or timed out. You can retry."); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (IsLoaded && generation == _generation) SetStatus("The action failed. Check the account and connection, then retry."); }
            finally { _busy = false; IsEnabled = true; }
        };
        _content.Children.Add(button);
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
        textInput.TextChanged += (_, _) => _drafts[field.Id] = textInput.Text;
        if (choiceInput is not null) choiceInput.SelectionChanged += (_, _) =>
        { if (choiceInput.SelectedValue is string value) _drafts[field.Id] = value; };
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
        async Task SaveAsync()
        {
            if (saving || _busy || !IsLoaded) return;
            saving = _busy = true;
            IsEnabled = false;
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
                {
                    if (error is null) { _drafts.Remove(field.Id); await ReloadAsync(); }
                    SetStatus(error ?? "Saved. The setting applies the next time this plugin runs.");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (IsLoaded && generation == _generation)
                    SetStatus("The setting could not be saved. The previous saved value is unchanged.");
            }
            finally
            {
                saving = _busy = false;
                IsEnabled = true;
                if (IsLoaded) save.IsEnabled = input.IsEnabled = true;
            }
        }
        save.Click += async (_, _) => await SaveAsync();
        if (field.SaveChoiceOnChange && choiceInput is not null)
            choiceInput.SelectionChanged += async (_, _) =>
            {
                if (choiceInput.IsLoaded && choiceInput.SelectedValue is string value && value != field.Value)
                    await SaveAsync();
            };
        else _content.Children.Add(save);
    }

    private void SetStatus(string message)
    {
        _status.Text = message;
        _status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
