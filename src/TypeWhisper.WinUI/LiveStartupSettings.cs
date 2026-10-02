using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveStartupSettings
{
    internal static async void Configure(string category, StackPanel content, List<ChoicePicker> pickers, IStartupRegistration registration)
    {
        if (category != "General") return;
        var row = SettingsRow.Require(content, "AutostartEnabled").Reset(pickers);
        var toggle = AppToggleSwitch.Create(false);
        toggle.IsEnabled = false;
        AutomationProperties.SetName(toggle, Loc.T("Start TypeWhisper with Windows"));
        row.Set(Loc.T("Start with Windows"), Loc.T("Open TypeWhisper when you sign in to Windows."),
            Loc.T("Starts in the tray when you sign in. Windows can also disable it in Startup apps."), toggle);
        var updating = false;
        void Show(StartupRegistrationState state)
        {
            updating = true;
            toggle.IsOn = state.IsEnabled; toggle.IsEnabled = state.CanChange;
            row.Status = state.Error ?? "";
            updating = false;
        }
        toggle.Toggled += async (_, _) =>
        {
            if (updating) return;
            toggle.IsEnabled = false;
            Show(await registration.SetEnabledAsync(toggle.IsOn));
        };
        Show(await registration.ReadAsync());
    }
}
