using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveStartupSettings
{
    internal static void Configure(string category, StackPanel content, List<PrototypeChoicePicker> pickers, StartupRegistration registration)
    {
        if (category != "General") return;
        var row = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "AutostartEnabled"));
        foreach (var old in row.Children.OfType<PrototypeChoicePicker>()) pickers.Remove(old);
        row.Children.Clear(); row.IsHitTestVisible = true;
        var toggle = new ToggleSwitch { Header = "Start this development build with Windows" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var updating = false;
        void Show(StartupRegistrationState state)
        {
            updating = true;
            toggle.IsOn = state.IsEnabled; toggle.IsEnabled = state.CanChange;
            status.Text = state.Error ?? (state.IsEnabled
                ? "Registered for sign-in. Starts in the tray from this published output. Windows can also disable it in Startup apps."
                : "Off. Enabling registers this development build only; your installed TypeWhisper registration stays unchanged.");
            updating = false;
        }
        toggle.Toggled += (_, _) => { if (!updating) Show(registration.SetEnabled(toggle.IsOn)); };
        row.Children.Add(toggle); row.Children.Add(status);
        Show(registration.Read());
    }
}
