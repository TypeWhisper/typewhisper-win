using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Core;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class LiveLanguageSettings
{
    internal static void Configure(string category, StackPanel content, List<ChoicePicker> pickers, InterfaceLanguageStore store, Func<Task<string?>> restart)
    {
        if (category != "General") return;
        var row = content.Children.OfType<StackPanel>().Single(item => Equals(item.Tag, "UiLanguage"));
        foreach (var old in row.Children.OfType<ChoicePicker>()) pickers.Remove(old);
        row.Children.Clear(); row.IsHitTestVisible = true;
        row.Children.Add(SettingsHelp.Label(Loc.T("App Language"), Loc.T("The language change will take effect after restarting TypeWhisper.")));
        var picker = new ChoicePicker(); picker.Configure(Loc.T("App Language"), "dictionary", "App language");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var restartNow = new HandCursorButton { HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed, Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        void Refresh(string? error = null)
        {
            var chosen = store.Saved ?? Loc.Language;
            picker.SetOptions(Loc.Languages.Select(language => new Choice(language.Code, language.Name, "")).ToArray(), chosen);
            var pending = chosen != Loc.Language;
            // Someone who just picked a language may not be able to read the current one.
            status.Text = error ?? Loc.In(chosen, Loc.Mark("The language change will take effect after restarting TypeWhisper."));
            restartNow.Content = Loc.In(chosen, Loc.Mark("Restart Now"));
            status.Visibility = error is not null || pending ? Visibility.Visible : Visibility.Collapsed;
            restartNow.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        }
        picker.SelectionChanged += language => Refresh(store.Save(language));
        restartNow.Click += async (_, _) =>
        {
            restartNow.IsEnabled = false;
            var error = await restart();
            restartNow.IsEnabled = true;
            if (error is not null) Refresh(error);
        };
        row.Children.Add(picker); row.Children.Add(status); row.Children.Add(restartNow); pickers.Add(picker);
        Refresh();
    }
}
