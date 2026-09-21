using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveStartupSettings
{
    internal static async void Configure(string category, StackPanel content, List<ChoicePicker> pickers, IStartupRegistration registration)
    {
        if (category != "General") return;
        var row = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "AutostartEnabled"));
        foreach (var old in row.Children.OfType<ChoicePicker>()) pickers.Remove(old);
        row.Children.Clear(); row.IsHitTestVisible = true;
        var toggle = new ToggleSwitch { Header = "Start TypeWhisper with Windows", IsEnabled = false };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var updating = false;
        void Show(StartupRegistrationState state)
        {
            updating = true;
            toggle.IsOn = state.IsEnabled; toggle.IsEnabled = state.CanChange;
            status.Text = state.Error ?? (state.IsEnabled
                ? "Starts in the tray when you sign in. Windows can also disable it in Startup apps."
                : "Off. Enable to start TypeWhisper in the tray when you sign in.");
            updating = false;
        }
        toggle.Toggled += async (_, _) =>
        {
            if (updating) return;
            toggle.IsEnabled = false;
            Show(await registration.SetEnabledAsync(toggle.IsOn));
        };
        row.Children.Add(toggle); row.Children.Add(status);
        Show(await registration.ReadAsync());
    }
}
