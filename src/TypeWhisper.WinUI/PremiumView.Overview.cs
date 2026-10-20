using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using Windows.System;
using Windows.UI;

namespace TypeWhisper.WinUI;

// The overview reads like a product page: the user's own dictionary and snippets are the argument for Premium,
// and the features are rows that keep their height whatever the window width.
internal sealed partial class PremiumView
{
    private const double WideLayout = 720;
    private static readonly Uri PricingPage = new("https://www.typewhisper.com/pricing#commercial-table");
    // Darker tones in the light theme keep the pill text and icons readable on their tinted backgrounds.
    private Color Gold => ActualTheme == ElementTheme.Light ? Color.FromArgb(255, 158, 110, 0) : Color.FromArgb(255, 235, 188, 64);
    private Color Green => ActualTheme == ElementTheme.Light ? Color.FromArgb(255, 30, 130, 60) : Color.FromArgb(255, 48, 209, 88);
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
            PremiumFeature.CalendarMeetings => Loc.T("Meeting Automation"),
            PremiumFeature.CorrectionLearning => Loc.T("Learn from Corrections"),
            PremiumFeature.CloudSync => Loc.T("Sync Dictionary & Snippets"),
            _ => Loc.T("Manage Premium Access")
        };
        if (_selectedFeature == PremiumFeature.CloudSync) { _features.Children.Add(new CloudSyncView()); return; }
        if (_selectedFeature is { } feature)
            Feature(feature, _detailTitle.Text, feature == PremiumFeature.CorrectionLearning
                ? Loc.T("Remembers confident edits after insertion and improves future text.")
                : Loc.T("Feature availability on Windows."));
    }

    private void RefreshOverview()
    {
        _overview.Children.Clear();
        var access = Access.Current;
        _overview.Children.Add(AccessStrip(access.Any));
        // Purchase prompts stay hidden while the organization provides the license, even if activation is pending,
        // and while policy suppresses optional prompts.
        var promptsSuppressed = OnboardingPolicy.SuppressOptionalPrompts(WinUIManagedPolicy.Current);
        var pitch = !access.Any && !WinUILicensing.Managed && !promptsSuppressed;
        if (pitch) _overview.Children.Add(Hero(access.Supporter));
        else _overview.Children.Add(Heading(access.Any));
        _overview.Children.Add(FeatureList(access));
        if (pitch) _overview.Children.Add(TrustLine());
    }

    private UIElement AccessStrip(bool active)
    {
        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(IconTile(active ? "" : "", active ? Green : Gold));
        var status = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(Copy(Loc.T("Premium access"), 12, true));
        var state = Copy(_status.Text, 15);
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
        status.Children.Add(state);
        Grid.SetColumn(status, 1); row.Children.Add(status);
        var manage = new HandCursorButton { Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], Content = Loc.T("Manage Access…"), VerticalAlignment = VerticalAlignment.Center };
        manage.Click += (_, _) => ShowDetails(null);
        Grid.SetColumn(manage, 2); row.Children.Add(manage);
        return Card(row);
    }

    private static UIElement Heading(bool active)
    {
        var heading = new StackPanel { Spacing = 6 };
        var title = Copy(active ? Loc.T("Your Premium features") : Loc.T("Premium features"), 20);
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        heading.Children.Add(title);
        heading.Children.Add(Copy(!active && WinUILicensing.Managed
            ? Loc.T("Your organization provides the license. Features unlock once it is active.")
            : Loc.T("See the current state at a glance and open only the settings you need."), 13, true));
        return heading;
    }

    // Two columns on a wide page: the pitch and, beside it, what the user already has.
    private UIElement Hero(bool supporter)
    {
        var grid = new Grid { ColumnSpacing = 32, RowSpacing = 24 };
        var pitch = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        pitch.Children.Add(Eyebrow(Loc.T("Premium"), Gold));
        var headline = Copy(Loc.T("Your dictionary on every PC. Corrections that stick."), 26);
        headline.MaxWidth = 540; headline.HorizontalAlignment = HorizontalAlignment.Left; headline.LineHeight = 32;
        AutomationProperties.SetHeadingLevel(headline, AutomationHeadingLevel.Level2);
        pitch.Children.Add(headline);
        var lede = Copy(Loc.T("A commercial license keeps your dictionary and snippets in sync through your own cloud folder and learns from the corrections you make after dictating. Meeting automation follows later on Windows."), 14, true);
        lede.MaxWidth = 540; lede.HorizontalAlignment = HorizontalAlignment.Left;
        pitch.Children.Add(lede);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 4, 0, 0) };
        var unlock = new HandCursorButton { Style = (Style)Application.Current.Resources["PrimaryButtonStyle"], Content = Loc.T("Unlock Premium"), MinHeight = 36 };
        unlock.Click += (_, _) => ShowDetails(null);
        actions.Children.Add(unlock);
        var plans = new HandCursorButton { Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], Content = Loc.T("See plans"), MinHeight = 36 };
        AutomationProperties.SetHelpText(plans, PricingPage.ToString());
        plans.Click += async (_, _) => await Launcher.LaunchUriAsync(PricingPage);
        actions.Children.Add(plans);
        pitch.Children.Add(actions);
        pitch.Children.Add(Copy(Loc.T("Individual license from €5 a month or €99 once, pay what you want."), 12, true));
        if (supporter) pitch.Children.Add(Copy(Loc.T("Supporter status alone does not unlock Premium features."), 12, true));
        grid.Children.Add(pitch);
        var ledger = Ledger();
        grid.Children.Add(ledger);
        void Arrange(double width)
        {
            var wide = width >= WideLayout;
            if (grid.ColumnDefinitions.Count == (wide ? 2 : 1) && grid.ColumnDefinitions.Count > 0) return;
            grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
            if (wide)
            {
                grid.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
                Grid.SetRow(ledger, 0); Grid.SetColumn(ledger, 1);
            }
            else
            {
                grid.ColumnDefinitions.Add(new());
                grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                Grid.SetRow(ledger, 1); Grid.SetColumn(ledger, 0);
            }
        }
        grid.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        Arrange(ActualWidth);
        var card = Card(grid);
        card.Padding = new Thickness(28, 26, 28, 26);
        return card;
    }

    // What learning does, shown as the corrections people make most: a typo, a name and a spelling, in their language.
    private static Border Ledger()
    {
        var panel = new StackPanel { Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Eyebrow(Loc.T("What TypeWhisper learns from your corrections"), null));
        foreach (var example in PremiumCorrectionExamples.For(Loc.Language))
            panel.Children.Add(LedgerLine(PremiumCorrectionExamples.Arrow(example), example.Kind switch
            {
                PremiumCorrectionKind.Typo => Loc.T("Typos that keep coming back"),
                PremiumCorrectionKind.Name => Loc.T("Names the recognition does not know"),
                _ => Loc.T("Spellings of products and technical terms")
            }));
        var note = Copy(Loc.T("You fix a word once after dictating. From then on it is written correctly, and with sync on every PC."), 12, true);
        note.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(note);
        return new Border
        {
            Child = panel, Padding = new Thickness(22, 20, 22, 20), CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["InkBrush"],
            BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"], BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    private static UIElement LedgerLine(string value, string note)
    {
        var line = new Grid { ColumnSpacing = 12 };
        line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new());
        var mark = BarMark(); mark.VerticalAlignment = VerticalAlignment.Top; mark.Margin = new Thickness(0, 4, 0, 0);
        line.Children.Add(mark);
        var copy = new StackPanel { Spacing = 2 };
        copy.Children.Add(Copy(value, 15));
        copy.Children.Add(Copy(note, 12, true));
        Grid.SetColumn(copy, 1); line.Children.Add(copy);
        return line;
    }

    // The three bars of the TypeWhisper mark, as the website uses them for list marks.
    private static StackPanel BarMark()
    {
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        foreach (var height in new[] { 7, 14, 7 })
            bars.Children.Add(new Border { Width = 3, Height = height, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)Application.Current.Resources["AccentBrush"] });
        return bars;
    }

    private static TextBlock Eyebrow(string text, Color? color)
    {
        var eyebrow = Copy(text.ToUpper(System.Globalization.CultureInfo.CurrentCulture), 12, color is null);
        eyebrow.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        eyebrow.CharacterSpacing = 90;
        if (color is { } accent) eyebrow.Foreground = new SolidColorBrush(accent);
        return eyebrow;
    }

    private UIElement FeatureList(PremiumAccess access)
    {
        var card = new SettingsCard();
        var sync = WinUICloudSync.Preferences;
        var folder = sync.Enabled && sync.Folder is { } path ? System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) : null;
        card.Add(FeatureRow(PremiumFeature.CloudSync, "devices", Loc.T("Sync Dictionary & Snippets"),
            Loc.T("Keeps your personal terms and text snippets available across your devices."),
            Loc.T("Choose a shared iCloud Drive, OneDrive or Dropbox folder."),
            !access.Any ? (Loc.T("Premium"), (Color?)Gold) : folder is { Length: > 0 } ? (folder, (Color?)Green) : (Loc.T("Cloud folder"), (Color?)null), access.Any));
        var learnable = access.Requirement(PremiumFeature.CorrectionLearning) == PremiumRequirement.Available;
        var learned = DictationDictionarySnapshot.Load(DictationDictionarySnapshot.StoragePath).Entries
            .Count(entry => entry.EntryType == DictionaryEntryType.Correction && entry.Source == DictionaryEntrySource.AutoLearned);
        card.Add(FeatureRow(PremiumFeature.CorrectionLearning, "correction", Loc.T("Learn from Corrections"),
            Loc.T("Remembers confident edits after insertion and improves future text."),
            learnable && learned > 0 ? Loc.T("{0} corrections learned", learned) + "  ·  " + PremiumCorrectionExamples.Primary(Loc.Language) : PremiumCorrectionExamples.Line(Loc.Language),
            !learnable ? (Loc.T("Premium"), (Color?)Gold) : CorrectionLearning.Enabled ? (Loc.T("Active"), (Color?)Green) : (Loc.T("Off"), (Color?)null), access.Any));
        card.Add(FeatureRow(PremiumFeature.CalendarMeetings, "calendar", Loc.T("Meeting Automation"),
            Loc.T("Reminds you before a scheduled meeting or starts recording automatically when you join."),
            Loc.T("Calendar integration is not connected in this Windows build yet."),
            (Loc.T("Coming later"), null), access.Any));
        return card;
    }

    private SettingsRow FeatureRow(PremiumFeature feature, string glyph, string title, string description, string detail, (string Text, Color? Accent) badge, bool settings)
    {
        var control = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        control.Children.Add(Pill(badge.Text, badge.Accent));
        if (settings)
        {
            var open = new HandCursorButton { Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], Content = Loc.T("Settings…") };
            AutomationProperties.SetName(open, Loc.T("{0} settings", title));
            open.Click += (_, _) => ShowDetails(feature);
            control.Children.Add(open);
        }
        var row = new SettingsRow().Icon(glyph).Set(title, description, control: control);
        var example = Copy(detail, 12, true);
        example.Margin = new Thickness(34, -4, 0, 0);
        row.Below(example);
        return row;
    }

    private static Border Pill(string text, Color? accent)
    {
        var label = Copy(text, 11, accent is null);
        label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        if (accent is { } color) label.Foreground = new SolidColorBrush(color);
        return new Border
        {
            Child = label, Padding = new Thickness(9, 4, 9, 4), CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center,
            Background = accent is { } tint ? Tint(tint, 28) : (Brush)Application.Current.Resources["ControlFillBrush"],
            BorderBrush = accent is null ? (Brush)Application.Current.Resources["HairlineBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(1)
        };
    }

    // Why the data stays the user's, in the words of the website, and the way in for existing keys.
    private UIElement TrustLine()
    {
        var facts = new StackPanel { Spacing = 10 };
        string[] lines = [Loc.T("Local first"), Loc.T("Your data stays in the cloud folder you choose"), Loc.T("The core app stays open source")];
        var items = lines.Select(text =>
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            item.Children.Add(BarMark());
            var copy = Copy(text, 12, true); copy.VerticalAlignment = VerticalAlignment.Center;
            item.Children.Add(copy);
            return item;
        }).ToArray();
        var row = new StackPanel { Spacing = 10 };
        foreach (var item in items) row.Children.Add(item);
        facts.Children.Add(row);
        var activate = new HyperlinkButton { Content = Loc.T("Already have a key? Activate your license"), Padding = new Thickness(0), Margin = new Thickness(0, 2, 0, 0) };
        activate.Click += (_, _) => ShowDetails(null);
        facts.Children.Add(activate);
        void Arrange(double width)
        {
            var horizontal = width >= WideLayout;
            if (row.Orientation == (horizontal ? Orientation.Horizontal : Orientation.Vertical)) return;
            row.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
            row.Spacing = horizontal ? 24 : 10;
        }
        facts.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        Arrange(ActualWidth);
        facts.Margin = new Thickness(6, 0, 6, 0);
        return facts;
    }

    private static SolidColorBrush Tint(Color color, byte alpha) => new(Color.FromArgb(alpha, color.R, color.G, color.B));
    private static Border IconTile(string glyph, Color color) => new()
    {
        Width = 42, Height = 42, CornerRadius = new CornerRadius(10), Background = Tint(color, 24),
        Child = new FontIcon { Glyph = glyph, FontSize = 20, Foreground = new SolidColorBrush(color) }
    };
}
