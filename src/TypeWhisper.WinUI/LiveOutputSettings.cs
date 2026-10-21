using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TypeWhisper.WinUI;

internal static class LiveOutputSettings
{
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers,
        LocalDictationSession session)
    {
        var store = session.OutputPreferences;
        if (category == "Dictation")
        {
            var row = SettingsRow.Require(content, "AutoPaste").Reset(pickers);
            var lockRow = SettingsRow.Require(content, "LockPasteToFocusedField").Reset(pickers);
            var preserveRow = SettingsRow.Require(content, "PreserveClipboard").Reset(pickers);
            var picker = new ChoicePicker();
            picker.Configure(Loc.T("After recording"), "text", "Preference AutoPaste");
            void Refresh()
            {
                picker.SetOptions([
                    new("On", Loc.T("Insert directly"), Loc.T("Paste the finished text into the original app.")),
                    new("Copy", Loc.T("Copy to clipboard"), Loc.T("Copy the finished text to the clipboard. Paste it yourself with Ctrl+V. No review window.")),
                    new("Off", Loc.T("Review first"), Loc.T("Open the result for review and copying. No automatic paste."))
                ], store.Current.AutoPaste ? "On" : store.Current.CopyInsteadOfReview ? "Copy" : "Off");
                // The paste target only matters while text is pasted automatically.
                lockRow.Visibility = store.Current.AutoPaste ? Visibility.Visible : Visibility.Collapsed;
            }
            picker.SelectionChanged += id =>
            {
                row.Status = store.Save(store.Current with { AutoPaste = id == "On", CopyInsteadOfReview = id == "Copy" }) ?? "";
                Refresh();
            };
            row.Set(Loc.T("After recording"), Loc.T("Insert the text directly, copy it to the clipboard, or review the result first."),
                Loc.T("Review first works even when history saving is off. Turning automatic paste off also applies to a dictation in progress."), picker);
            row.Status = store.Error ?? "";
            pickers.Add(picker);
            var lockToggle = AppToggleSwitch.Create(store.Current.LockPasteToFocusedField);
            AutomationProperties.SetName(lockToggle, Loc.T("Paste only into the original field"));
            lockRow.Set(Loc.T("Paste only into the original field"),
                Loc.T("Return to the text field active when recording started, even if you switch windows. If the app does not expose that field, return to its window instead. If neither is available, the result opens for review instead of being pasted elsewhere."), lockToggle);
            var resettingLock = false;
            lockToggle.Toggled += (_, _) =>
            {
                if (resettingLock) return;
                lockRow.Status = store.Save(store.Current with { LockPasteToFocusedField = lockToggle.IsOn }) ?? "";
                resettingLock = true; lockToggle.IsOn = store.Current.LockPasteToFocusedField; resettingLock = false;
            };
            // Also applies to pasting the last dictation and inserting from the workflow palette, so it stays visible.
            var preserveToggle = AppToggleSwitch.Create(store.Current.PreserveClipboard);
            AutomationProperties.SetName(preserveToggle, Loc.T("Preserve clipboard content"));
            preserveRow.Set(Loc.T("Preserve clipboard content"),
                Loc.T("Restores your clipboard after text insertion. Without this, your clipboard contains the transcribed text after dictation."), preserveToggle);
            var resettingPreserve = false;
            preserveToggle.Toggled += (_, _) =>
            {
                if (resettingPreserve) return;
                preserveRow.Status = store.Save(store.Current with { PreserveClipboard = preserveToggle.IsOn }) ?? "";
                resettingPreserve = true; preserveToggle.IsOn = store.Current.PreserveClipboard; resettingPreserve = false;
            };
            Refresh();
        }
        if (category != "Privacy") return;
        var saveRow = SettingsRow.Require(content, "SaveToHistoryEnabled").Reset(pickers);
        var toggle = AppToggleSwitch.Create(store.Current.SaveToHistory);
        AutomationProperties.SetName(toggle, Loc.T("Save to history"));
        saveRow.Set(Loc.T("Save to history"), Loc.T("Keep completed transcripts available in History."),
            Loc.T("Keep new dictation results on this device. Turning this off leaves existing history unchanged. Results can still be pasted or reviewed. Changes also apply to a dictation that has not been saved yet."), toggle);
        saveRow.Status = store.Error ?? "";
        var audioRow = SettingsRow.Require(content, "SaveHistoryAudio").Reset(pickers);
        var audioToggle = AppToggleSwitch.Create(store.Current.SaveHistoryAudio);
        AutomationProperties.SetName(audioToggle, Loc.T("Keep dictation audio in history"));
        audioRow.Set(Loc.T("Keep dictation audio"), Loc.T("Keep a local audio copy with new dictation entries. Audio follows history deletion and retention."),
            Loc.T("Save a local audio copy with new dictation entries so you can listen again. Audio is deleted with its history entry and follows history retention. File imports and recorder files are separate. Off by default. Turning this off keeps existing recordings and also applies to a dictation that has not been saved yet."), audioToggle);
        string? audioError = null;
        void RefreshAudio()
        {
            audioToggle.IsEnabled = store.Current.SaveToHistory;
            audioRow.Status = audioError ?? (store.Current.SaveToHistory ? "" : Loc.T("Requires Save to history."));
        }
        RefreshAudio();
        var restoring = false;
        toggle.Toggled += (_, _) =>
        {
            if (restoring) return;
            saveRow.Status = store.Save(store.Current with { SaveToHistory = toggle.IsOn }) ?? "";
            restoring = true; toggle.IsOn = store.Current.SaveToHistory; restoring = false;
            RefreshAudio();
        };
        audioToggle.Toggled += (_, _) =>
        {
            if (restoring) return;
            audioError = store.Save(store.Current with { SaveHistoryAudio = audioToggle.IsOn });
            restoring = true; audioToggle.IsOn = store.Current.SaveHistoryAudio; restoring = false;
            RefreshAudio();
        };
    }
}
