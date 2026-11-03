# TypeWhisper add-on for World of Warcraft

Lets TypeWhisper dictation write into the World of Warcraft chat reliably and shows the dictation in the game.

TypeWhisper cannot talk to the game directly. Add-ons have no network, file or clipboard access, so TypeWhisper only sends keys: it opens the chat, pastes the dictated text and presses Enter. Pressing Enter to open the chat fails when the chat is already open, because Enter then sends or closes it.

## Keys

| Chord | Meaning |
| --- | --- |
| Ctrl+Shift+F11 | Open the chat. Bound to the game's own *Open Chat* command. |
| Ctrl+Shift+F8 | Recording started. |
| Ctrl+Shift+F9 | Recording ended, TypeWhisper is transcribing. |
| Ctrl+Shift+F10 | The dictation was canceled or failed. |

Ctrl+Shift+F11 opens a closed chat. An open chat ignores it, so its text and focus stay as they are.

The other three chords drive a cast bar in the game's own style:

- **Recording:** a green waveform, like a channeled spell. No audio reaches the game, so the waveform is drawn, not measured.
- **Transcribing:** the bar fills like a spell cast.
- **Done:** the bar completes when TypeWhisper opens the chat for the text, or when the text arrives in a chat that was already open. No extra key is needed.
- **Canceled or failed:** the bar turns red, like an interrupted cast.

TypeWhisper sends these chords only while the game is in front. It never releases a Ctrl or Shift you are holding for your dictation shortcut.

While the chat input has focus, it takes every key and key bindings do not fire. The add-on then listens on the chat input for its three chords. It only listens: the chat's own handlers run first and unchanged, and the add-on never writes to the chat. If a chord never arrives, the bar gives up by itself.

Sending stays with Blizzard's own code, so it also works during encounters. The bindings are override bindings: they sit on top of your own key bindings and are never saved with them.

## Install

In TypeWhisper, open **Settings › Advanced › Integrations**, turn on **World of Warcraft** and click **Install add-on**. TypeWhisper finds World of Warcraft through Battle.net and copies the add-on into every installed version (Retail, Classic, Forever). If the game is running, type `/reload`.

For a manual install, copy the `TypeWhisper` folder into `World of Warcraft\<version>\Interface\AddOns\`.

## Settings

Everything about dictating in the game is set on the add-on's page in **Options › AddOns › TypeWhisper**. Type `/tw`, or on Retail and Forever use the add-on menu on the minimap.

- **Send messages automatically:** Enter sends each message. Turn it off to check the text in the chat before you send it.
- **Show the cast bar while you dictate:** turn it off to see TypeWhisper's own indicator instead.
- **Translate my messages into:** pick a language from the list. TypeWhisper translates each dictation with the default LLM of its workflows, and the page names that LLM. Without a ready LLM, messages go out untranslated and the page says so in red. A workflow of your own for World of Warcraft in TypeWhisper takes precedence.

The add-on cannot reach TypeWhisper, so TypeWhisper reads these settings from the file the game saves them in. The game writes it when you type `/reload` or log out. After a change, the page offers a **Reload now** button.

The other way round, TypeWhisper writes `Status.lua` into the add-on folder when you dictate into the game or open **Settings › Advanced**. It names the translating LLM. The game loads it on the next `/reload`.

To answer a whisper, press the game's Reply key (R by default), then dictate. The page names your Reply key.

The page also shows whether the bindings are active and has buttons to test, move, size and reset the bar:

- `/tw` or `/typewhisper`: reports whether the bindings are active and opens the settings page.
- `/tw test`: plays a dictation on the cast bar.
- `/tw move`: unlocks the bar so you can drag it; type it again to lock it.
- `/tw scale 120`: sets the bar size, from 50 to 200 percent.
- `/tw reset`: puts the bar back in its default place.

The add-on keeps the last 40 signals of a session in its saved variables for troubleshooting. It records only the three signal keys and its own state changes, never typed text.

## Files

- `TypeWhisper.toc`: supported game versions, icon and saved variables
- `Status.lua`: written by TypeWhisper; ships empty
- `TypeWhisper.lua`: the bindings, the cast bar and the settings page
- `Icon.tga`: the TypeWhisper logo for the add-on list, 64×64 pixels. The game loads TGA or BLP, not PNG; it is made from `assets/icon.png`.

Bump `## Version` in the TOC when the add-on changes. TypeWhisper compares this version to offer an update.
