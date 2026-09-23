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

Open Quick Launch from the tray or your configured launcher shortcut. Search for
a destination, or open **Suggestions** to see entries that are not pinned.
Pinned commands can be reordered and removed through their action menu.

| Workspace | Use it to |
| --- | --- |
| History | Find, copy and export saved transcripts. |
| Recorder | Record microphone/system audio and manage saved recordings. |
| Transcribe file | Queue audio/video files and export completed transcripts. |
| Workflows | Configure reusable text processing and explicit output actions. |
| Dictionary | Manage terms, corrections and term packs. |
| Snippets | Expand spoken triggers into reusable text. |
| Settings | Configure recording, shortcuts, appearance, account and updates. |

Use Integrations to manage installed plugins and their settings. Installing a
plugin, configuring it, downloading a model and selecting that model are separate
steps. Save edited plugin settings before running its actions.

## Workflows and automation

A workflow can choose transcription options, process text with an explicitly
configured LLM, and send output to an enabled action plugin. A workflow that
requires an unavailable provider reports that problem rather than choosing
another service silently. Memory context is explicitly selected per workflow.

For scripts and external tools, start with the [CLI guide](WINDOWS-CLI.md).
It explains how to enable the local HTTP API and install the bundled command.
