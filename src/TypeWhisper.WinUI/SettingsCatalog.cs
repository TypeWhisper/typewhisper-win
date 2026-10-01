using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

// Settings page layout, row tags and search index. Generic rows write only to the in-memory values dictionary;
// ConfigureLiveSettings replaces most of them with saved controls, and shortcut rows commit through the delegates passed to Render.
internal static partial class SettingsCatalog
{
    private sealed record Field(string Category, string Key, string Label, string Value, string Hint, string[]? Choices = null);
    private static Field Toggle(string c, string k, string label, bool value = false, string hint = "") => new(c, k, label, value ? "On" : "Off", hint.Length > 0 ? hint : k switch
    {
        "AutostartEnabled" => Loc.T("Open TypeWhisper when you sign in to Windows."),
        "AutoPaste" => Loc.T("Insert the finished transcript into the active text field."),
        "TranscribeShortQuietClipsAggressively" => Loc.T("Try to recognize very brief or quiet speech, even when detection is uncertain."),
        "TranscriptionNumberNormalizationEnabled" => Loc.T("Write spoken numbers as digits where appropriate."),
        "ShortUtterancePunctuationEnabled" => Loc.T("Add punctuation to brief dictations too."),
        "VocabularyBoostingEnabled" => Loc.T("Help the engine recognize words from your selected vocabulary packs."),
        "WhisperModeEnabled" => Loc.T("Boost quiet speech automatically."),
        "AudioDuckingEnabled" => Loc.T("Turn down other apps while you speak."),
        "PauseMediaDuringRecording" => Loc.T("Pause media playback during your recording."),
        "SoundFeedbackEnabled" => Loc.T("Play a short sound when recording starts or stops."),
        "SpokenFeedbackEnabled" => Loc.T("Read recording feedback aloud using the selected voice."),
        "SilenceAutoStopEnabled" => Loc.T("Finish the recording after a period without speech."),
        "RecorderMicEnabled" => Loc.T("Start new recorder sessions with your microphone enabled."),
        "RecorderSystemAudioEnabled" => Loc.T("Start new recorder sessions with sound from your computer enabled."),
        "RecorderTranscriptionEnabled" => Loc.T("Create a transcript when you finish a recorder session."),
        "DictationRecoveryAutomaticFallbackEnabled" => Loc.T("Try the recovery engine if the initial transcription fails."),
        "WorkflowRequestRecoveryEnabled" => Loc.T("Keep failed requests available so you can try them again."),
        "SaveToHistoryEnabled" => Loc.T("Keep completed transcripts available in History."),
        "SaveHistoryAudio" => Loc.T("Keep a local audio copy with new dictation entries. Audio follows history deletion and retention."),
        "MemoryEnabled" => Loc.T("Use personal context to help tailor future results."),
        "WatchFolderAutoStart" => Loc.T("Start watching your chosen folder when TypeWhisper opens."),
        "ApiServerRequiresAuthentication" => Loc.T("Require authentication before accepting API requests."),
        _ => ""
    }, ["Off", "On"]);
    private static Field Choice(string c, string k, string label, string value, string[] options, string hint = "") => new(c, k, label, value, hint, options);
    private static Field Text(string c, string k, string label, string value = "", string hint = "") => new(c, k, label, value, hint);
    private static readonly Field[] Fields =
    [
        Choice("General", "UiLanguage", Loc.T("App Language"), "English", ["English"]),
        Toggle("General", "AutostartEnabled", Loc.T("Start with Windows")),
        Choice("Account & about", "UpdateChannel", Loc.T("Update channel"), "Stable", [Loc.Mark("Stable"), Loc.Mark("Daily"), Loc.Mark("Release Candidate")]),

        Choice("Dictation", "Mode", Loc.T("Recording mode"), "Toggle", [Loc.Mark("Toggle"), Loc.Mark("Push to talk"), Loc.Mark("Hybrid")]),
        Choice("Advanced", "CancellationBehavior", Loc.T("Cancellation behavior"), "Double", [Loc.Mark("Double"), Loc.Mark("Single"), Loc.Mark("Instant")], Loc.T("Double: press Esc twice to cancel. Single: press Esc once. Both show a cancellation banner for 1.5 seconds. Instant: press Esc once without a banner. Applies to recording and processing.")),
        Choice("Dictation", "Language", Loc.T("Spoken language"), "Automatic", [Loc.Mark("Automatic"), Loc.Mark("English"), Loc.Mark("German"), Loc.Mark("French"), Loc.Mark("Spanish"), Loc.Mark("Italian")]),
        Choice("Dictation", "LanguageHints", Loc.T("Preferred languages"), "Unrestricted", [Loc.Mark("Unrestricted"), Loc.Mark("German and English"), Loc.Mark("German"), Loc.Mark("English"), Loc.Mark("French"), Loc.Mark("Spanish")], Loc.T("Sample language selection; no language codes required.")),
        Choice("Dictation", "TranscriptionTask", Loc.T("Task"), "Transcribe", [Loc.Mark("Transcribe"), Loc.Mark("Translate")]),
        Choice("Dictation", "TranslationTargetLanguage", Loc.T("Translate into"), "English", [Loc.Mark("English"), Loc.Mark("German"), Loc.Mark("French"), Loc.Mark("Spanish"), Loc.Mark("Italian")]),
        Toggle("Dictation", "AutoPaste", Loc.T("After recording"), true, Loc.T("Insert the text directly, or review the result first.")),
        Toggle("Dictation", "LockPasteToFocusedField", Loc.T("Paste only into the original field"), false, Loc.T("Used when automatic paste is enabled. Keep the target fixed when dictation starts.")),
        Choice("Advanced", "ModelAutoUnloadSeconds", Loc.T("Unload idle models"), "After 10 minutes", [Loc.Mark("Never"), Loc.Mark("Immediately"), Loc.Mark("After 2 minutes"), Loc.Mark("After 5 minutes"), Loc.Mark("After 10 minutes"), Loc.Mark("After 30 minutes"), Loc.Mark("After 1 hour")], Loc.T("Release the memory of local models after inactivity. They load again when needed.")),
        Toggle("Advanced", "TranscribeShortQuietClipsAggressively", Loc.T("Recognize short, quiet clips")),
        Toggle("Dictation", "TranscriptionNumberNormalizationEnabled", Loc.T("Normalize numbers"), true),
        Toggle("Dictation", "ShortUtterancePunctuationEnabled", Loc.T("Punctuate short phrases"), true),
        Choice("Dictation", "EnglishOutputVariant", Loc.T("English spelling"), "As transcribed", [Loc.Mark("As transcribed"), Loc.Mark("American"), Loc.Mark("British")]),
        Choice("Dictation", "GermanOutputVariant", Loc.T("German spelling"), "As transcribed", [Loc.Mark("As transcribed"), Loc.Mark("Germany"), Loc.Mark("Switzerland")]),
        Toggle("Dictation", "VocabularyBoostingEnabled", Loc.T("Vocabulary boosting")),
        Choice("Dictation", "VocabularyBoostingEnabledPackIds", Loc.T("Vocabulary packs"), "None", [Loc.Mark("None"), Loc.Mark("Sample technical vocabulary"), Loc.Mark("Sample medical vocabulary")], Loc.T("Sample packs. Manage your own words in Dictionary.")),
        Choice("Dictation", "VocabularyBoostingSelectedIndustryPresetId", Loc.T("Industry"), "General", [Loc.Mark("General"), Loc.Mark("Technology"), Loc.Mark("Medicine"), Loc.Mark("Legal")], Loc.T("Sample industry presets.")),
        Choice("Dictation", "SpokenFormattingProfiles", Loc.T("Spoken formatting"), "Engine defaults", [Loc.Mark("Engine defaults"), Loc.Mark("Sample punctuation rules")], Loc.T("Engine-specific formatting is a sample here.")),

        Choice("Audio", "SelectedMicrophoneDevice", Loc.T("Microphone"), "System default", [Loc.Mark("System default"), Loc.Mark("Sample USB microphone"), Loc.Mark("Sample headset")], Loc.T("Device choices are samples.")),
        Text("Audio", "MicrophonePriorityList", Loc.T("Microphone fallback order"), "", Loc.T("Preferred device names in order; sample configuration only.")),
        Toggle("Advanced", "WhisperModeEnabled", Loc.T("Whisper mode")),
        Toggle("Audio", "AudioDuckingEnabled", Loc.T("Lower other audio while recording")),
        Choice("Audio", "AudioDuckingLevel", Loc.T("Other audio volume"), "20%", ["0%", "10%", "20%", "30%", "50%", "75%"]),
        Toggle("Audio", "PauseMediaDuringRecording", Loc.T("Pause media during recording")),
        Toggle("Audio", "SoundFeedbackEnabled", Loc.T("Sound feedback"), true),
        Toggle("Advanced", "SpokenFeedbackEnabled", Loc.T("Spoken feedback")),
        Choice("Advanced", "SpokenFeedbackProviderId", Loc.T("Spoken feedback provider"), "Windows speech", [Loc.Mark("Windows speech"), Loc.Mark("Sample plugin")]),
        Text("Advanced", "SpokenFeedbackVoiceId", Loc.T("Voice"), "System default"),
        Toggle("Audio", "SilenceAutoStopEnabled", Loc.T("Stop after silence")),
        Choice("Audio", "SilenceAutoStopSeconds", Loc.T("Silence timeout"), "10 seconds", [Loc.Mark("3 seconds"), Loc.Mark("5 seconds"), Loc.Mark("10 seconds"), Loc.Mark("15 seconds"), Loc.Mark("30 seconds")]),

        Text("Shortcuts", "MainDictationHotkeys", Loc.T("Main dictation"), LocalDictationSession.DefaultShortcut),
        Text("Shortcuts", "CancelProcessingHotkeys", Loc.T("Cancel processing"), "", Loc.T("Cancel final dictation processing or an active selected-text workflow. Does nothing while idle or recording.")),
        Text("Shortcuts", "PushToTalkHotkey", Loc.T("Push to talk")),
        Text("Shortcuts", "ToggleOnlyHotkeys", Loc.T("Toggle recording")),
        Text("Shortcuts", "HoldOnlyHotkeys", Loc.T("Hold to record")),
        Text("Shortcuts", "RecentTranscriptionsHotkeys", Loc.T("Recent transcriptions")),
        Text("Shortcuts", "CopyLastTranscriptionHotkeys", Loc.T("Copy last transcription")),
        Text("Shortcuts", "PasteLastTranscriptionHotkeys", Loc.T("Paste last transcription")),
        Text("Shortcuts", "ReadLastTranscriptionHotkeys", Loc.T("Read last transcription")),
        Text("Shortcuts", "WorkflowPaletteHotkeys", Loc.T("Workflow palette")),
        Text("Shortcuts", "RecorderToggleHotkeys", Loc.T("Recorder")),


        Choice("Live text", "LiveTextPlacement", Loc.T("Live text position"), "Attached to recording", [Loc.Mark("Attached to recording"), Loc.Mark("Floating window")], Loc.T("Float the existing live text and drag its header to move it independently of the recording indicator.")),
        Choice("Live text", "LiveTranscriptionFontSize", Loc.T("Text size"), "12", ["10", "11", "12", "13", "14", "15", "16", "17", "18"], Loc.T("Size of the live transcript and completed result in the overlay.")),
        Toggle("Live text", "OnlineAsrBatchLiveTranscriptionEnabled", Loc.T("Live text for online batch engines"), false, Loc.T("Availability depends on the selected engine.")),
        Choice("Live text", "PreviewBubbleAutoHideMilliseconds", Loc.T("Result preview duration"), "1.5 seconds", [Loc.Mark("Immediately"), Loc.Mark("0.5 seconds"), Loc.Mark("1 second"), Loc.Mark("1.5 seconds"), Loc.Mark("2 seconds"), Loc.Mark("3 seconds"), Loc.Mark("5 seconds")], Loc.T("After successful paste. Review windows stay open; errors remain visible for five seconds.")),

        Choice("Recorder", "RecorderSystemAudioDeviceId", Loc.T("System audio device"), "System default", [Loc.Mark("System default")], Loc.T("Select a real audio output in Recorder settings.")),
        Toggle("Recorder", "RecorderMicEnabled", Loc.T("Microphone on by default"), true),
        Toggle("Recorder", "RecorderSystemAudioEnabled", Loc.T("System audio on by default")),
        Choice("Recorder", "RecorderOutputFormat", Loc.T("Audio format"), "WAV", ["WAV"], Loc.T("16 kHz mono WAV recording.")),
        Choice("Recorder", "RecorderTrackMode", Loc.T("Tracks"), "Mixed", [Loc.Mark("Mixed")], Loc.T("Microphone and system audio are mixed into one mono track.")),

        Text("Files & recovery", "FileTranscriptionEngineOverride", Loc.T("File transcription engine"), "", Loc.T("Empty uses the default engine.")),
        Text("Files & recovery", "FileTranscriptionModelOverride", Loc.T("File transcription model")),
        Choice("Files & recovery", "DictationRecoveryRetentionDays", Loc.T("Keep recovery audio"), "30 days", [Loc.Mark("Delete immediately"), Loc.Mark("7 days"), Loc.Mark("30 days"), Loc.Mark("90 days"), Loc.Mark("Forever")]),
        Toggle("Files & recovery", "DictationRecoveryAutomaticFallbackEnabled", Loc.T("Automatic transcription fallback")),
        Text("Files & recovery", "DictationRecoveryEngineId", Loc.T("Recovery engine")),
        Text("Files & recovery", "DictationRecoveryModelId", Loc.T("Recovery model")),
        Choice("Files & recovery", "DictationRecoveryLanguage", Loc.T("Recovery language"), "Automatic", [Loc.Mark("Automatic"), Loc.Mark("English"), Loc.Mark("German"), Loc.Mark("French"), Loc.Mark("Spanish")]),
        Choice("Files & recovery", "DictationRecoveryTask", Loc.T("Recovery task"), "Transcribe", [Loc.Mark("Transcribe"), Loc.Mark("Translate")]),
        Toggle("Files & recovery", "WorkflowRequestRecoveryEnabled", Loc.T("Recover failed workflow requests"), true),

        Toggle("Privacy", "SaveToHistoryEnabled", Loc.T("Save to history"), true),
        Toggle("Privacy", "SaveHistoryAudio", Loc.T("Keep dictation audio")),
        Choice("Privacy", "HistoryRetentionMode", Loc.T("History retention"), "For a duration", [Loc.Mark("For a duration"), Loc.Mark("Forever"), Loc.Mark("Until the app closes")]),
        Choice("Privacy", "HistoryRetentionMinutes", Loc.T("Keep history for"), "90 days", [Loc.Mark("1 day"), Loc.Mark("7 days"), Loc.Mark("30 days"), Loc.Mark("90 days"), Loc.Mark("180 days")]),
        Toggle("Privacy", "MemoryEnabled", Loc.T("Personal memory")),


    ];

