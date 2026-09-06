using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

// UI inventory of the existing AppSettings and SettingsViewModel, not a settings migration.
// Values live only in the launcher's session dictionary. No production services are called.
internal static partial class PrototypeSettingsCatalog
{
    private sealed record Field(string Category, string Key, string Label, string Value, string Hint, string[]? Choices = null);
    private static Field Toggle(string c, string k, string label, bool value = false, string hint = "") => new(c, k, label, value ? "On" : "Off", hint.Length > 0 ? hint : k switch
    {
        "AutostartEnabled" => "Open TypeWhisper when you sign in to Windows.",
        "AutoPaste" => "Insert the finished transcript into the active text field.",
        "TranscribeShortQuietClipsAggressively" => "Try to recognize very brief or quiet speech, even when detection is uncertain.",
        "TranscriptionNumberNormalizationEnabled" => "Write spoken numbers as digits where appropriate.",
        "ShortUtterancePunctuationEnabled" => "Add punctuation to brief dictations too.",
        "VocabularyBoostingEnabled" => "Help the engine recognize words from your selected vocabulary packs.",
        "WhisperModeEnabled" => "Boost quiet speech automatically.",
        "AudioDuckingEnabled" => "Turn down other apps while you speak.",
        "PauseMediaDuringRecording" => "Pause media playback during your recording.",
        "SoundFeedbackEnabled" => "Play a short sound when recording starts or stops.",
        "SpokenFeedbackEnabled" => "Read recording feedback aloud using the selected voice.",
        "SilenceAutoStopEnabled" => "Finish the recording after a period without speech.",
        "RecorderMicEnabled" => "Start new recorder sessions with your microphone enabled.",
        "RecorderSystemAudioEnabled" => "Start new recorder sessions with sound from your computer enabled.",
        "RecorderTranscriptionEnabled" => "Create a transcript when you finish a recorder session.",
        "DictationRecoveryAutomaticFallbackEnabled" => "Try the recovery engine if the initial transcription fails.",
        "WorkflowRequestRecoveryEnabled" => "Keep failed requests available so you can try them again.",
        "SaveToHistoryEnabled" => "Keep completed transcripts available in History.",
        "MemoryEnabled" => "Use personal context to help tailor future results.",
        "WatchFolderAutoStart" => "Start watching your chosen folder when TypeWhisper opens.",
        "ApiServerRequiresAuthentication" => "Require authentication before accepting API requests.",
        _ => ""
    }, ["Off", "On"]);
    private static Field Choice(string c, string k, string label, string value, string options, string hint = "") => new(c, k, label, value, hint, options.Split('|'));
    private static Field Text(string c, string k, string label, string value = "", string hint = "") => new(c, k, label, value, hint);
    private static readonly Field[] Fields =
    [
        Choice("General", "UiLanguage", "Interface language", "System", "System|English|Deutsch"),
        Toggle("General", "AutostartEnabled", "Start with Windows"),
        Choice("General", "UpdateChannel", "Update channel", "Installed channel", "Installed channel|Stable|Preview"),

        Choice("Dictation", "Mode", "Recording mode", "Toggle", "Toggle|Push to talk|Hybrid"),
        Choice("Dictation", "Language", "Spoken language", "Automatic", "Automatic|English|German|French|Spanish|Italian"),
        Choice("Dictation", "LanguageHints", "Preferred languages", "Unrestricted", "Unrestricted|German and English|German|English|French|Spanish", "Sample language selection; no language codes required."),
        Choice("Dictation", "TranscriptionTask", "Task", "Transcribe", "Transcribe|Translate"),
        Choice("Dictation", "TranslationTargetLanguage", "Translate into", "English", "English|German|French|Spanish|Italian"),
        Toggle("Dictation", "AutoPaste", "After recording", true, "Insert the text directly, or review the result first."),
        Toggle("Dictation", "LockPasteToFocusedField", "Paste only into the original field", false, "Used when automatic paste is enabled. Keep the target fixed when dictation starts."),
        Toggle("Dictation", "TranscribeShortQuietClipsAggressively", "Recognize short, quiet clips"),
        Toggle("Dictation", "TranscriptionNumberNormalizationEnabled", "Normalize numbers", true),
        Toggle("Dictation", "ShortUtterancePunctuationEnabled", "Punctuate short phrases", true),
        Choice("Dictation", "EnglishOutputVariant", "English spelling", "As transcribed", "As transcribed|American|British"),
        Choice("Dictation", "GermanOutputVariant", "German spelling", "As transcribed", "As transcribed|Germany|Switzerland"),
        Toggle("Dictation", "VocabularyBoostingEnabled", "Vocabulary boosting"),
        Choice("Dictation", "VocabularyBoostingEnabledPackIds", "Vocabulary packs", "None", "None|Sample technical vocabulary|Sample medical vocabulary", "Sample packs. Manage your own words in Quick Launch."),
        Choice("Dictation", "VocabularyBoostingSelectedIndustryPresetId", "Industry", "General", "General|Technology|Medicine|Legal", "Sample industry presets."),
        Choice("Dictation", "SpokenFormattingProfiles", "Spoken formatting", "Engine defaults", "Engine defaults|Sample punctuation rules", "Engine-specific formatting is a sample here."),

        Choice("Audio", "SelectedMicrophoneDevice", "Microphone", "System default", "System default|Sample USB microphone|Sample headset", "Device choices are samples."),
        Text("Audio", "MicrophonePriorityList", "Microphone fallback order", "", "Preferred device names in order; sample configuration only."),
        Toggle("Audio", "WhisperModeEnabled", "Whisper mode"),
        Toggle("Audio", "AudioDuckingEnabled", "Lower other audio while recording"),
        Choice("Audio", "AudioDuckingLevel", "Other audio volume", "20%", "0%|10%|20%|30%|50%|75%"),
        Toggle("Audio", "PauseMediaDuringRecording", "Pause media during recording"),
        Toggle("Audio", "SoundFeedbackEnabled", "Sound feedback", true),
        Toggle("Audio", "SpokenFeedbackEnabled", "Spoken feedback"),
        Choice("Audio", "SpokenFeedbackProviderId", "Spoken feedback provider", "Windows speech", "Windows speech|Sample plugin"),
        Text("Audio", "SpokenFeedbackVoiceId", "Voice", "System default"),
        Toggle("Audio", "SilenceAutoStopEnabled", "Stop after silence"),
        Choice("Audio", "SilenceAutoStopSeconds", "Silence timeout", "10 seconds", "3 seconds|5 seconds|10 seconds|15 seconds|30 seconds"),

        Text("Shortcuts", "MainDictationHotkeys", "Main dictation", "Ctrl+Shift+F9"),
        Text("Shortcuts", "QuickLaunchHotkeys", "Quick Launch", "Alt+Space"),
        Text("Shortcuts", "PushToTalkHotkey", "Push to talk"),
        Text("Shortcuts", "ToggleOnlyHotkeys", "Toggle recording"),
        Text("Shortcuts", "HoldOnlyHotkeys", "Hold to record"),
        Text("Shortcuts", "RecentTranscriptionsHotkeys", "Recent transcriptions"),
        Text("Shortcuts", "CopyLastTranscriptionHotkeys", "Copy last transcription"),
        Text("Shortcuts", "WorkflowPaletteHotkeys", "Workflow palette"),
        Text("Shortcuts", "RecorderToggleHotkeys", "Recorder"),


        Choice("Live text", "LiveTranscriptionFontSize", "Text size", "12", "10|11|12|13|14|15|16|17|18", "Catalog preview only; not connected to the overlay yet."),
        Toggle("Live text", "OnlineAsrBatchLiveTranscriptionEnabled", "Live text for online batch engines", false, "Availability depends on the selected engine."),
        Choice("Live text", "PreviewBubbleAutoHideMilliseconds", "Result preview duration", "1.5 seconds", "Keep visible|0.5 seconds|1 second|1.5 seconds|2 seconds|3 seconds|5 seconds"),

        Choice("Recorder", "RecorderSystemAudioDeviceId", "System audio device", "System default", "System default|Sample speakers|Sample headset"),
        Toggle("Recorder", "RecorderMicEnabled", "Microphone on by default", true),
        Toggle("Recorder", "RecorderSystemAudioEnabled", "System audio on by default"),
        Choice("Recorder", "RecorderOutputFormat", "Audio format", "WAV", "WAV|MP3|FLAC|M4A"),
        Choice("Recorder", "RecorderTrackMode", "Tracks", "Mixed", "Mixed|Separate"),
        Choice("Recorder", "RecorderMicDuckingMode", "Microphone ducking", "Aggressive", "Off|Gentle|Aggressive"),
        Toggle("Recorder", "RecorderTranscriptionEnabled", "Transcribe after recording", true),
        Choice("Recorder", "RecorderTranscriptionTask", "Task", "Transcribe", "Transcribe|Translate"),
        Text("Recorder", "RecorderTranslationTargetLanguage", "Translation language"),
        Text("Recorder", "RecorderTranscriptionEngineOverride", "Engine override", "", "Empty uses the default engine."),
        Text("Recorder", "RecorderTranscriptionModelOverride", "Model override", "", "Empty uses the default model."),

        Text("Files & recovery", "FileTranscriptionEngineOverride", "File transcription engine", "", "Empty uses the default engine."),
        Text("Files & recovery", "FileTranscriptionModelOverride", "File transcription model"),
        Choice("Files & recovery", "DictationRecoveryRetentionDays", "Keep recovery audio", "30 days", "Delete immediately|7 days|30 days|90 days|Forever"),
        Toggle("Files & recovery", "DictationRecoveryAutomaticFallbackEnabled", "Automatic transcription fallback"),
        Text("Files & recovery", "DictationRecoveryEngineId", "Recovery engine"),
        Text("Files & recovery", "DictationRecoveryModelId", "Recovery model"),
        Choice("Files & recovery", "DictationRecoveryLanguage", "Recovery language", "Automatic", "Automatic|English|German|French|Spanish"),
        Choice("Files & recovery", "DictationRecoveryTask", "Recovery task", "Transcribe", "Transcribe|Translate"),
        Toggle("Files & recovery", "WorkflowRequestRecoveryEnabled", "Recover failed workflow requests", true),

        Toggle("Privacy", "SaveToHistoryEnabled", "Save to history", true),
        Choice("Privacy", "HistoryRetentionMode", "History retention", "For a duration", "For a duration|Forever|Until the app closes"),
        Choice("Privacy", "HistoryRetentionMinutes", "Keep history for", "90 days", "1 day|7 days|30 days|90 days|180 days"),
        Toggle("Privacy", "MemoryEnabled", "Personal memory"),
        Toggle("Privacy", "TargetAppCorrectionLearningEnabled", "Learn from corrections", true, "Uses corrections made in the target app when supported."),

        Text("Automation", "WatchFolderPath", "Watch folder"),
        Text("Automation", "WatchFolderOutputPath", "Output folder"),
        Choice("Automation", "WatchFolderOutputFormat", "Output format", "Markdown", "Markdown|Text|SRT|VTT|JSON"),
        Toggle("Automation", "WatchFolderAutoStart", "Watch automatically"),
        Toggle("Automation", "WatchFolderDeleteSource", "Delete source after transcription", false, "Destructive in the real app. This prototype never deletes files."),
        Choice("Automation", "WatchFolderLanguage", "Watch folder language", "Automatic", "Automatic|English|German|French|Spanish"),
        Text("Automation", "WatchFolderEngineOverride", "Watch folder engine"),
        Text("Automation", "WatchFolderModelOverride", "Watch folder model"),
        Toggle("Automation", "ApiServerEnabled", "HTTP API server", false, "No server is started by this prototype."),
        Text("Automation", "ApiServerPort", "HTTP port", "8978", "Prototype text field; the real app must validate 1–65535."),
        Toggle("Automation", "ApiServerRequiresAuthentication", "Require API authentication"),

        Text("Sync & backup", "CloudFolderSyncFolderPath", "Cloud sync folder", "", "Use a shared cloud folder across your devices. No sync or file access occurs here."),
    ];

