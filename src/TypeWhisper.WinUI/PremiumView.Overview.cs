using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Presentation;
using Windows.UI;

namespace TypeWhisper.WinUI;

internal sealed partial class PremiumView
{
    private readonly StackPanel _overview = new() { Spacing = 20 };
    private readonly StackPanel _details = new() { Spacing = 16, Visibility = Visibility.Collapsed };
    private readonly StackPanel _accessDetails = new() { Spacing = 16 };
    private readonly TextBlock _detailTitle = Copy("", 20);
    private PremiumFeature? _selectedFeature;

    private void ShowOverview()
    {
        _details.Visibility = Visibility.Collapsed;
        _overview.Visibility = Visibility.Visible;
        RefreshOverview();
    }

    private void ShowDetails(PremiumFeature? feature)
    {
        _selectedFeature = feature;
        _overview.Visibility = Visibility.Collapsed;
        _details.Visibility = Visibility.Visible;
        RefreshDetails();
    }

    private void RefreshDetails()
    {
        _accessDetails.Visibility = _selectedFeature is null ? Visibility.Visible : Visibility.Collapsed;
        _notice.Visibility = _selectedFeature is null ? Visibility.Visible : Visibility.Collapsed;
        _features.Children.Clear();
        _learningToggle = null;
        _learningStatus = null;
        _detailTitle.Text = _selectedFeature switch
        {
            PremiumFeature.CalendarMeetings => "Meeting Automation",
            PremiumFeature.CorrectionLearning => "Learn from Corrections",
            PremiumFeature.CloudSync => "Sync Dictionary & Snippets",
            _ => "Manage Premium Access"
        };
        if (_selectedFeature == PremiumFeature.CloudSync) { _features.Children.Add(new CloudSyncView()); return; }
        if (_selectedFeature is { } feature)
            Feature(feature, _detailTitle.Text, feature == PremiumFeature.CorrectionLearning
                ? "Remembers confident edits after insertion and improves future text."
                : "Feature availability on Windows.");
    }

    private void RefreshOverview()
    {
        _overview.Children.Clear();
        var active = Access.Current.Any;
        var green = Color.FromArgb(255, 48, 209, 88);
        var gold = Color.FromArgb(255, 235, 188, 64);
        var accessRow = new Grid { ColumnSpacing = 14 };
        accessRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        accessRow.ColumnDefinitions.Add(new());
        accessRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        accessRow.Children.Add(IconTile(active ? "\uE73E" : "\uE72E", active ? green : gold));
        var status = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(Copy("Premium access", 12, true));
        _status.FontSize = 16;
        status.Children.Add(Copy(_status.Text, 16));
        Grid.SetColumn(status, 1); accessRow.Children.Add(status);
        var manage = new HandCursorButton { Content = "Manage Access…", CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
        manage.Click += (_, _) => ShowDetails(null);
        Grid.SetColumn(manage, 2); accessRow.Children.Add(manage);
        _overview.Children.Add(Card(accessRow));
        if (!active)
        {
            var hero = new StackPanel { Spacing = 12 };
            hero.Children.Add(IconTile("\uE734", gold));
            hero.Children.Add(Copy("Premium features that save you work", 22));
            hero.Children.Add(Copy("Record scheduled meetings automatically, learn from your corrections, and keep your dictionary and snippets in sync.", 14, true));
            hero.Children.Add(Copy("Correction learning is available on Windows. Cloud folder sync is also available; meeting automation is coming later.", 12, true));
            var unlock = new HandCursorButton { Content = "Unlock Premium" };
            unlock.Click += (_, _) => ShowDetails(null);
            hero.Children.Add(unlock);
            if (Access.Current.Supporter) hero.Children.Add(Copy("Supporter status alone does not unlock Premium features.", 12, true));
            _overview.Children.Add(Card(hero));
        }
        else
        {
            var heading = new StackPanel { Spacing = 6 };
            heading.Children.Add(Copy("Your Premium features", 20));
            heading.Children.Add(Copy("See the current state at a glance and open only the settings you need.", 13, true));
            _overview.Children.Add(heading);
        }
        var cards = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        cards.Children.Add(OverviewFeature(PremiumFeature.CalendarMeetings, "Meeting Automation", "Reminds you before a scheduled meeting or starts recording automatically when you join.", "Calendar integration is not connected in this Windows build yet.", "\uE787", Color.FromArgb(255, 59, 167, 255)));
        cards.Children.Add(OverviewFeature(PremiumFeature.CorrectionLearning, "Learn from Corrections", "Remembers confident edits after insertion and improves future text.", "teh → the\nrecieve → receive", "\uE734", gold));
        cards.Children.Add(OverviewFeature(PremiumFeature.CloudSync, "Sync Dictionary & Snippets", "Keeps your personal terms and text snippets available across your devices.", "Choose a shared iCloud Drive, OneDrive or Dropbox folder.", "\uE753", Color.FromArgb(255, 67, 201, 220)));
        void Arrange(double width)
        {
            var columns = width >= 660 ? 3 : 1;
            if (cards.ColumnDefinitions.Count == columns) return;
            cards.ColumnDefinitions.Clear(); cards.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++) cards.ColumnDefinitions.Add(new());
            for (var i = 0; i < (columns == 3 ? 1 : 3); i++) cards.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (var i = 0; i < cards.Children.Count; i++) { Grid.SetColumn((FrameworkElement)cards.Children[i], i % columns); Grid.SetRow((FrameworkElement)cards.Children[i], i / columns); }
        }
        cards.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        Arrange(ActualWidth);
        _overview.Children.Add(cards);
    }

