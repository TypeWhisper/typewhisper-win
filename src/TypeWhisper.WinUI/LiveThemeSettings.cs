using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveThemeSettings
{
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers, InterfaceThemeStore store, Func<Task<string?>> restart)
    {
        if (category != "General") return;
        var row = SettingsRow.Require(content, "InterfaceTheme").Reset(pickers);
        var picker = new ChoicePicker(); picker.Configure(Loc.T("Theme"), "desktop", "App theme");
        row.Set(Loc.T("Theme"), control: picker);
        var restartNow = new HandCursorButton { Content = Loc.T("Restart Now"), HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed, Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        void Refresh(string? error = null)
        {
            picker.SetOptions([
                new("System", Loc.T("System"), Loc.T("Follows the Windows app mode.")),
                new("Light", Loc.T("Light"), ""),
                new("Dark", Loc.T("Dark"), "")
            ], store.Saved.ToString());
            var pending = store.Saved != AppTheme.Applied;
            row.Status = error ?? (pending ? Loc.T("The theme change will take effect after restarting TypeWhisper.") : "");
            restartNow.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        }
        picker.SelectionChanged += id => Refresh(Enum.TryParse<InterfaceTheme>(id, out var theme) ? store.Save(theme) : null);
        restartNow.Click += async (_, _) =>
        {
            restartNow.IsEnabled = false;
            var error = await restart();
            restartNow.IsEnabled = true;
            if (error is not null) Refresh(error);
        };
        row.Below(restartNow); pickers.Add(picker);
        Refresh();
    }
}
