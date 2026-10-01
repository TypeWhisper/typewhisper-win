using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveHistoryRetentionSettings
{
    // Call after LiveOutputSettings: its preview controls are replaced here.
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers,
        HistoryRetentionController controller)
    {
        if (category != "Privacy") return;
        var row = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "HistoryRetentionMode"));
        foreach (var old in row.Children.OfType<ChoicePicker>()) pickers.Remove(old);
        row.Children.Clear();
        row.IsHitTestVisible = true;
        var oldDuration = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "HistoryRetentionMinutes"));
        foreach (var old in oldDuration.Children.OfType<ChoicePicker>()) pickers.Remove(old);
        oldDuration.Children.Clear();
        oldDuration.Visibility = Visibility.Collapsed;
        var oldNote = content.Children.OfType<TextBlock>().FirstOrDefault(text => text.Text == Loc.T("Automatic history deletion and personal memory are not available yet."));
        if (oldNote is not null) oldNote.Text = Loc.T("Personal memory is not available yet.");
        var previewNote = content.Children.OfType<TextBlock>().FirstOrDefault(text => text.Text == Loc.T("History saving is saved automatically. Unavailable controls are disabled.")
            || text.Text == Loc.T("History saving is saved automatically. Retention changes take effect when you choose Apply retention. Unavailable controls are disabled."));
        if (previewNote is not null) previewNote.Text = Loc.T("History saving is saved automatically. Retention changes take effect when you choose Apply retention. Unavailable controls are disabled.");

        row.Children.Add(SettingsHelp.Label(Loc.T("History retention"),
            Loc.T("Forever keeps existing history. Age is measured from when an entry was created.")));
        row.Children.Add(Label(Loc.T("A duration permanently deletes entries older than that age and their saved audio, including entries already in history.")));
        var picker = new ChoicePicker();
        picker.Configure(Loc.T("History retention"), "history", Loc.T("History retention"));
        var durationLabel = Label(Loc.T("Keep history for (minutes)"));
        var duration = new NumberBox { Minimum = 1, Maximum = HistoryRetentionPreferences.MaximumMinutes,
            SmallChange = 60, LargeChange = 1440, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        AutomationProperties.SetName(duration, Loc.T("History retention in minutes"));
        var apply = new HandCursorButton { Content = Loc.T("Apply retention"), HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        AutomationProperties.SetName(apply, Loc.T("Apply history retention"));
        var status = Label("");
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var mode = controller.Preferences.Current.HistoryRetentionMode;
        var refreshing = false;
        var busy = false;
        var dirty = false;
        string ActiveStatus() => controller.Error ?? (controller.Preferences.Current.HistoryRetentionMode == HistoryRetentionMode.Forever
            ? Loc.T("Automatic history deletion is off.")
            : Loc.T("The saved duration applies to existing and future history entries."));
        void OnChanged() => row.DispatcherQueue.TryEnqueue(() =>
        {
            if (row.IsLoaded && !dirty && !busy) status.Text = ActiveStatus();
        });
        row.Loaded += (_, _) => { controller.Changed += OnChanged; if (!dirty) status.Text = ActiveStatus(); };
        row.Unloaded += (_, _) => controller.Changed -= OnChanged;
        void SetDurationVisibility()
        {
            duration.Visibility = durationLabel.Visibility = mode == HistoryRetentionMode.Duration ? Visibility.Visible : Visibility.Collapsed;
        }
        void Refresh()
        {
            refreshing = true;
            mode = controller.Preferences.Current.HistoryRetentionMode;
            picker.SetOptions([
                new("Forever", Loc.T("Forever"), Loc.T("Keep history until you delete it.")),
                new("Duration", Loc.T("For a duration"), Loc.T("Automatically delete entries older than your selected age."))
            ], mode.ToString());
            duration.Value = controller.Preferences.Current.HistoryRetentionMinutes;
            SetDurationVisibility();
            refreshing = false;
            dirty = false;
        }
        picker.SelectionChanged += id =>
        {
            if (refreshing || busy) return;
            if (Enum.TryParse<HistoryRetentionMode>(id, out var selected)) mode = selected;
            SetDurationVisibility();
            dirty = true;
            status.Text = Loc.T("Not applied yet. Choose Apply retention to save this selection.");
        };
        duration.ValueChanged += (_, _) =>
        {
            if (!refreshing && !busy) { dirty = true; status.Text = Loc.T("Not applied yet. Choose Apply retention to save this selection."); }
        };
        apply.Click += async (_, _) =>
        {
            if (busy) return;
            if (!double.IsFinite(duration.Value) || duration.Value != Math.Truncate(duration.Value)
                || duration.Value < 1 || duration.Value > HistoryRetentionPreferences.MaximumMinutes)
            {
                status.Text = Loc.T("Enter a whole number of minutes between 1 and 5,256,000.");
                return;
            }
            var selection = new HistoryRetentionPreferences(mode, (int)duration.Value);
            busy = true;
            apply.IsEnabled = picker.IsEnabled = duration.IsEnabled = false;
            try
            {
                var confirm = selection.RequiresConfirmationComparedTo(controller.Preferences.Current);
                if (confirm)
                {
                    var dialog = new ContentDialog { XamlRoot = row.XamlRoot, Title = Loc.T("Delete older history automatically?"),
                        Content = Loc.T("Applying this choice permanently deletes existing entries older than {0:N0} minutes and their saved audio now. It also deletes entries as they reach this age in the future. This cannot be undone.", selection.HistoryRetentionMinutes),
                        PrimaryButtonText = Loc.T("Apply and delete older entries"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    {
                        status.Text = Loc.T("Canceled. Your saved retention choice is unchanged.");
                        Refresh();
                        return;
                    }
                }
                var error = await controller.ChangeAsync(selection, confirmedShortening: confirm);
                Refresh();
                status.Text = error ?? (controller.Preferences.Current.HistoryRetentionMode == HistoryRetentionMode.Forever
                    ? Loc.T("Saved. Automatic history deletion is off. Previously deleted entries cannot be restored.")
                    : Loc.T("Saved. Entries older than this duration are deleted automatically while the app is running and when it starts."));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                status.Text = Loc.T("Retention could not be changed. Your previously saved choice still applies.");
                Refresh();
            }
            finally { busy = false; apply.IsEnabled = picker.IsEnabled = duration.IsEnabled = true; }
        };
        row.Children.Add(picker); row.Children.Add(durationLabel); row.Children.Add(duration);
        row.Children.Add(apply); row.Children.Add(status); pickers.Add(picker);
        Refresh();
        status.Text = controller.Error ?? (mode == HistoryRetentionMode.Forever ? Loc.T("Automatic history deletion is off.") : Loc.T("The saved duration applies to existing and future history entries."));
    }

    private static TextBlock Label(string text, double fontSize = 12) => new()
    {
        Text = text, FontSize = fontSize, TextWrapping = TextWrapping.Wrap
    };
}