    internal static readonly string[] Categories = ["General", "Dictation", "Audio", "Shortcuts", "Live text", "Recorder", "Files & recovery", "Privacy", "Premium", "Account & about"];

    internal static IEnumerable<SettingSearchEntry> SearchEntries => Fields.Select(setting => new SettingSearchEntry(
        setting.Category == "Live text" ? "Appearance" : setting.Category, setting.Key, setting.Label, setting.Hint,
        ChoiceIcon(setting), string.Join(' ', setting.Choices ?? []))).Append(new(
            "Dictation", "DictationModel", Loc.T("Dictation model"), Loc.T("Choose the active dictation model. Downloads and credentials are managed in plugin settings."), "chip", "engine default downloaded"));

    private static TextBlock Label(string text, double size = 13, bool muted = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources[muted ? "MutedBrush" : "TextBrush"]
    };

    internal static IEnumerable<(string Key, string Label, string Value)> ShortcutBindings(Dictionary<string, string> values) =>
        Fields.Where(field => field.Category == "Shortcuts").Select(field => (field.Key, field.Label, values.GetValueOrDefault(field.Key, field.Value)));

    internal static void Render(string category, StackPanel target, Dictionary<string, string> values, List<ChoicePicker> pickers, Action? refresh = null, Func<string, string?>? commitDictationHotkeys = null, Func<string, string?>? commitCancelProcessingHotkeys = null, Func<string, string?>? commitRecentTranscriptionsHotkeys = null, Func<string, string?>? commitCopyLastTranscriptionHotkeys = null, Func<string, string?>? commitPasteLastTranscriptionHotkeys = null, Func<string, string?>? commitReadLastTranscriptionHotkeys = null, Func<string, string?>? commitWorkflowPaletteHotkeys = null, Func<string, string, string?>? commitRecordingShortcut = null, Func<string, string?>? commitRecorderHotkeys = null)
    {
        target.Children.Clear();
        var title = Label(SettingsWindow.DisplayName(category), 24);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        titleRow.Children.Add(title);
        if (category == "Shortcuts")
            titleRow.Children.Add(SettingsHelp.Button(Loc.T(category), Loc.T("These global shortcuts are saved. Configure selected-text shortcuts in Workflows. Disabled shortcut controls are unavailable.")));
        else if (category == "Privacy")
            titleRow.Children.Add(SettingsHelp.Button(Loc.T(category), Loc.T("Settings are saved automatically. History retention changes take effect when you choose Apply retention. Unavailable controls are disabled.")));
        else if (category is not "Premium" and not "Account & about" and not "Dictation")
            titleRow.Children.Add(SettingsHelp.Button(Loc.T(category), Loc.T("Settings are saved automatically. Unavailable controls are disabled.")));
        target.Children.Add(titleRow);
        if (category == "Premium")
        {
            target.Children.Add(new PremiumView(pickers));
            return;
        }
        if (category == "Account & about")
        {
            target.Children.Add(new AccountView(values, pickers));
            return;
        }

        if (category == "Shortcuts")
        {
            var guide = Label(Loc.T("Your actions, your keys. Add alternatives with + or click a key to change it."), 13, true);
            ToolTipService.SetToolTip(guide, Loc.T("Main dictation and cancel processing shortcuts are global and saved automatically. Cancel processing requires a main key and cancels final dictation processing or an active selected-text workflow. Configure selected-text shortcuts in Workflows. Disabled actions here are unavailable."));
            target.Children.Add(guide);
            var list = new StackPanel { Spacing = 24 };
            (string Title, string[] Keys)[] groups =
            [
                (Loc.T("Dictation"), ["MainDictationHotkeys", "CancelProcessingHotkeys", "PushToTalkHotkey", "ToggleOnlyHotkeys", "HoldOnlyHotkeys"]),
                (Loc.T("Recent transcriptions"), ["RecentTranscriptionsHotkeys", "CopyLastTranscriptionHotkeys", "PasteLastTranscriptionHotkeys", "ReadLastTranscriptionHotkeys"]),
                (Loc.T("Workflow palette"), ["WorkflowPaletteHotkeys"]),
                (Loc.T("Recorder"), ["RecorderToggleHotkeys"])
            ];
            foreach (var group in groups)
            {
                var section = new StackPanel { Spacing = 10 };
                var heading = Label(group.Title, 14);
                heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
                section.Children.Add(heading);
                var rows = new StackPanel();
                foreach (var key in group.Keys)
                {
                    if (rows.Children.Count > 0) rows.Children.Add(new Border
                    {
                        Height = 1, Margin = new Thickness(16, 0, 16, 0),
                        Background = (Brush)Application.Current.Resources["HairlineBrush"]
                    });
                    var field = Fields.Single(f => f.Key == key);
                    // Preserve field tags so search results still scroll to the exact action.
                    var item = new StackPanel { Tag = field.Key };
                    var commit = field.Key switch {
                        "MainDictationHotkeys" => commitDictationHotkeys, "CancelProcessingHotkeys" => commitCancelProcessingHotkeys,
                        "RecentTranscriptionsHotkeys" => commitRecentTranscriptionsHotkeys,
                        "CopyLastTranscriptionHotkeys" => commitCopyLastTranscriptionHotkeys,
                        "PasteLastTranscriptionHotkeys" => commitPasteLastTranscriptionHotkeys,
                        "ReadLastTranscriptionHotkeys" => commitReadLastTranscriptionHotkeys,
                        "WorkflowPaletteHotkeys" => commitWorkflowPaletteHotkeys,
                        "RecorderToggleHotkeys" => commitRecorderHotkeys,
                        "PushToTalkHotkey" or "ToggleOnlyHotkeys" or "HoldOnlyHotkeys" => commitRecordingShortcut is null ? null : value => commitRecordingShortcut(field.Key, value), _ => null };
                    item.Children.Add(new ShortcutRecorder(field.Key, field.Label, field.Value, values,
                        () => Fields.Where(f => f.Category == "Shortcuts").Select(f =>
                            (f.Key, f.Label, values.GetValueOrDefault(f.Key, f.Value))), commit) { IsEnabled = commit is not null });
                    rows.Children.Add(item);
                }
                section.Children.Add(new Border
                {
                    Child = rows, Padding = new Thickness(2), CornerRadius = new CornerRadius(10),
                    Background = (Brush)Application.Current.Resources["SurfaceBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"], BorderThickness = new Thickness(1)
                });
                list.Children.Add(section);
            }
            target.Children.Add(list);
            return;
        }
        if (category == "Dictation")
        {
            RenderDictation(target, values, pickers);
            return;
        }
        if (category == "Audio")
        {
            RenderAudio(target, values, pickers);
            return;
        }
        RenderFields(Fields.Where(f => f.Category == category), target, values, pickers, refresh);
    }

