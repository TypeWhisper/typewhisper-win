using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.WinUI;

internal sealed partial class LivePluginTextSettings
{
    private Action? _profileDirtyChanged;
    private string? _profileName;
    private string _memoryFilter = "";
    private bool _singleConfiguration;
    private ListView? _profilePicker;
    private double _profileScrollOffset;
    private readonly Dictionary<string, bool> _dirtyProfiles = new();
    private readonly HashSet<string> _profileActionDrafts = [];
    internal void NotifyProfileKeyChanged() => _profileDirtyChanged?.Invoke();

    internal async Task<bool> CanLeaveAsync()
    {
        if (_busy) { SetStatus("Wait for the current profile operation to finish."); return false; }
        if (!_dirtyProfiles.Values.Any(dirty => dirty) && _pendingApiKey() is null) return true;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = _singleConfiguration ? "Discard unsaved changes?" : "Discard unsaved profile changes?",
            Content = "Your edits and any entered API key have not been saved. Stay here to save them, or discard them and leave.",
            PrimaryButtonText = "Discard changes", CloseButtonText = "Keep editing", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void RenderProfileEditor(PluginTextSetting selector, PluginTextSetting[] fields,
        PluginSettingsAction[] actions, string? addId, string? removeId, bool showKey, bool generic = false)
    {
        var memoryEditor = _id == "com.typewhisper.file-memory";
        var name = selector.Choices.FirstOrDefault(c => c.Value == selector.Value)?.Title ?? selector.Value;
        if (memoryEditor) { name = name.Length > 65 ? name[..65] + "…" : name; actions = actions.Where(a => a.Id != "search").ToArray(); }
        _profileName = name;
        var singleConfiguration = selector.Choices.Count == 1 && addId is null && removeId is null;
        _singleConfiguration = singleConfiguration;
        var editable = fields.Where(f => (generic || f.Id != selector.Id) && (!memoryEditor || f.Id != "query")).ToArray();
        var values = editable.ToDictionary(f => f.Id,
            f => _drafts.TryGetValue(f.Id, out var draft) ? draft : f.Value);
        _dirtyProfiles[selector.Value] = _profileActionDrafts.Contains(selector.Value) || _pendingApiKey() is not null || editable.Any(f => values[f.Id] != f.Value);
        if (generic)
        {
            foreach (var stale in _drafts.Keys.Where(id => !editable.Any(f => f.Id == id)).ToArray()) _drafts.Remove(stale);
            _dirtyProfiles.Clear();
        }
        var generation = _generation;
        var layout = new Grid { ColumnSpacing = 22, RowSpacing = 12 };
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(185) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var sidebar = new Grid { RowSpacing = 10 };
        sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var sidebarHeader = new Grid();
        sidebarHeader.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        sidebarHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        sidebarHeader.Children.Add(new TextBlock { Text = selector.Title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var sidebarTop = new StackPanel { Spacing = 10 };
        sidebarTop.Children.Add(sidebarHeader);
        sidebar.Children.Add(sidebarTop);
        var picker = new HandCursorListView { SelectionMode = ListViewSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch, ItemContainerStyle = (Style)Application.Current.Resources["CommandItemStyle"] };
        var profileItems = new Dictionary<string, ListViewItem>();
        foreach (var choice in selector.Choices)
        {
            var label = new TextBlock { Text = choice.Title, TextWrapping = TextWrapping.Wrap, FontSize = 13,
                Margin = new(12, 10, 10, 10), VerticalAlignment = VerticalAlignment.Center,
                MaxLines = memoryEditor ? 3 : 0, TextTrimming = memoryEditor ? TextTrimming.CharacterEllipsis : TextTrimming.None };
            var item = new ListViewItem { Tag = choice.Value, Content = label, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(item, choice.Title);
            profileItems.Add(choice.Value, item); picker.Items.Add(item);
        }
        picker.SelectedItem = profileItems.GetValueOrDefault(selector.Value);
        if (memoryEditor)
        {
            var search = new TextBox { Text = _memoryFilter, PlaceholderText = "Search memories…", MinHeight = 40,
                HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(search, "Search saved memories");
            var empty = ProfileNote("No matching entries.");
            void Filter()
            {
                _memoryFilter = search.Text;
                foreach (var choice in selector.Choices)
                    profileItems[choice.Value].Visibility = choice.Title.Contains(_memoryFilter, StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible : Visibility.Collapsed;
                empty.Visibility = profileItems.Values.Any(item => item.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
            }
            search.TextChanged += (_, _) => Filter();
            sidebarTop.Children.Add(search); sidebarTop.Children.Add(empty); Filter();
        }
        _profilePicker = picker;
        AutomationProperties.SetName(picker, selector.Title);
        AutomationProperties.SetHelpText(picker, selector.Description);
        Grid.SetRow(picker, 1); sidebar.Children.Add(picker);
        var add = actions.FirstOrDefault(a => a.Id == addId);
        if (add is not null)
        {
            var button = ProfileButton("+", () => RunProfileActionAsync(add, name, leaveProfile: true));
            AutomationProperties.SetName(button, add.Title);
            ToolTipService.SetToolTip(button, add.Title);
            button.MinWidth = 32; button.Padding = new(8, 4, 8, 4);
            Grid.SetColumn(button, 1); sidebarHeader.Children.Add(button);
        }
        sidebar.Visibility = singleConfiguration ? Visibility.Collapsed : Visibility.Visible;
        layout.Children.Add(sidebar);
        var content = new StackPanel { Spacing = 20, Margin = new(0, 0, 14, 12) };
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        var restoredOffset = _profileScrollOffset;
        scroll.Loaded += (_, _) => scroll.ChangeView(null, restoredOffset, null, true);
        scroll.ViewChanged += (_, _) => { if (generation == _generation) _profileScrollOffset = scroll.VerticalOffset; };
        AutomationProperties.SetName(scroll, "Settings for “" + name + "”");
        Grid.SetColumn(scroll, 1); layout.Children.Add(scroll);
        if (!generic && !string.IsNullOrWhiteSpace(selector.Description))
            content.Children.Add(ProfileNote(selector.Description));
        var connectionPanel = new StackPanel { Spacing = 12 };
        var modelsPanel = new StackPanel { Spacing = 12 };
        content.Children.Add(connectionPanel);
        var modelHeader = new Grid { ColumnSpacing = 8 };
        modelHeader.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        modelHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        modelHeader.Children.Add(new TextBlock { Text = "Models", FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var divider = new Border { Height = 1, Background = (Brush)Application.Current.Resources["HairlineBrush"] };
        content.Children.Add(divider);
        content.Children.Add(modelHeader); content.Children.Add(modelsPanel);
        if (!generic && editable.All(field => field.Section == PluginSettingsSection.Connection) &&
            actions.All(action => action.Section == PluginSettingsSection.Connection))
        {
            modelHeader.Visibility = Visibility.Collapsed;
            divider.Visibility = Visibility.Collapsed;
        }
        if (generic)
        {
            modelHeader.Visibility = Visibility.Collapsed;
            if (_models is LivePortableModelSettings modelSettings)
            {
                modelSettings.ShowLlmSummary = !editable.Any(f => f.Section == PluginSettingsSection.TextProcessing);
                modelSettings.TranscriptionModelSettingChoices = editable
                    .Where(f => f.Section == PluginSettingsSection.Transcription && f.Choices.Count > 0)
                    .Select(f => f.Choices.Select(c => c.Value).ToHashSet(StringComparer.Ordinal)).ToArray();
            }
            modelsPanel.Children.Add(_models);
        }

        var saveState = ProfileNote("");
        var fieldGroups = new Dictionary<string, FrameworkElement>();
        AutomationProperties.SetLiveSetting(saveState, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var save = ProfileButton(memoryEditor ? "Save memory" : singleConfiguration ? "Save settings" : "Save profile", async () =>
        {
            if (_busy || !_session.CanStartPluginSettingsAction || !IsLoaded || generation != _generation) return;
            _busy = true; IsEnabled = false;
            SetStatus("");
            try
            {
                string? error;
                if (generic)
                {
                    var changes = editable.Where(f => values[f.Id] != f.Value).ToDictionary(f => f.Id, f => values[f.Id]);
                    var result = await _session.SavePluginSettingsAsync(_id, changes, _pendingApiKey());
                    error = result.Error;
                    if (IsLoaded && generation == _generation)
                    {
                        foreach (var id in result.SavedFields) _drafts.Remove(id);
                        if (result.ApiKeySaved) _clearApiKey();
                        if (error is not null) { await ReloadAsync(); SetStatus(error); return; }
                    }
                }
                else error = await _session.SavePluginProfileSettingsAsync(_id, selector.Value, values, _pendingApiKey());
                if (!IsLoaded || generation != _generation) return;
                if (error is not null) { SetStatus("“" + name + "”: " + error); return; }
                foreach (var field in editable) _drafts.Remove(field.Id);
                _profileActionDrafts.Remove(selector.Value);
                _dirtyProfiles[selector.Value] = false;
                _clearApiKey();
                await ReloadAsync();
                SetStatus("“" + _profileName + "” saved.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (IsLoaded && generation == _generation) SetStatus("Could not save “" + name + "”. Your edits are still here; retry saving."); }
            finally { _busy = false; IsEnabled = true; if (_refreshRequested) RequestRefresh(); }
        });
        AutomationProperties.SetName(save, memoryEditor ? "Save memory" : singleConfiguration ? "Save settings" : "Save profile “" + name + "”");
        void UpdateDirty()
        {
            foreach (var field in editable)
                if (fieldGroups.TryGetValue(field.Id, out var group))
                    group.Visibility = field.VisibleWhen is not { } condition ||
                        values.TryGetValue(condition.SettingId, out var controllingValue) && condition.Values.Contains(controllingValue)
                        ? Visibility.Visible : Visibility.Collapsed;
            var dirty = _profileActionDrafts.Contains(selector.Value) || _pendingApiKey() is not null || editable.Any(f => values[f.Id] != f.Value);
            _dirtyProfiles[selector.Value] = dirty;
            foreach (var choice in selector.Choices)
                if (profileItems[choice.Value].Content is TextBlock label)
                    label.Text = choice.Title + (_dirtyProfiles.GetValueOrDefault(choice.Value) ? " *" : "");
            save.IsEnabled = dirty;
            var others = selector.Choices.Count(c => c.Value != selector.Value && _dirtyProfiles.GetValueOrDefault(c.Value));
            saveState.Text = dirty ? "Unsaved changes" : "Saved";
            if (others > 0) saveState.Text += " · Unsaved edits in " + others + (others == 1 ? " other profile" : " other profiles");
        }
        _profileDirtyChanged = UpdateDirty;
        ScriptCodeEditor? scriptEditor = null;
        var scriptLanguage = editable.FirstOrDefault(f => f.Id.EndsWith(":shell", StringComparison.Ordinal));
        PluginSettingsSection? previousSection = null;
        foreach (var field in editable)
        {
            var panel = field.Section == PluginSettingsSection.Connection ? connectionPanel : modelsPanel;
            if (field.Section != previousSection && field.Section != PluginSettingsSection.Connection)
                panel.Children.Add(new TextBlock { Text = field.Section switch { PluginSettingsSection.Transcription => "Transcription", PluginSettingsSection.Speech => "Speech", PluginSettingsSection.TextProcessing => "Text processing", _ => "Settings" },
                    FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new(0, 8, 0, 0) });
            previousSection = field.Section;
            var group = new StackPanel { Spacing = 5 };
            fieldGroups.Add(field.Id, group);
            group.Children.Add(SettingsHelp.Label(field.Title, field.Description));
            Control input;
            if (_id == "com.typewhisper.webhook" && field.Id.EndsWith(":workflows", StringComparison.Ordinal))
            {
                input = new WebhookWorkflowPicker(values[field.Id], field.MaxLength, value =>
                { values[field.Id] = value; _drafts[field.Id] = value; UpdateDirty(); });
            }
            else if (field.Choices.Count > 0)
            {
                var choice = new ComboBox { ItemsSource = field.Choices,
                    DisplayMemberPath = nameof(PluginSettingChoice.Title), SelectedValuePath = nameof(PluginSettingChoice.Value),
                    SelectedValue = values[field.Id], HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 40 };
                choice.SelectionChanged += (_, _) =>
                {
                    if (choice.SelectedValue is not string value) return;
                    values[field.Id] = value; _drafts[field.Id] = value; UpdateDirty();
                    if (field.Id == scriptLanguage?.Id && scriptEditor is not null) scriptEditor.SyntaxLanguage = value;
                };
                input = choice;
            }
            else if (field.Suggestions.Count > 0)
            {
                var suggested = new AutoSuggestBox { Text = values[field.Id],
                    ItemsSource = field.Suggestions, MinHeight = 40, MaxSuggestionListHeight = 240 };
                suggested.TextChanged += (_, _) =>
                {
                    values[field.Id] = suggested.Text; _drafts[field.Id] = suggested.Text; UpdateDirty();
                    suggested.ItemsSource = field.Suggestions.Where(s => s.Contains(suggested.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
                };
                suggested.SuggestionChosen += (_, e) => suggested.Text = (string)e.SelectedItem;
                input = suggested;
            }
            else if (_id == "com.typewhisper.script" && field.Id.EndsWith(":command", StringComparison.Ordinal))
            {
                var language = scriptLanguage is null ? "powershell" : values[scriptLanguage.Id];
                var editor = new ScriptCodeEditor(values[field.Id], language, field.MaxLength);
                scriptEditor = editor;
                editor.CodeChanged += value => { values[field.Id] = value; _drafts[field.Id] = value; UpdateDirty(); };
                var position = ProfileNote("Ln 1, Col 1 · Ctrl+Z undo · Ctrl+Y redo");
                editor.PositionChanged += (line, column) => position.Text = $"Ln {line}, Col {column} · Ctrl+Z undo · Ctrl+Y redo";
                editor.EditorNotice += message => position.Text = message;
                AutomationProperties.SetLiveSetting(position, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
                var toolbar = new Grid { Margin = new(0, 2, 0, 2) };
                toolbar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                toolbar.Children.Add(ProfileNote("SCRIPT · syntax highlighting"));
                var expand = ProfileButton("Expand", () => { editor.Height = editor.Height == 240 ? 440 : 240; return Task.CompletedTask; });
                expand.Click += (_, _) => expand.Content = editor.Height == 240 ? "Expand" : "Collapse";
                var format = ProfileButton("Format", () => { editor.FormatCode(); return Task.CompletedTask; });
                format.IsEnabled = language is "powershell" or "pwsh";
                editor.SyntaxLanguageChanged += () => format.IsEnabled = editor.SyntaxLanguage is "powershell" or "pwsh";
                ToolTipService.SetToolTip(format, "Add line breaks between PowerShell statements. Changes remain unsaved.");
                var editorActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                editorActions.Children.Add(format); editorActions.Children.Add(expand);
                Grid.SetColumn(editorActions, 1); toolbar.Children.Add(editorActions);
                group.Children.Add(toolbar);
                editor.Tag = position;
                input = editor;
            }
            else
            {
                var text = new TextBox { Text = values[field.Id], MaxLength = Math.Clamp(field.MaxLength, 1, 32768),
                    MinHeight = memoryEditor && field.IsMultiline ? 220 : 40, AcceptsReturn = field.IsMultiline,
                    Padding = field.IsMultiline ? new Thickness(10, 10, 4, 10) : new Thickness(10, 0, 4, 0),
                    TextWrapping = field.IsMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    Style = (Style)Application.Current.Resources[field.IsMultiline ? "LexiconMultilineStyle" : "SearchTextBoxStyle"] };
                text.TextChanged += (_, _) => { values[field.Id] = text.Text; _drafts[field.Id] = text.Text; UpdateDirty(); };
                input = text;
            }
            AutomationProperties.SetName(input, field.Title + " for “" + name + "”");
            AutomationProperties.SetHelpText(input, field.Description);
            var border = new Border { Child = input, CornerRadius = new(8), BorderThickness = new(1),
                Background = (Brush)Application.Current.Resources["SurfaceBrush"],
                BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"] };
            input.GotFocus += (_, _) => border.BorderBrush = (Brush)Application.Current.Resources["AccentBrush"];
            input.LostFocus += (_, _) => border.BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"];
            group.Children.Add(border);
            if (input is ScriptCodeEditor { Tag: TextBlock positionNote }) group.Children.Add(positionNote);
            panel.Children.Add(group);
        }
        if (showKey) connectionPanel.Children.Add(_credentials);
        foreach (var action in actions.Where(a => a.Id != addId && a.Id != removeId))
        {
            var button = ProfileButton(action.Title, async () =>
            {
                if (generic && (_dirtyProfiles.GetValueOrDefault(selector.Value) || _pendingApiKey() is not null))
                { SetStatus("Save your changes before running this action."); return; }
                await RunProfileActionAsync(action, name, profileId: generic ? null : selector.Value, values: values);
            });
            AutomationProperties.SetName(button, action.Title + " for “" + name + "”");
            ToolTipService.SetToolTip(button, action.Description);
            if (action.Section == PluginSettingsSection.Connection)
            {
                if (showKey)
                {
                    _connectionActions.Children.Add(button);
                    _profileConnectionButtons.Add(button);
                }
                else connectionPanel.Children.Add(button);
            }
            else if (generic)
            {
                modelsPanel.Children.Add(button);
            }
            else { Grid.SetColumn(button, 1); modelHeader.Children.Add(button); }
        }
        if (generic && connectionPanel.Children.Count == 0)
        { connectionPanel.Visibility = Visibility.Collapsed; divider.Visibility = Visibility.Collapsed; }
        var footerContent = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        footerContent.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footerContent.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var feedback = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        feedback.Children.Add(saveState);
        _content.Children.Remove(_status); feedback.Children.Add(_status);
        _status.FontSize = 12;
        footerContent.Children.Add(feedback);
        Grid.SetColumn(save, 1); footerContent.Children.Add(save);
        save.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"];
        var footer = new Border { Child = footerContent, Padding = new(0, 12, 14, 6),
            BorderThickness = new(0, 1, 0, 0), BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"] };
        Grid.SetColumn(footer, 1); Grid.SetRow(footer, 1); layout.Children.Add(footer);
        var remove = actions.FirstOrDefault(a => a.Id == removeId);
        if (remove is not null)
        {
            var removeButton = ProfileButton(remove.Title, async () =>
            {
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Remove “" + name + "”?",
                    Content = memoryEditor ? "This deletes the saved memory from this device. It will no longer be available to workflows." : showKey ? "This removes this configuration, its saved API key and any unsaved edits. Workflows using it will need another provider." : "This removes this configuration and any unsaved edits.",
                    PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await RunProfileActionAsync(remove, name, removedFields: editable.Select(f => f.Id).ToArray(), removedProfileId: selector.Value);
            });
            Grid.SetRow(removeButton, 2); sidebar.Children.Add(removeButton);
        }
        layout.SizeChanged += (_, e) =>
        {
            if (singleConfiguration)
            {
                layout.ColumnDefinitions[0].Width = new GridLength(0);
                layout.ColumnSpacing = 0;
                return;
            }
            var narrow = e.NewSize.Width < 620;
            layout.ColumnDefinitions[0].Width = narrow ? new GridLength(0) : new GridLength(185);
            layout.ColumnSpacing = narrow ? 0 : 22;
            Grid.SetColumn(sidebar, narrow ? 1 : 0);
            Grid.SetRow(scroll, narrow ? 1 : 0);
            Grid.SetRow(footer, narrow ? 2 : 1);
            layout.RowDefinitions[0].Height = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            layout.RowDefinitions[1].Height = narrow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            picker.MaxHeight = narrow ? 110 : double.PositiveInfinity;
        };
        UpdateDirty();
        Content = layout;

        var selecting = false;
        picker.SelectionChanged += async (_, _) =>
        {
            if (selecting || _busy || !picker.IsLoaded || generation != _generation ||
                picker.SelectedItem is not ListViewItem { Tag: string selected } || selected == selector.Value) return;
            selecting = true;
            try
            {
                if (!await _canLeaveConnection()) { picker.SelectedItem = profileItems[selector.Value]; return; }
                if (!IsLoaded || generation != _generation) return;
                _busy = true; IsEnabled = false;
                var error = await _session.SavePluginTextSettingAsync(_id, selector.Id, selected);
                if (!IsLoaded || generation != _generation) return;
                if (error is not null) { picker.SelectedItem = profileItems[selector.Value]; SetStatus(error); return; }
                SetStatus("");
                _profileScrollOffset = 0;
                await ReloadAsync();
                if (_profilePicker is { } currentPicker)
                {
                    if (currentPicker.IsLoaded) currentPicker.Focus(FocusState.Programmatic);
                    else currentPicker.Loaded += (_, _) => currentPicker.Focus(FocusState.Programmatic);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { if (IsLoaded && generation == _generation) { picker.SelectedItem = profileItems[selector.Value]; SetStatus("Could not switch profiles. Try again."); } }
            finally { selecting = _busy = false; IsEnabled = true; if (_refreshRequested) RequestRefresh(); }
        };
    }

    private async Task RunProfileActionAsync(PluginSettingsAction action, string name,
        bool leaveProfile = false, string[]? removedFields = null, string? profileId = null, IReadOnlyDictionary<string, string>? values = null, string? removedProfileId = null)
    {
        if (_busy || !IsLoaded) return;
        if (!_session.CanStartPluginSettingsAction)
        { SetStatus("Finish dictation and other plugin operations before changing this profile."); return; }
        var generation = _generation;
        if (leaveProfile && !await _canLeaveConnection()) return;
        if (!IsLoaded || generation != _generation) return;
        _busy = true; IsEnabled = false;
        SetStatus(action.Title + "…");
        var pendingApiKey = _pendingApiKey();
        var hasPendingChanges = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(_id == "com.typewhisper.script" && action.Id.StartsWith("test:", StringComparison.Ordinal) ? 6 : 3));
            void CancelForRecording() => timeout.Cancel();
            _session.RecordingStarting += CancelForRecording;
            string? result;
            try
            {
                result = await _session.PluginRuntime.UseConfigurationAsync(_id, async (plugin, ct) =>
                {
                    if (profileId is not null && values is not null && plugin is IPluginProfileSettings profiles)
                    {
                        var actionResult = await profiles.ExecuteProfileActionAsync(profileId, action.Id, values, pendingApiKey, ct);
                        hasPendingChanges = actionResult.HasPendingChanges;
                        return actionResult.Message;
                    }
                    if (plugin is not IPluginSettingsActions settings) throw new NotSupportedException();
                    return await settings.ExecuteSettingsActionAsync(action.Id, ct);
                }, timeout.Token, preserveCompletedResult: true);
            }
            finally { _session.RecordingStarting -= CancelForRecording; }
            if (!IsLoaded || generation != _generation) return;
            if (hasPendingChanges && profileId is not null) _profileActionDrafts.Add(profileId);
            if (removedFields is not null)
            {
                foreach (var field in removedFields) _drafts.Remove(field);
                if (removedProfileId is not null)
                {
                    _dirtyProfiles.Remove(removedProfileId);
                    _profileActionDrafts.Remove(removedProfileId);
                }
            }
            if (leaveProfile) _profileScrollOffset = 0;
            await ReloadAsync();
            SetStatus(leaveProfile ? result ?? "Profile added." : "“" + name + "”: " + (result ?? "Completed."));
            if (_id == "com.typewhisper.script" && action.Id.StartsWith("test:", StringComparison.Ordinal) &&
                result is not null && (result.StartsWith("Result: ", StringComparison.Ordinal) || result.StartsWith("Ergebnis: ", StringComparison.Ordinal)))
            {
                var output = result[(result.IndexOf(": ", StringComparison.Ordinal) + 2)..];
                var language = output.TrimStart().StartsWith('"') || output.TrimStart().StartsWith('{') ? "json" : "markdown";
                var preview = new ScriptCodeEditor(output, language, 32768, readOnly: true);
                AutomationProperties.SetName(preview, "Script test output");
                var panel = new StackPanel { Spacing = 10 };
                panel.Children.Add(ProfileNote("Sample output · up to 2,000 characters · draft not saved"));
                panel.Children.Add(preview);
                await new ContentDialog { XamlRoot = XamlRoot, Title = "Test result", Content = panel,
                    CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close }.ShowAsync();
            }
        }
        catch (OperationCanceledException)
        { if (IsLoaded && generation == _generation) SetStatus("The action was cancelled or timed out. You can retry."); }
        catch (ArgumentException ex)
        { if (IsLoaded && generation == _generation) SetStatus(ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsLoaded && generation == _generation) SetStatus("Could not complete “" + action.Title + "” for “" + name + "”. Check the plugin settings, then retry."); }
        finally { _busy = false; IsEnabled = true; if (_refreshRequested) RequestRefresh(); }
    }

    private static TextBlock ProfileNote(string text) => new() { Text = text, FontSize = 12,
        TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MutedBrush"] };

    private static HandCursorButton ProfileButton(string title, Func<Task> action)
    {
        var button = new HandCursorButton { Content = title, HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        button.Click += async (_, _) => await action();
        return button;
    }

}
