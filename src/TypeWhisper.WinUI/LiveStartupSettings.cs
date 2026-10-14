using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveStartupSettings
{
    // The reconciler applies LaunchAtLogin and remembers the user's own choice; the row follows its outcome.
    internal static async void Configure(string category, StackPanel content, List<ChoicePicker> pickers, IStartupRegistration registration,
        StartupPolicyReconciler policy, Func<bool> setupCompleted)
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
            row.Status = outcome.State.Error ?? outcome.LockReason ?? "";
            updating = false;
        }
        async Task ApplyPolicyAsync() => Show(await policy.ApplyAsync(WinUIManagedPolicy.Current, setupCompleted()));
        toggle.Toggled += async (_, _) =>
        {
            if (updating) return;
            toggle.IsEnabled = false;
            var state = await registration.SetEnabledAsync(toggle.IsOn);
            if (state.Error is null) policy.RecordUserChoice(toggle.IsOn);
            Show(new(state, false, null));
        };
        void OnPolicyChanged(ManagedPolicySnapshot _) => row.DispatcherQueue.TryEnqueue(async () => await ApplyPolicyAsync());
        ViewSubscriptions.Attach(row, () => WinUIManagedPolicy.Changed += OnPolicyChanged, () => WinUIManagedPolicy.Changed -= OnPolicyChanged);
        await ApplyPolicyAsync();
    }
}