    internal static readonly string[] Categories = ["General", "Dictation", "Audio", "Shortcuts", "Live text", "Recorder", "Files & recovery", "Privacy", "Automation", "Sync & backup", "Account & about"];

    internal static IEnumerable<PrototypeSettingSearchEntry> SearchEntries => Fields.Select(setting => new PrototypeSettingSearchEntry(
        setting.Category == "Live text" ? "Appearance" : setting.Category, setting.Key, setting.Label, setting.Hint,
        ChoiceIcon(setting), string.Join(' ', setting.Choices ?? []))).Append(new(
            "Dictation", "DictationModel", "Dictation model", "Choose the active dictation model. Downloads and credentials are managed in plugin settings.", "chip", "engine default downloaded"));

    private static TextBlock Label(string text, double size = 13, bool muted = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources[muted ? "MutedBrush" : "TextBrush"]
    };

    internal static IEnumerable<(string Key, string Label, string Value)> ShortcutBindings(Dictionary<string, string> values) =>
        Fields.Where(field => field.Category == "Shortcuts").Select(field => (field.Key, field.Label, values.GetValueOrDefault(field.Key, field.Value)));

    internal static void Render(string category, StackPanel target, Dictionary<string, string> values, List<PrototypeChoicePicker> pickers, Action? refresh = null, Func<string, string?>? commitLauncherHotkeys = null, Func<string, string?>? commitDictationHotkeys = null)
    {
        target.Children.Clear();
        var title = Label(category, 24);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        target.Children.Add(title);
        if (category == "Account & about")
        {
            target.Children.Add(new PrototypeAccountView(values, pickers));
            return;
        }
        if (category == "Sync & backup")
        {
            target.Children.Add(new PrototypeSyncBackupView(values));
            return;
        }
        if (category == "Shortcuts")
        {
            var guide = Label("Your actions, your keys. Add alternatives with + or click a key to change it.", 13, true);
            ToolTipService.SetToolTip(guide, "Quick Launch shortcuts are registered globally and saved for this development app. A main key is required; unavailable combinations keep your previous bindings. Other actions remain session-only previews.");
            target.Children.Add(guide);
            var list = new StackPanel { Spacing = 24 };
            (string Title, string[] Keys)[] groups =
            [
                ("Quick Launch", ["QuickLaunchHotkeys"]),
                ("Dictation", ["MainDictationHotkeys", "PushToTalkHotkey", "ToggleOnlyHotkeys", "HoldOnlyHotkeys"]),
                ("Recent transcriptions", ["RecentTranscriptionsHotkeys", "CopyLastTranscriptionHotkeys"]),
                ("Workflow palette", ["WorkflowPaletteHotkeys"]),
                ("Recorder", ["RecorderToggleHotkeys"])
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
                    item.Children.Add(new PrototypeShortcutRecorder(field.Key, field.Label, field.Value, values,
                        () => Fields.Where(f => f.Category == "Shortcuts").Select(f =>
                            (f.Key, f.Label, values.GetValueOrDefault(f.Key, f.Value))), field.Key switch { "QuickLaunchHotkeys" => commitLauncherHotkeys, "MainDictationHotkeys" => commitDictationHotkeys, _ => null }));
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
            target.Children.Add(Label("Quick Launch shortcuts are global and saved. Other shortcuts are preview-only.", 12, true));
            return;
        }
        if (category == "Dictation")
        {
            RenderDictation(target, values, pickers);
            return;
        }
        target.Children.Add(Label("Settings preview · changes are remembered for this session only. No production settings are changed.", 12, true));
        if (category == "Audio")
        {
            RenderAudio(target, values, pickers);
            return;
        }
        RenderFields(Fields.Where(f => f.Category == category), target, values, pickers, refresh);
        if (category == "Automation") target.Children.Add(Label("Command-line integration: install, repair and status controls will be connected with the production integration. No PATH changes are made here.", 12, true));
    }

    internal static void RenderLiveTextOptions(StackPanel target, Dictionary<string, string> values, List<PrototypeChoicePicker> pickers)
    {
        RenderFields(Fields.Where(f => f.Category == "Live text"), target, values, pickers, null);
    }

    private static void RenderAudio(StackPanel target, Dictionary<string, string> values, List<PrototypeChoicePicker> pickers)
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

    private static void RenderFields(IEnumerable<Field> fields, StackPanel target, Dictionary<string, string> values, List<PrototypeChoicePicker> pickers, Action? refresh)
    {
        foreach (var field in fields)
        {
            var value = values.GetValueOrDefault(field.Key, field.Value);
            var stack = new StackPanel { Spacing = 8, Tag = field.Key };
            if (field.Key == "LocalModelStoragePath")
            {
                stack.Children.Add(new PrototypeFolderPreference(field.Key, field.Label, value, values));
            }
            else if (field.Category == "Shortcuts")
            {
                stack.Children.Add(new PrototypeShortcutRecorder(field.Key, field.Label, field.Value, values,
                    () => Fields.Where(setting => setting.Category == "Shortcuts").Select(setting =>
                        (setting.Key, setting.Label, values.GetValueOrDefault(setting.Key, setting.Value)))));
            }
            else if (field.Choices is ["Off", "On"])
            {
                var row = new Grid { ColumnSpacing = 16 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                copy.Children.Add(Label(field.Label, 14));
                if (field.Hint.Length > 0) copy.Children.Add(Label(field.Hint, 12, true));
                row.Children.Add(copy);
                var toggle = PrototypeToggleSwitch.Create(value == "On");
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
                stack.Children.Add(Label(field.Label, 14));
                var picker = new PrototypeChoicePicker();
                picker.Configure(field.Label, ChoiceIcon(field), $"Preference {field.Key}");
                picker.SetOptions(field.Choices.Select(v => new PrototypeChoice(v, v, "Session-only setting")).ToArray(), value);
                picker.SelectionChanged += selected =>
                {
                    values[field.Key] = selected;
                    if (field.Category == "Dictation") refresh?.Invoke();
                };
                pickers.Add(picker); stack.Children.Add(picker);
            }
            else
            {
                stack.Children.Add(Label(field.Label, 14));
                var input = new TextBox { Text = value, MinHeight = 40, Style = (Style)Application.Current.Resources["PrototypeSearchTextBoxStyle"] };
                AutomationProperties.SetName(input, $"Preference {field.Key}");
                AutomationProperties.SetHelpText(input, field.Label);
                input.TextChanged += (_, _) => values[field.Key] = input.Text;
                stack.Children.Add(new Border { Background = (Brush)Application.Current.Resources["SurfaceBrush"], BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"], BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Child = input });
            }
            if (field.Hint.Length > 0 && field.Choices is not ["Off", "On"]) stack.Children.Add(Label(field.Hint, 12, true));
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
        "Mode" => "microphone",
        "RecorderMicDuckingMode" => "speaker",
        _ => "settings"
    };
}
