# Games portable plugin

Dictation into game chats. The first game is World of Warcraft, in all versions (Retail, Classic, Classic Era, Forever and the test realms).

Version `1.0.0`; plugin ID `com.typewhisper.games`; minimum host `1.1.7` (TypeWhisper for Windows 1.2.3 and 1.3).

## How it works

The plugin is a dictation target: it lists the game's process names, and TypeWhisper hands it only dictations into those apps. While the game is in front, a dictation then:

1. opens the chat with the TypeWhisper add-on's key (Ctrl+Shift+F11), or with Enter when the add-on is missing,
2. pastes the text the way TypeWhisper pastes dictation, following the paste and clipboard settings,
3. presses Enter.

Long text goes out as several messages of up to 255 UTF-8 bytes, one second apart, so the server's chat throttle keeps them. A message never starts with a slash, so a dictation cannot run a game command. Keys are only sent while the game is in front.

With the add-on installed and its cast bar turned on, the plugin also presses Ctrl+Shift+F8, F9 and F10 while you dictate. The add-on shows recording and transcribing as a cast bar in the game, and TypeWhisper hides its own indicator meanwhile. Errors, copies and the Escape warning still show in TypeWhisper.

Sending, the cast bar and a translation language are set on the add-on's page in the game (`/tw`). The plugin reads them from the add-on's saved variables when a dictation starts; the game saves them on `/reload` or logout. A chosen language makes TypeWhisper translate the dictation with the default LLM of its workflows. Without a ready LLM the message goes out as dictated, and the add-on's page says so. See the [add-on README](../../addons/wow/README.md) for its keys, settings and files.

## Setup

Install and turn on the plugin under **Settings › Plugins**, then click **Install add-on** on its page. The plugin finds World of Warcraft through Battle.net's product database and the default folders. If the game is installed elsewhere, enter its folder (the one that holds `_retail_`, `_classic_` or `_forever_`) under **World of Warcraft folder** and save. The button reads **Update add-on** when a version has an older add-on and **Reinstall add-on** when every version is current.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.Games/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.Games/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.games` inside the plugin project. Package that directory as the ZIP root. The add-on files in `addons/wow/TypeWhisper` are embedded in the plugin assembly.

Tests cover message splitting, chat delivery and its failure steps, the cast bar signals and their timeouts, reading the add-on's saved settings, Battle.net's product database, installing the add-on, the plugin's settings and install action, and installing and running the package as a dictation target on a host that offers one.
