# Script Runner

Local text post-processing with an ordered list of scripts. Version `1.3.0`, plugin ID `com.typewhisper.script`, minimum host `1.1.2`.

## Setup

Use the **Scripts** sidebar to add or select a script. Edit its name, shell, command, timeout and enabled state, then save the settings together. The list order is the execution order. Move earlier/later and remove actions operate on the selected script.

**Test script** executes the current draft with harmless sample text, including German umlauts, without saving it or enabling it for dictation. New scripts and template copies are disabled. Review their commands before enabling them.

Transcription text arrives on stdin; stdout becomes the replacement text. Scripts run locally with your Windows user permissions. A failed or timed-out script keeps its input and the chain continues. Cancellation stops execution. Timeouts are configurable from 1 to 300 seconds.

## Built-in templates

Choose a template and click **Add selected template** to create an editable, disabled copy. Existing scripts are preserved.

| Template | Behavior |
| --- | --- |
| UPPERCASE | Converts the full text to uppercase. |
| lowercase | Converts the full text to lowercase. |
| Trim whitespace | Removes whitespace at the beginning and end. |
| Clean up spacing | Collapses repeated spaces and tabs while preserving paragraphs. |
| Markdown bullet list | Formats non-empty lines as bullets; preserves existing bullets. |
| Markdown checklist | Formats non-empty lines as tasks; preserves existing checkboxes. |
| Markdown quotation | Quotes each line, including blank paragraph lines. |
| JSON string | Escapes quotes, backslashes and line breaks as one JSON string. |
| URL-encode text | Encodes a query parameter value, not a complete URL. |

The templates only transform stdin into stdout. They do not access files or the network. PowerShell and pwsh use UTF-8 input/output so characters such as ä, ö and ü survive. cmd commands may need to set their own code page when invoking native utilities.

Scripts can read `TYPEWHISPER_APP_NAME`, `TYPEWHISPER_LANGUAGE` and `TYPEWHISPER_PROFILE` from their environment. Windows supports cmd, PowerShell and pwsh; the latter must be installed separately.

## Build and verification

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.Script/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.Script/Tests -c Release
```

The package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.script` inside the plugin project. Package that directory as the ZIP root.

Tests cover every template through real PowerShell, Unicode, draft tests without persistence, batch validation, stale selection rejection, disabled defaults, restart persistence, timeout, cancellation, fail-open chaining, corrupt-store protection and portable package lifecycle. Shell execution tests require Windows; configuration and package tests also run headlessly on other platforms. Automated tests do not replace visual and dictation acceptance in the app.

## Migration scope

Runtime sources originated from `plugins/TypeWhisper.Plugin.Script` at `4db8f6ac`, adapted under `plugins-v2` without WPF. The macOS implementation at `ac00e39ea63e4789de8427d034d2085b3d898159` was compared but not modified. macOS commands and paths require manual adaptation. Legacy settings and scripts are not imported automatically; configuration remains in the portable plugin data directory. Public catalog publication remains separate.
