using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// Advanced › Integrations: dictation into the World of Warcraft chat. Sending, the cast bar and translation are set on
// the add-on's own page in the game, so TypeWhisper only turns chat dictation on and installs the add-on. Row tags
// match the settings search keys.
internal static class WowIntegrationSettings
{
    internal const string WowChatKey = "WowChat", WowAddOnKey = "WowAddOn";

    /// <param name="translationLlm">The shared workflow LLM that translates game chat and whether it can run; a
    /// translation chosen in the game is skipped without it.</param>
    internal static void AddTo(SettingsRows rows, GamePreferencesStore store, Func<(string? Name, bool Ready)> translationLlm)
    {
        var enabled = AppToggleSwitch.Create(store.Current.WorldOfWarcraft.Enabled);
        AutomationProperties.SetName(enabled, Loc.T("Dictate into the World of Warcraft chat"));
        var enabledRow = new SettingsRow(WowChatKey).Icon("gamepad").Set(GameCatalog.WorldOfWarcraft.Name,
            Loc.T("While World of Warcraft is in front, dictation opens the chat, inserts the text and sends it."),
            Loc.T("Works in all World of Warcraft versions, including Classic and Forever. Long text is sent as several messages of up to 255 bytes. Text never starts with a slash, so a dictation cannot run a game command."),
            enabled);
        rows.Children.Add(enabledRow);

        var install = new HandCursorButton { Content = Loc.T("Install add-on"), Style = (Style)Application.Current.Resources["PrimaryButtonStyle"] };
        AutomationProperties.SetName(install, Loc.T("Install the TypeWhisper add-on into World of Warcraft"));
        var choose = new HandCursorButton { Content = Loc.T("Choose folder…"), Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        AutomationProperties.SetName(choose, Loc.T("Choose the World of Warcraft folder"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(install); actions.Children.Add(choose);
        var addOnRow = new SettingsRow(WowAddOnKey).Set(Loc.T("TypeWhisper add-on"), "",
            Loc.T("The add-on opens the chat without closing an open one and shows your dictation as a cast bar. Set sending, the cast bar and translation on its page in the game: type /tw. TypeWhisper reads them when you type /reload or log out. To answer a whisper, press the game's reply key, then dictate."),
            actions);
        rows.Children.Add(addOnRow);

        IReadOnlyList<WowVersionFolder>? versions = null;
        string? chosenTranslation = null;
        var busy = false;
        var justInstalled = false;
        var refreshing = false;

        void Refresh()
        {
            refreshing = true;
            var wow = store.Current.WorldOfWarcraft;
            enabled.IsOn = wow.Enabled;
            addOnRow.Visibility = wow.Enabled ? Visibility.Visible : Visibility.Collapsed;
            var missing = versions?.Where(version => version.InstalledVersion != WowAddOn.BundledVersion).ToArray() ?? [];
            var installed = versions?.Where(version => version.InstalledVersion is not null).ToArray() ?? [];
            addOnRow.Description = versions is null ? Loc.T("Looking for World of Warcraft…")
                : versions.Count == 0 ? Loc.T("World of Warcraft was not found. Choose the game folder.")
                : missing.Length == 0 ? justInstalled
                    ? Loc.T("Installed for {0}. If the game is running, type /reload in the chat.", Names(versions))
                    : Loc.T("Installed for {0}. Settings: type /tw in the game.", Names(versions))
                : installed.Length == 0 ? Loc.T("Not installed yet. Found: {0}. Without it, TypeWhisper opens the chat with Enter, so start with the chat closed.", Names(versions))
                : Loc.T("Needs an update or install for {0}.", Names(missing));
            install.Content = missing.Length == 0 && versions is { Count: > 0 } ? Loc.T("Reinstall add-on") : Loc.T("Install add-on");
            install.Style = (Style)Application.Current.Resources[missing.Length == 0 ? "SecondaryButtonStyle" : "PrimaryButtonStyle"];
            install.IsEnabled = !busy && versions is { Count: > 0 };
            choose.IsEnabled = !busy;
            refreshing = false;
        }

        async Task ScanAsync()
        {
            versions = null; Refresh();
            var folder = store.Current.WorldOfWarcraft.Folder;
            var llm = translationLlm();
            (versions, chosenTranslation) = await Task.Run(() =>
            {
                var found = WowAddOn.FindInstallRoots(folder).SelectMany(WowAddOn.VersionFolders).ToArray();
                // The add-on page names the translating LLM after the next /reload in each version.
                foreach (var version in found) WowAddOn.WriteStatus(version.Path, llm.Name, llm.Ready);
                return (found, found.Select(version => WowAddOnSettings.Read(version.Path).TranslateTo).FirstOrDefault(language => language is not null));
            });
            Refresh();
            // A translation that cannot run is explained here too, without waiting for a /reload in the game.
            if (string.IsNullOrEmpty(addOnRow.Status) && chosenTranslation is not null && store.Current.WorldOfWarcraft.Enabled && !llm.Ready)
                addOnRow.Status = Loc.T("Translation is chosen in the game, but the default LLM is missing or unavailable, so messages go out untranslated. Open Default LLM in Workflows to choose one.");
        }

        string? Save(Func<GameChatPreferences, GameChatPreferences> change) =>
            store.Save(store.Current with { WorldOfWarcraft = change(store.Current.WorldOfWarcraft) });

        enabled.Toggled += (_, _) =>
        {
            if (refreshing) return;
            enabledRow.Status = Save(wow => wow with { Enabled = enabled.IsOn }) ?? "";
            Refresh();
        };
        install.Click += async (_, _) =>
        {
            if (versions is not { Count: > 0 } targets) return;
            busy = true; addOnRow.Status = ""; Refresh();
            try
            {
                var failures = await Task.Run(() => WowAddOn.Install(targets));
                addOnRow.Status = failures.Count == 0 ? ""
                    : Loc.T("The add-on could not be installed for {0}. Close the game and try again.", Names(failures.Select(failure => failure.Folder)));
                justInstalled = failures.Count < targets.Count;
            }
            finally { busy = false; }
            await ScanAsync();
        };
        choose.Click += async (_, _) =>
        {
            busy = true; Refresh();
            try
            {
                var picker = new FolderPicker(choose.XamlRoot.ContentIslandEnvironment.AppWindowId) { Title = Loc.T("Choose the World of Warcraft folder") };
                var folder = await picker.PickSingleFolderAsync();
                if (folder is null) return;
                // A slow network folder must not freeze the window.
                var path = folder.Path;
                if (!await Task.Run(() => WowAddOn.FindInstallRoots(path, detect: false).SelectMany(WowAddOn.VersionFolders).Any()))
                {
                    addOnRow.Status = Loc.T("This folder contains no World of Warcraft version. Choose the folder that holds _retail_, _classic_ or _forever_.");
                    return;
                }
                addOnRow.Status = Save(wow => wow with { Folder = folder.Path }) ?? "";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { addOnRow.Status = Loc.T("Could not select folder: {0}", ex.Message); }
            // A canceled picker or a folder without the game returns early; the buttons must come back either way.
            finally { busy = false; Refresh(); }
            await ScanAsync();
        };

        enabledRow.Status = store.Error ?? "";
        Refresh();
        _ = ScanAsync();
    }

    private static string Names(IEnumerable<WowVersionFolder> versions) => string.Join(", ", versions.Select(version => version.Name).Distinct());
}
