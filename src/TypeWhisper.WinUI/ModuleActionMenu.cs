using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.ModuleKit;
using TypeWhisper.Core;

namespace TypeWhisper.WinUI;

// Module record actions behind one "More actions" button, so any number of them leaves the host's own buttons in place.
internal static class ModuleActionMenu
{
    // Null when no module offers an action for this many records.
    internal static Button? For(IReadOnlyList<string> recordIds)
    {
        var menu = new MenuFlyout();
        foreach (var action in HostModules.ActiveCatalog.RecordActions)
        {
            var state = ModuleRecordActionState.For(action, recordIds.Count);
            if (!state.Visible) continue;
            var item = new MenuFlyoutItem { Text = state.Title, IsEnabled = state.Enabled, Icon = new FontIcon { Glyph = action.Glyph } };
            if (state.Help is { } help)
            {
                ToolTipService.SetToolTip(item, help);
                AutomationProperties.SetHelpText(item, help);
            }
            item.Click += (_, _) => ModuleRecordActionState.Perform(action, [.. recordIds], AppDiagnostics.WriteFailure);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return null;
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
}
