using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.ModuleKit;
using TypeWhisper.Core;

namespace TypeWhisper.WinUI;

// Module record actions behind one "More actions" button, so any number of them leaves the host's own buttons in place.
internal static class ModuleActionMenu
{
    // Null when no module offers an action for these records now. The button stays while the view shows them, so its
    // items are asked again each time the menu opens: an action whose visibility changed meanwhile is added or left
    // out then, and a menu left without actions says so.
    internal static Button? For(IReadOnlyList<ModuleRecordInfo> records)
    {
        var actions = HostModules.ActiveCatalog.RecordActions;
        if (ModuleRecordActionState.Offered(actions, records, AppDiagnostics.WriteFailure).Count == 0) return null;
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => Fill(menu, actions, records);
        var title = Loc.T("More actions");
        var button = new HandCursorButton
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Flyout = menu,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"]
        };
        AutomationProperties.SetName(button, title);
        ToolTipService.SetToolTip(button, title);
        return button;
    }

    private static void Fill(MenuFlyout menu, IReadOnlyList<ModuleRecordAction> actions, IReadOnlyList<ModuleRecordInfo> records)
    {
        menu.Items.Clear();
        var recordIds = records.Select(record => record.Id).ToArray();
        foreach (var (action, state) in ModuleRecordActionState.Offered(actions, records, AppDiagnostics.WriteFailure))
        {
            var item = new MenuFlyoutItem { Text = state.Title, IsEnabled = state.Enabled, Icon = new FontIcon { Glyph = action.Glyph } };
            if (state.Help is { } help)
            {
                ToolTipService.SetToolTip(item, help);
                AutomationProperties.SetHelpText(item, help);
            }
            item.Click += (_, _) => ModuleRecordActionState.Perform(action, [.. recordIds], AppDiagnostics.WriteFailure);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("No actions available"), IsEnabled = false });
    }
}
