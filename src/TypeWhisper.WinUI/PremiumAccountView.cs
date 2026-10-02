using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

internal sealed class PremiumAccountView : UserControl
{
    private readonly Button _signIn = new HandCursorButton { Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], Content = Loc.T("Sign in with Apple") };
    private readonly Button _refresh = new HandCursorButton { Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], Content = Loc.T("Refresh account") };
    private readonly Button _link = new HandCursorButton { Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], Content = Loc.T("Link commercial license") };
    private readonly Button _signOut = new HandCursorButton { Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], Content = Loc.T("Sign out") };
    private readonly Button _cancel = new HandCursorButton { Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], Content = Loc.T("Cancel sign-in") };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
    internal PremiumAccountView()
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(SettingsHelp.Label(Loc.T("Premium account"), Loc.T("Sign in with your Apple Account in your browser. An activated commercial license is linked to your account after sign-in. Account access and iCloud Drive folder synchronization are separate; signing out keeps your local data and license."), 16));
        var actions = new StackPanel { Spacing = 8 };
        foreach (var button in new[] { _signIn, _refresh, _link, _signOut, _cancel }) { button.HorizontalAlignment = HorizontalAlignment.Left; actions.Children.Add(button); }
        body.Children.Add(actions); body.Children.Add(_status);
        Content = new Border { Child = body, Padding = new(18, 14, 18, 14), CornerRadius = new(12), Background = (Brush)Application.Current.Resources["SurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"], BorderThickness = new(1) };
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _signIn.Click += async (_, _) => await WinUIPremiumAccount.SignInAsync();
        _refresh.Click += async (_, _) => await WinUIPremiumAccount.RefreshAsync();
        _link.Click += async (_, _) => await WinUIPremiumAccount.LinkAsync();
        _signOut.Click += async (_, _) => await WinUIPremiumAccount.SignOutAsync();
        _cancel.Click += (_, _) => WinUIPremiumAccount.Cancel();
        Loaded += (_, _) => { WinUIPremiumAccount.Changed += Refresh; WinUILicensing.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => { WinUIPremiumAccount.Changed -= Refresh; WinUILicensing.Changed -= Refresh; };
        Refresh();
    }
    private void Refresh()
    {
        var signedIn = WinUIPremiumAccount.SignedIn;
        _signIn.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        _refresh.Visibility = _signOut.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        _link.Visibility = signedIn && WinUILicensing.Service.HasCommercialLicense ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Visibility = WinUIPremiumAccount.Busy ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { _signIn, _refresh, _link, _signOut }) button.IsEnabled = !WinUIPremiumAccount.Busy;
        _status.Text = WinUIPremiumAccount.Status;
    }
}