    private UIElement OverviewFeature(PremiumFeature feature, string title, string description, string preview, string glyph, Color accent)
    {
        var available = Access.Current.Requirement(feature) == PremiumRequirement.Available;
        var learning = feature == PremiumFeature.CorrectionLearning;
        var panel = new Grid { RowSpacing = 16, MinHeight = 290 };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new());
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new Grid { ColumnSpacing = 6 };
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); top.ColumnDefinitions.Add(new());
        top.Children.Add(IconTile(glyph, accent));
        var badge = Copy(feature == PremiumFeature.CloudSync ? (WinUICloudSync.Preferences.Enabled ? "Enabled" : "Cloud folder") : !learning ? "Coming later" : available ? (CorrectionLearning.Enabled ? "Active" : "Off") : "Premium", 11);
        badge.Foreground = new SolidColorBrush(accent);
        var pill = new Border { Child = badge, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(12), Background = Tint(accent, 24), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(pill, 1); top.Children.Add(pill); panel.Children.Add(top);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(Copy(title, 18)); content.Children.Add(Copy(description, 13, true)); content.Children.Add(Copy(preview, 12, true));
        Grid.SetRow(content, 1); panel.Children.Add(content);
        if (Access.Current.Any)
        {
            var settings = new HandCursorButton { Content = "Settings…", CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left };
            settings.Click += (_, _) => ShowDetails(feature);
            Grid.SetRow(settings, 2); panel.Children.Add(settings);
        }
        return new Border { Child = panel, Padding = new Thickness(16), CornerRadius = new CornerRadius(12), Background = Tint(accent, 12), BorderBrush = Tint(accent, 65), BorderThickness = new Thickness(1) };
    }

    private static SolidColorBrush Tint(Color color, byte alpha) => new(Color.FromArgb(alpha, color.R, color.G, color.B));
    private static Border IconTile(string glyph, Color color) => new()
    {
        Width = 42, Height = 42, CornerRadius = new CornerRadius(10), Background = Tint(color, 24),
        Child = new FontIcon { Glyph = glyph, FontSize = 22, Foreground = new SolidColorBrush(color) }
    };
}