    internal static void RenderLiveTextOptions(StackPanel target, Dictionary<string, string> values, List<ChoicePicker> pickers)
    {
        RenderFields(Fields.Where(f => f.Category == "Live text" && f.Key != "OnlineAsrBatchLiveTranscriptionEnabled"), target, values, pickers, null);
    }

    private static void RenderAudio(StackPanel target, Dictionary<string, string> values, List<ChoicePicker> pickers)
    {
        // Preserve every existing preference, but reveal dependent fields in place.
        // Toggling an option must not rebuild the page or discard an open picker.
        void FieldsInto(StackPanel panel, params string[] keys) => RenderFields(
            keys.Select(key => Fields.Single(f => f.Key == key)), panel, values, pickers, null);
        StackPanel Group(params string[] keys)
        {
            var panel = new StackPanel { Spacing = 16 };
            FieldsInto(panel, keys);
            return panel;
        }
        StackPanel Conditional(string key, params string[] dependentKeys)
        {
            var panel = new StackPanel { Spacing = 16 };
            var details = Group(dependentKeys);
            void Update() => details.Visibility = values.GetValueOrDefault(key, "Off") == "On" ? Visibility.Visible : Visibility.Collapsed;
            RenderFields(Fields.Where(f => f.Key == key), panel, values, pickers, Update);
            Update();
            panel.Children.Add(details);
            return panel;
        }

        FieldsInto(target, "SelectedMicrophoneDevice", "SoundFeedbackEnabled", "WhisperModeEnabled");
        target.Children.Add(Conditional("AudioDuckingEnabled", "AudioDuckingLevel"));
        FieldsInto(target, "PauseMediaDuringRecording");
        target.Children.Add(Conditional("SpokenFeedbackEnabled", "SpokenFeedbackProviderId", "SpokenFeedbackVoiceId"));
        target.Children.Add(Conditional("SilenceAutoStopEnabled", "SilenceAutoStopSeconds"));
        FieldsInto(target, "MicrophonePriorityList");
    }

