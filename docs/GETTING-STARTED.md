# Getting started with Windows 1.1

## Install

Choose the Windows installer for your architecture from
[GitHub Releases](https://github.com/TypeWhisper/typewhisper-win/releases).
Daily builds are prereleases. Application releases and plugin releases share the
repository, so select an application installer rather than a plugin ZIP.

Setup installs .NET 10 Runtime when needed. The portable ZIP requires that runtime
separately. The current source configures Windows build 19041 as its minimum;
native acceptance on older Windows and ARM64 is still pending. Store packages
have their own minimum. Check the notes accompanying your actual package.

Existing 1.0 installations and early 1.1 installations have different package
identities. The [upgrade guide](DAILY-1.1-CANDIDATE.md) explains which update feed
applies, what is copied and what remains in the previous profile.

## Complete your first dictation

1. Follow the setup wizard to install a transcription plugin and select a model.
   Local models require a download; cloud providers require their own configuration.
2. Select a microphone and check the recording shortcut. New profiles default to
   **Ctrl+Shift**; existing profiles keep their saved shortcut.
3. Focus a text field in another application. Start and stop dictation using the
   selected recording mode and shortcut.
4. Wait for processing. With automatic insertion enabled, TypeWhisper attempts
   to insert the completed text into the target application.

If a result window opens, its heading explains why. See
[dictation results](DICTATION-RESULTS.md) for the next step.

## Find the main workspaces

Click the tray icon to open Settings. As in the macOS app, its sidebar holds
every workspace: **Home** shows your activity and recent transcriptions, followed
by the settings pages, Dictionary, Snippets, Workflows, installed plugins and
your account. Right-click the tray icon for quick actions: start or stop a
Recorder session, open Settings or History, pause dictation shortcuts,
transcribe a file, and insert, copy or read back recent transcriptions.

| Workspace | Use it to |
| --- | --- |
| History | Open its own window with the Inbox, all entries, entries with audio, failed entries and entries per device and source. Search, edit, copy, export and mark entries complete. |
| Workflow palette | Run a workflow on the text selected in any app. The result replaces the selection; if that is not possible, it is copied. Assign the shortcut in **Settings → Shortcuts**. |
| Recorder | Record microphone/system audio and manage saved recordings. |
| File transcription | Queue audio/video files and export completed transcripts. |
| Workflows | Configure reusable text processing and explicit output actions. |
| Dictionary | Manage terms, corrections and term packs. |
| Snippets | Expand spoken triggers into reusable text. |

The **Recent transcriptions** shortcut and tray entry open a small palette with
your latest transcriptions. Choose one to insert it into the app you were using.
Without selected or copied text, the workflow palette offers the same list.

Messages that need your attention, such as a shortcut that could not be
registered, appear in a card at the position of the recording overlay. The card
does not take focus from the app you are using and closes on its own.

Use Integrations to manage installed plugins and their settings. Installing a
plugin, configuring it, downloading a model and selecting that model are separate
steps. Save edited plugin settings before running its actions.

Switching from Wispr Flow or Handy? Use the optional import link below the setup
wizard's welcome introduction. You can also use **Import → From another app…**
in Dictionary or Snippets to review and add your existing entries. See the
[import guide](IMPORT-FROM-OTHER-APPS.md) for supported content and formats.

## Suggest dictionary aliases

In Dictionary, choose **Suggest aliases…**, or use a word's or correction group's
context menu to start with its spelling. Enter the correct term, choose German
or English as the spoken language, and generate suggestions with a loaded local
language model. For example, enable Local LLM in Plugins and download and load
a Gemma 4, Qwen3.5 or LFM2.5 model first. This feature only uses local LLMs, even if your default workflow
provider is a cloud service.

Select the misheard variants you want to replace, then choose **Save aliases**.
The correct spelling is added under Words when needed; selected aliases are
saved under Corrections and apply to subsequent dictations. Existing corrections
are kept, including disabled entries. Review suggestions carefully because each
saved alias becomes an automatic replacement. Canceling adds no entries.

## Sync History with your Mac

Cloud-folder sync requires a commercial license. In **Settings → Sync & backup**,
choose the same folder that TypeWhisper on your Mac uses, for example in iCloud
Drive, OneDrive, Dropbox or a network share. Both apps create a
`typewhisper-sync` folder inside it. If you pick that `typewhisper-sync` folder
itself, Windows uses the folder that contains it.

Turn on **Sync History & Inbox** to exchange the text and Inbox state of your
entries. History then lists your Mac and iPhone as separate devices. Deleting an
entry in History removes it on every device. An entry that is merely missing on
another device is not deleted. **Sync Audio for New Entries** also copies audio for entries created after
you turn it on, and requires **Keep dictation audio**.

## Workflows and automation

A workflow can choose transcription options, process text with an explicitly
configured LLM, and send output to an enabled action plugin. A workflow that
requires an unavailable provider reports that problem rather than choosing
another service silently. Memory context is explicitly selected per workflow.

To switch between normal dictation and native English translation with separate
shortcuts, create two **Dictation Only** workflows. Set both to **Shortcut ·
dictation**, assign different shortcuts, and select **Transcribe** for one and
**Translate to English** for the other under **Transcription task**. Press a
workflow shortcut once to start recording and again to stop.

The task applies only to that recording. **Use global setting** keeps the task
selected in Dictation; existing workflows use this default. App, Website and
Global fallback workflows can also select a recording task.

Native translation produces English directly with the selected transcription
model, without an LLM. Local models work offline after download. Choose a
translation-capable model: Whisper's English-only and Large V3 Turbo models
(including quantized Turbo) cannot translate. An incompatible model stops the
workflow with an explanation. The **Translation** template instead translates
text through a configured LLM and can target other languages.

For scripts and external tools, start with the [CLI guide](WINDOWS-CLI.md).
It explains how to enable the local HTTP API and install the bundled command.
