using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveStartupSettings
{
    // The reconciler applies LaunchAtLogin and remembers the user's own choice; the row follows its outcome. Policy
    // passes run through the window's serialized entry point so the row never races the policy-change handler.
    internal static async void Configure(string category, StackPanel content, List<ChoicePicker> pickers,
        Func<bool, Task<StartupPolicyOutcome?>> change, Func<Task<StartupPolicyOutcome?>> apply)
    {
        if (category != "General") return;
        var row = SettingsRow.Require(content, "AutostartEnabled").Reset(pickers);
        var toggle = AppToggleSwitch.Create(false);
        toggle.IsEnabled = false;
        AutomationProperties.SetName(toggle, Loc.T("Start TypeWhisper with Windows"));
        row.Set(Loc.T("Start with Windows"), Loc.T("Open TypeWhisper when you sign in to Windows."),
            Loc.T("Starts in the tray when you sign in. Windows can also disable it in Startup apps."), toggle);
        var updating = false;
        void Show(StartupPolicyOutcome outcome)
        {
            updating = true;
            toggle.IsOn = outcome.State.IsEnabled;
            toggle.IsEnabled = outcome.State.CanChange && !outcome.Locked;
            row.Status = outcome.State.Error ?? outcome.LockReason ?? outcome.Notice ?? "";
            updating = false;
        }
        async Task ApplyPolicyAsync() { if (await apply() is { } outcome) Show(outcome); }
        toggle.Toggled += async (_, _) =>
        {
            if (updating) return;
            toggle.IsEnabled = false;
            if (await change(toggle.IsOn) is { } outcome) Show(outcome);
            else toggle.IsEnabled = true;
        };
        void OnPolicyChanged(ManagedPolicySnapshot _) => row.DispatcherQueue.TryEnqueue(async () => await ApplyPolicyAsync());
        ViewSubscriptions.Attach(row, () => WinUIManagedPolicy.Changed += OnPolicyChanged, () => WinUIManagedPolicy.Changed -= OnPolicyChanged);
        await ApplyPolicyAsync();
    }
}
