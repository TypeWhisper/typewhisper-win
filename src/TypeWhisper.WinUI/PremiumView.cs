using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed class PremiumView : UserControl
{
    // A single access source for the host. Real entitlement providers can replace the actual-access callback.
    internal static PremiumAccessState Access { get; } = new(WinUIProfile.DataPath("premium-development.txt"), () => new());
    private readonly TextBlock _status = Copy("", 20);
    private readonly TextBlock _notice = Copy("", 12, true);
    private readonly StackPanel _features = new() { Spacing = 12 };
    private readonly ChoicePicker? _scenario;
    private bool _refreshing;

    internal PremiumView(List<ChoicePicker> pickers)
    {
        var body = new StackPanel { Spacing = 20 }; Content = body;
        body.Children.Add(_status);
        body.Children.Add(_notice);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        body.Children.Add(_features);
        if (PremiumAccessState.CanOverride)
        {
            var development = new StackPanel { Spacing = 10 };
            development.Children.Add(Copy("Development access", 16));
            development.Children.Add(Copy("Test access on this development profile. Changes apply immediately and survive restarts. No purchase or license activation is performed.", 12, true));
            _scenario = new ChoicePicker();
            _scenario.Configure("Development access", "lock", "Premium development access");
            pickers.Add(_scenario);
            _scenario.SelectionChanged += id =>
            {
                if (!_refreshing && Enum.TryParse<PremiumDevScenario>(id, out var scenario)) Access.SetScenario(scenario);
            };
            development.Children.Add(_scenario);
            body.Children.Add(Card(development));
        }
        Loaded += (_, _) => { Access.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => Access.Changed -= Refresh;
        Refresh();
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var access = Access.Current;
            _status.Text = (access.Any ? "Premium access active" : access.Supporter ? "Supporter · no Premium access" : "No Premium access")
                + (Access.IsOverridden ? " · Development" : "");
            _notice.Text = Access.Error ?? (Access.IsOverridden
                ? "Development access is active. Feature availability below is separate from access."
                : "License activation and account sign-in are not connected in this build yet.");
            _features.Children.Clear();
            Feature(PremiumFeature.CalendarMeetings, "Calendar meetings", "Start meeting recordings from your calendar.");
            Feature(PremiumFeature.CorrectionLearning, "Correction learning", "Learn from corrections you make after dictation.");
            Feature(PremiumFeature.CloudSync, "Cloud sync", "Keep your TypeWhisper data in sync across devices.");
            _scenario?.SetOptions([
                new("Actual", "Use actual access", "Remove the development override."),
                new("Free", "No Premium access", "Test locked features."),
                new("Supporter", "Supporter", "Supporter status does not unlock Premium."),
                new("Commercial", "Commercial license", "Calendar meetings and correction learning."),
                new("Premium", "Premium account", "Calendar meetings and cloud sync; signed in."),
                new("All", "All access", "Commercial license and a signed-in Premium account."),
                new("SignedOut", "Premium account · signed out", "Cloud sync requires sign-in.")
            ], Access.Scenario.ToString());
        }
        finally { _refreshing = false; }
    }

    private void Feature(PremiumFeature feature, string title, string description)
    {
        var panel = new StackPanel { Spacing = 7 };
        panel.Children.Add(Copy(title, 16));
        panel.Children.Add(Copy(description, 13, true));
        var requirement = Access.Current.Requirement(feature);
        panel.Children.Add(Copy(requirement switch
        {
            PremiumRequirement.Available => "Access granted",
            PremiumRequirement.Commercial => "Requires a commercial license",
            PremiumRequirement.PremiumAccount => "Requires a Premium account",
            PremiumRequirement.SignIn => "Sign in to use this feature",
            PremiumRequirement.LinkCommercialLicense => "Link your commercial license to your account",
            _ => "Requires a commercial license or Premium account"
        }, 12));
        panel.Children.Add(Copy("Not connected in this Windows build yet.", 12, true));
        _features.Children.Add(Card(panel));
    }
    private static TextBlock Copy(string text, double size, bool muted = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources[muted ? "MutedBrush" : "TextBrush"],
        FontWeight = size >= 16 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
    };
    private static Border Card(UIElement child) => new()
    {
        Child = child, Padding = new Thickness(16), CornerRadius = new CornerRadius(8),
        Background = (Brush)Application.Current.Resources["SurfaceBrush"]
    };
}
