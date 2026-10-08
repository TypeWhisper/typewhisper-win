using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveHistoryRetentionSettings
{
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers,
        HistoryRetentionController controller)
    {
        if (category != "Privacy") return;
        var row = SettingsRow.Require(content, "HistoryRetentionMode").Reset(pickers);
        // The duration is entered in this row.
        var oldDuration = SettingsRow.Require(content, "HistoryRetentionMinutes").Reset(pickers);
        oldDuration.Visibility = Visibility.Collapsed;

        var picker = new ChoicePicker();
        picker.Configure(Loc.T("History retention"), "history", Loc.T("History retention"));
        var durationLabel = new TextBlock { Text = Loc.T("Keep history for (minutes)"), FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var duration = new NumberBox { Minimum = 1, Maximum = HistoryRetentionPreferences.MaximumMinutes, Width = 160,
            SmallChange = 60, LargeChange = 1440, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        AutomationProperties.SetName(duration, Loc.T("History retention in minutes"));
        var apply = new HandCursorButton { Content = Loc.T("Apply retention"), Style = (Style)Application.Current.Resources["PrimaryButtonStyle"] };
        AutomationProperties.SetName(apply, Loc.T("Apply history retention"));
        var durationLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        durationLine.Children.Add(durationLabel); durationLine.Children.Add(duration);
        // Shown only while a choice waits to be applied.
        var pending = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        pending.Children.Add(durationLine); pending.Children.Add(apply);
        row.Set(Loc.T("History retention"),
            Loc.T("A duration permanently deletes entries older than that age and their saved audio, including entries already in history."),
            Loc.T("Forever keeps existing history. Age is measured from when an entry was created."), picker);
        row.Below(pending);
        pickers.Add(picker);
        var mode = controller.Preferences.Current.HistoryRetentionMode;
        var refreshing = false;
        var busy = false;
        var dirty = false;
        void OnChanged() => row.DispatcherQueue.TryEnqueue(() =>
        {
            if (row.IsLoaded && !dirty && !busy) row.Status = controller.Error ?? "";
        });
        ViewSubscriptions.Attach(row, () => { controller.Changed += OnChanged; if (!dirty) row.Status = controller.Error ?? ""; },
            () => controller.Changed -= OnChanged);
        void Arrange()
        {
            durationLine.Visibility = mode == HistoryRetentionMode.Duration ? Visibility.Visible : Visibility.Collapsed;
            apply.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
            pending.Visibility = dirty || mode == HistoryRetentionMode.Duration ? Visibility.Visible : Visibility.Collapsed;
        }
        void MarkDirty()
        {
            dirty = true;
            row.Status = Loc.T("Not applied yet. Choose Apply retention to save this selection.");
            Arrange();
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
            refreshing = false;
            dirty = false;
            Arrange();
        }
        picker.SelectionChanged += id =>
        {
            if (refreshing || busy) return;
            if (Enum.TryParse<HistoryRetentionMode>(id, out var selected)) mode = selected;
            MarkDirty();
        };
        duration.ValueChanged += (_, _) => { if (!refreshing && !busy) MarkDirty(); };
        apply.Click += async (_, _) =>
        {
            if (busy) return;
            if (!double.IsFinite(duration.Value) || duration.Value != Math.Truncate(duration.Value)
                || duration.Value < 1 || duration.Value > HistoryRetentionPreferences.MaximumMinutes)
            {
                row.Status = Loc.T("Enter a whole number of minutes between 1 and 5,256,000.");
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
                    if (!await Dialogs.ConfirmAsync(row, Loc.T("Delete older history automatically?"),
                        Loc.T("Applying this choice permanently deletes existing entries older than {0:N0} minutes and their saved audio now. It also deletes entries as they reach this age in the future. This cannot be undone.", selection.HistoryRetentionMinutes),
                        Loc.T("Apply and delete older entries"), destructive: true))
                    {
                        Refresh();
                        row.Status = Loc.T("Canceled. Your saved retention choice is unchanged.");
                        return;
                    }
                }
                var error = await controller.ChangeAsync(selection, confirmedShortening: confirm);
                Refresh();
                row.Status = error ?? "";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Refresh();
                row.Status = Loc.T("Retention could not be changed. Your previously saved choice still applies.");
            }
            finally { busy = false; apply.IsEnabled = picker.IsEnabled = duration.IsEnabled = true; }
        };
        Refresh();
        row.Status = controller.Error ?? "";
    }
}
