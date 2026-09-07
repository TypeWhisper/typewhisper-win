using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveHistoryRetentionSettings
{
    // Call after LiveOutputSettings: its preview controls are replaced here.
    internal static void Configure(string category, StackPanel content, List<PrototypeChoicePicker> pickers,
        HistoryRetentionController controller)
    {
        if (category != "Privacy") return;
        var row = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "HistoryRetentionMode"));
        foreach (var old in row.Children.OfType<PrototypeChoicePicker>()) pickers.Remove(old);
        row.Children.Clear();
        row.IsHitTestVisible = true;
        var oldDuration = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "HistoryRetentionMinutes"));
        foreach (var old in oldDuration.Children.OfType<PrototypeChoicePicker>()) pickers.Remove(old);
        oldDuration.Children.Clear();
        oldDuration.Visibility = Visibility.Collapsed;
        var oldNote = content.Children.OfType<TextBlock>().FirstOrDefault(text => text.Text.StartsWith("Automatic history deletion,"));
        if (oldNote is not null) oldNote.Text = "Personal memory and correction learning are not available yet.";
        var previewNote = content.Children.OfType<TextBlock>().FirstOrDefault(text => text.Text.StartsWith("History saving is saved"));
        if (previewNote is not null) previewNote.Text = "History saving and retention are saved for this development profile. Unavailable controls are disabled.";

        row.Children.Add(Label("History retention", 14));
        row.Children.Add(Label("Forever keeps existing history. A duration permanently deletes entries older than that age and their saved audio, including entries already in history. Age is measured from when an entry was created."));
        var picker = new PrototypeChoicePicker();
        picker.Configure("History retention", "history", "History retention");
        var durationLabel = Label("Keep history for (minutes)");
        var duration = new NumberBox { Minimum = 1, Maximum = HistoryRetentionPreferences.MaximumMinutes,
            SmallChange = 60, LargeChange = 1440, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        AutomationProperties.SetName(duration, "History retention in minutes");
        var apply = new HandCursorButton { Content = "Apply retention", HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
        AutomationProperties.SetName(apply, "Apply history retention");
        var status = Label("");
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var mode = controller.Preferences.Current.HistoryRetentionMode;
        var refreshing = false;
        var busy = false;
        var dirty = false;
        string ActiveStatus() => controller.Error ?? (controller.Preferences.Current.HistoryRetentionMode == HistoryRetentionMode.Forever
            ? "Automatic history deletion is off."
            : "The saved duration applies to existing and future history entries.");
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
                new("Forever", "Forever", "Keep history until you delete it."),
                new("Duration", "For a duration", "Automatically delete entries older than your selected age.")
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
            status.Text = "Not applied yet. Choose Apply retention to save this selection.";
        };
        duration.ValueChanged += (_, _) =>
        {
            if (!refreshing && !busy) { dirty = true; status.Text = "Not applied yet. Choose Apply retention to save this selection."; }
        };
        apply.Click += async (_, _) =>
        {
            if (busy) return;
            if (!double.IsFinite(duration.Value) || duration.Value != Math.Truncate(duration.Value)
                || duration.Value < 1 || duration.Value > HistoryRetentionPreferences.MaximumMinutes)
            {
                status.Text = "Enter a whole number of minutes between 1 and 5,256,000.";
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
                    var dialog = new ContentDialog { XamlRoot = row.XamlRoot, Title = "Delete older history automatically?",
                        Content = $"Applying this choice permanently deletes existing entries older than {selection.HistoryRetentionMinutes:N0} minutes and their saved audio now. It also deletes entries as they reach this age in the future. This cannot be undone.",
                        PrimaryButtonText = "Apply and delete older entries", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    {
                        status.Text = "Canceled. Your saved retention choice is unchanged.";
                        Refresh();
                        return;
                    }
                }
                var error = await controller.ChangeAsync(selection, confirmedShortening: confirm);
                Refresh();
                status.Text = error ?? (controller.Preferences.Current.HistoryRetentionMode == HistoryRetentionMode.Forever
                    ? "Saved. Automatic history deletion is off. Previously deleted entries cannot be restored."
                    : "Saved. Entries older than this duration are deleted automatically while the app is running and when it starts.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                status.Text = "Retention could not be changed. Your previously saved choice still applies.";
                Refresh();
            }
            finally { busy = false; apply.IsEnabled = picker.IsEnabled = duration.IsEnabled = true; }
        };
        row.Children.Add(picker); row.Children.Add(durationLabel); row.Children.Add(duration);
        row.Children.Add(apply); row.Children.Add(status); pickers.Add(picker);
        Refresh();
        status.Text = controller.Error ?? (mode == HistoryRetentionMode.Forever ? "Automatic history deletion is off." : "The saved duration applies to existing and future history entries.");
    }

    private static TextBlock Label(string text, double fontSize = 12) => new()
    {
        Text = text, FontSize = fontSize, TextWrapping = TextWrapping.Wrap
    };
}