    private static void RenderFields(IEnumerable<Field> fields, StackPanel target, Dictionary<string, string> values, List<ChoicePicker> pickers, Action? refresh)
    {
        foreach (var field in fields)
        {
            var value = values.GetValueOrDefault(field.Key, field.Value);
            var stack = new StackPanel { Spacing = 8, Tag = field.Key };
            if (field.Category == "Shortcuts")
            {
                stack.Children.Add(new ShortcutRecorder(field.Key, field.Label, field.Value, values,
                    () => Fields.Where(setting => setting.Category == "Shortcuts").Select(setting =>
                        (setting.Key, setting.Label, values.GetValueOrDefault(setting.Key, setting.Value)))));
            }
            else if (field.Choices is ["Off", "On"])
            {
                var row = new Grid { ColumnSpacing = 16 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                copy.Children.Add(SettingsHelp.Label(field.Label, field.Hint));
                row.Children.Add(copy);
                var toggle = AppToggleSwitch.Create(value == "On");
                toggle.Toggled += (_, _) =>
                {
                    values[field.Key] = toggle.IsOn ? "On" : "Off";
                    refresh?.Invoke();
                };
                AutomationProperties.SetName(toggle, $"Preference {field.Key}");
                AutomationProperties.SetHelpText(toggle, $"{field.Label}. {field.Hint}");
                Grid.SetColumn(toggle, 1); row.Children.Add(toggle); stack.Children.Add(row);
            }
            else if (field.Choices is not null)
            {
                stack.Children.Add(SettingsHelp.Label(field.Label, field.Hint));
                var picker = new ChoicePicker { Tag = field.Key };
                picker.Configure(field.Label, ChoiceIcon(field), $"Preference {field.Key}");
                picker.SetOptions(field.Choices.Select(v => new Choice(v, Loc.T(v), Loc.T("Session-only setting"))).ToArray(), value);
                picker.SelectionChanged += selected =>
                {
                    values[field.Key] = selected;
                    if (field.Category == "Dictation") refresh?.Invoke();
                };
                pickers.Add(picker); stack.Children.Add(picker);
            }
            else
            {
                stack.Children.Add(SettingsHelp.Label(field.Label, field.Hint));
                var input = new TextBox { Text = value, MinHeight = 40, Style = (Style)Application.Current.Resources["SearchTextBoxStyle"] };
                AutomationProperties.SetName(input, $"Preference {field.Key}");
                AutomationProperties.SetHelpText(input, field.Label);
                input.TextChanged += (_, _) => values[field.Key] = input.Text;
                stack.Children.Add(new Border { Background = (Brush)Application.Current.Resources["SurfaceBrush"], BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"], BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Child = input });
            }
            if (field.Hint.Length > 0 && field.Category == "Shortcuts")
                stack.Children.Add(SettingsHelp.Button(field.Label, field.Hint));
            target.Children.Add(stack);
            if (field.Category != "Shortcuts") target.Children.Add(new Border { Tag = "SettingSeparator", Height = 1, Background = (Brush)Application.Current.Resources["HairlineBrush"] });
        }
    }

    // Follow the visible content, including expanded/collapsed dependent fields.
    // Keeping the separator in the tree lets it reappear when another row is shown.
    internal static void UpdateTrailingSeparators(StackPanel root)
    {
        bool Visit(StackPanel panel, bool followingContent)
        {
            var hasContent = false;
            for (var index = panel.Children.Count - 1; index >= 0; index--)
            {
                var child = panel.Children[index];
                if (child is Border { Tag: "SettingSeparator" } separator)
                {
                    var visibility = followingContent ? Visibility.Visible : Visibility.Collapsed;
                    if (separator.Visibility != visibility) separator.Visibility = visibility;
                    continue;
                }
                if (child.Visibility != Visibility.Visible) continue;
                var contributes = child is StackPanel nested ? Visit(nested, followingContent) : true;
                followingContent |= contributes;
                hasContent |= contributes;
            }
            return hasContent;
        }
        Visit(root, false);
    }

    // Variant 1 is the selected shared pattern; icons identify the setting, not its current value.
    private static string ChoiceIcon(Field field) => field.Key switch
    {
        _ when field.Category == "Shortcuts" => "keyboard",
        "SelectedMicrophoneDevice" => "microphone",
        "RecorderSystemAudioDeviceId" or "AudioDuckingLevel" or "SpokenFeedbackProviderId" => "speaker",
        "UiLanguage" or "Language" or "LanguageHints" or "TranslationTargetLanguage"
            or "EnglishOutputVariant" or "GermanOutputVariant" or "DictationRecoveryLanguage"
            or "WatchFolderLanguage" or "VocabularyBoostingEnabledPackIds"
            or "VocabularyBoostingSelectedIndustryPresetId" => "dictionary",
        "SelectedModelId" or "LocalModelAcceleration" => "chip",
        "DefaultLlmProvider" => "plugin",
        "SilenceAutoStopSeconds" or "ModelAutoUnloadSeconds" or "PreviewBubbleAutoHideMilliseconds"
            or "DictationRecoveryRetentionDays" or "HistoryRetentionMode" or "HistoryRetentionMinutes" => "history",
        "LiveTranscriptionFontSize" or "SpokenFormattingProfiles" => "text",
        "RecorderOutputFormat" or "WatchFolderOutputFormat" => "file",
        "TranscriptionTask" or "RecorderTranscriptionTask" or "DictationRecoveryTask" => "workflow",
        "RecorderTrackMode" => "layout",
        "Mode" or "CancellationBehavior" => "microphone",
        "RecorderMicDuckingMode" => "speaker",
        _ => "settings"
    };
}
