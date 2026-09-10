using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

internal sealed class PremiumAccountView : UserControl
{
    private readonly Button _signIn = new HandCursorButton { Content = "Sign in with Apple", CornerRadius = new(8) };
    private readonly Button _refresh = new HandCursorButton { Content = "Refresh account", CornerRadius = new(8) };
    private readonly Button _link = new HandCursorButton { Content = "Link commercial license", CornerRadius = new(8) };
    private readonly Button _signOut = new HandCursorButton { Content = "Sign out", CornerRadius = new(8) };
    private readonly Button _cancel = new HandCursorButton { Content = "Cancel sign-in", CornerRadius = new(8) };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
    internal PremiumAccountView()
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(SettingsHelp.Label("Premium account", "Sign in with your Apple Account in your browser. An activated commercial license is linked to your account after sign-in. Account access and iCloud Drive folder synchronization are separate; signing out keeps your local data and license.", 16));
        var actions = new StackPanel { Spacing = 8 };
        foreach (var button in new[] { _signIn, _refresh, _link, _signOut, _cancel }) { button.HorizontalAlignment = HorizontalAlignment.Left; actions.Children.Add(button); }
        body.Children.Add(actions); body.Children.Add(_status);
        Content = new Border { Child = body, Padding = new(16), CornerRadius = new(8), Background = (Brush)Application.Current.Resources["SurfaceBrush"] };
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
