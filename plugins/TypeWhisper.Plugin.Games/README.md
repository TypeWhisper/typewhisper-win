# Games portable plugin

Dictation into game chats. World of Warcraft comes with the plugin, in all versions (Retail, Classic, Classic Era, Forever and the test realms), and users can add any other game with the key its chat opens with.

Version `1.0.0`; plugin ID `com.typewhisper.games`; minimum host `1.1.7` (TypeWhisper for Windows 1.2.3 and 1.3).

## How it works

The plugin is a dictation target: it lists the process names of every game that is turned on, and TypeWhisper hands it only dictations into those apps. While such a game is in front, a dictation:

1. opens the chat with the game's key: the TypeWhisper add-on's chord (Ctrl+Shift+F11) for World of Warcraft, Enter without the add-on, or the key chosen for an added game,
2. pastes the text the way TypeWhisper pastes dictation, following the paste and clipboard settings,
3. presses Enter, unless sending is turned off.

Long text goes out as several messages, one second apart, so chat throttles keep them: up to 255 UTF-8 bytes each in World of Warcraft, and up to the limit chosen for an added game. A message never starts with a slash, so a dictation cannot run a game command. Keys are only sent while the game is in front.

### World of Warcraft

With the add-on installed and its cast bar turned on, the plugin also presses Ctrl+Shift+F8, F9 and F10 while you dictate. The add-on shows recording and transcribing as a cast bar in the game, and TypeWhisper hides its own indicator meanwhile. Errors, copies and the Escape warning still show in TypeWhisper.

Sending, the cast bar and a translation language are set on the add-on's page in the game (`/tw`). The plugin reads them from the add-on's saved variables when a dictation starts; the game saves them on `/reload` or logout. A chosen language makes TypeWhisper translate the dictation with the default LLM of its workflows. Without a ready LLM the message goes out as dictated, and the add-on's page says so. See the [add-on README](../../addons/wow/README.md) for its keys, settings and files.

### Added games

An added game has a name, its program names as Task Manager shows them, the key that opens its chat (Enter, T, Y, U, or nothing when the player opens it), whether Enter sends, the longest message, and a language to translate into. Without an add-on the game cannot tell whether its chat is open, and the key would close an open one, so dictation starts with the chat closed, and TypeWhisper's own indicator shows the dictation. Two games cannot share a program name.

## Setup

Install and turn on the plugin under **Settings › Plugins**. Its page lists the games on the left; the right side shows the chosen game's state and settings.

- **World of Warcraft:** click **Install add-on**. The plugin finds the game through Battle.net's product database and the default folders; the empty **World of Warcraft folder** field shows where. If the game is installed elsewhere, choose its folder (the one that holds `_retail_`, `_classic_` or `_forever_`) with **Browse…** and save. The button reads **Update add-on** when a version has an older add-on and **Reinstall add-on** when every version is current.
- **Another game:** click **+**, start the game, pick its program from the list, choose the key that opens its chat and save. **Remove game…** takes it off the list.

Each game can be turned off without removing it; TypeWhisper then pastes into it as into any other app. Settings are saved under `games.v1` in the plugin's data.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.Games/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.Games/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.games` inside the plugin project. Package that directory as the ZIP root. The add-on files in `addons/wow/TypeWhisper` are embedded in the plugin assembly.

Tests cover message splitting, chat delivery and its failure steps, the cast bar signals and their timeouts, reading the add-on's saved settings, Battle.net's product database, installing the add-on, the game list with added games and their validation, translations for every string shown, and installing and running the package as a dictation target on a host that offers one.
