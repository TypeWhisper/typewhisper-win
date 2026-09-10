# Windows 1.1 CLI

The `typewhisper` command controls the running Windows app through its [local HTTP API](WINUI-HTTP-API.md).

## Install and connect

1. Open **Settings > Advanced > Command Line Tool** and select **Install**. Use **Update** to refresh an existing installation, or **Remove** to uninstall it.
2. Under **Settings > Advanced > HTTP API**, enable the server and select **Apply**. The server is off by default; the default port is `8978`.
3. Open a new terminal and run `typewhisper status`.

Installation copies the bundled CLI to `%LOCALAPPDATA%\TypeWhisper\1.1\Cli` and adds that directory to your user PATH. It does not require administrator access. If an older command takes precedence, inspect `Get-Command typewhisper -All` in PowerShell or invoke the new executable directly:

```powershell
& "$env:LOCALAPPDATA\TypeWhisper\1.1\Cli\typewhisper.exe" status
```

The installed CLI remembers the profile of the app that installed it. It reads that profile's `api-discovery.json` for the port and authentication mode/token, with `api-port` as a legacy fallback. An unbound CLI checks `%LOCALAPPDATA%\TypeWhisper-WinUI-DevUserData` before `%LOCALAPPDATA%\TypeWhisper`. Without valid discovery, the port falls back to `8978`.

Use `--profile <directory>` to select another profile, or set `TYPEWHISPER_PROFILE`. The explicit flag takes precedence over the environment variable, then the installed profile binding. For example:

```powershell
typewhisper status --profile "$env:LOCALAPPDATA\TypeWhisper-WinUI-DevUserData"
```

`--dev` selects `%LOCALAPPDATA%\TypeWhisper-WinUI-DevUserData` explicitly and does not fall back to production discovery. It overrides `TYPEWHISPER_PROFILE` and the installed profile binding; combining it with `--profile` is an input error.

`--port <N>` overrides discovery. `--api-token <token>` overrides `TYPEWHISPER_API_TOKEN`, which overrides the discovered token. A discovery token is only reused for its matching port, and is omitted when discovery reports authentication disabled. The CLI only connects to `127.0.0.1`.

## Commands

```powershell
typewhisper status
typewhisper models
typewhisper transcribe recording.wav --language de
typewhisper transcribe recording.wav --json
typewhisper history --limit 20 --offset 0
typewhisper history search "meeting notes"
typewhisper history --query "meeting notes" --json
typewhisper last
typewhisper export typewhisper-backup.json
typewhisper import typewhisper-backup.json
```

`history last` is an alias for `last`. History defaults to 50 entries, accepts `--limit` from 0 to 200, and accepts a nonnegative `--offset`.

Model operations use engine and model IDs returned by `typewhisper models`. Replace the example variables with those IDs:

```powershell
typewhisper models load --engine $engineId --model $modelId
typewhisper models unload --engine $engineId
typewhisper models delete --engine $engineId --model $modelId
```

Loading requires downloaded assets. Unload and delete depend on the plugin's support; deleting a selected model is protected. Model operations can fail while the app is busy.

Dictation returns a session ID. Keep it to retrieve the result after stopping:

```powershell
$session = typewhisper dictation start --json | ConvertFrom-Json
typewhisper dictation status
typewhisper dictation stop
typewhisper dictation result $session.id --json
```

Stopping returns before transcription finishes. Repeat `dictation result` while its status is `recording` or `processing`; a completed result includes `transcription`. Add `--workflow <id>` to `dictation start` to use an existing workflow. Workflow IDs are available through the HTTP API's `/v1/rules` endpoint. Session results belong to the running app; persisted transcripts remain accessible through history.

For binary stdin input, omit the file argument or use `-`. These equivalent redirection examples are for **Command Prompt**:

```cmd
typewhisper transcribe - < audio.wav
typewhisper transcribe < audio.wav
```

## Output and limits

- `--json` writes the API response as JSON to stdout; export instead returns `{ "file": "<absolute destination>", "bytes": <byte count> }`, matching the Mac CLI. Plain transcription and `last` output print the text. Errors go to stderr.
- Exit codes are `0` for success, `1` for invalid arguments/local input or cancellation, `2` for connection failures/timeouts, and `3` for server errors or malformed JSON responses. These failure categories match the Mac CLI. A failed dictation result or backup restore also returns `3`.
- The app and HTTP API must remain running. Requests time out after five minutes. Ctrl+C cancels the CLI request.
- Stdin audio is limited to 32 MiB. Ordinary file arguments use an absolute local path instead of uploading the file. Supported audio formats and engine capabilities follow the Windows API.
- `--task transcribe` is the CLI default. `--task translate` requires support from the selected engine/model. `--language` selects an explicit source language.
- `--engine` and `--model` temporarily select the engine/model for this transcription. The API restores the previous selection and loaded/unloaded state afterward. Use `--await-download` to wait for required model assets to restore or download.
- Repeat `--language-hint` for ordered source-language hints. Hints require engine support declared in the Windows registry; unsupported hints return HTTP 422. Do not combine hints with `--language`.
- `--translate-to <code>` translates the transcript using the configured, ready default workflow LLM. Configure it in Workflows first; an unavailable default returns HTTP 422. This is separate from the engine-native `--task translate`.
- `--no-corrections` skips dictionary corrections. API transcription does not apply UI snippets or dictation postprocessors; it returns the transcript with only the requested correction and translation stages.
- Backup export writes the file atomically. An import that changes the profile returns HTTP 202: the request is accepted, and TypeWhisper restarts to finish restoring it. CLI success at that point confirms acceptance, not completion. An unchanged import returns HTTP 200. See the API reference for backup contents and merge behavior.

Run `typewhisper --help` for syntax and `typewhisper --version` for the installed version.

## Mac CLI comparison

Compared with the Mac source at commit [`357fe6f`](https://github.com/TypeWhisper/typewhisper-mac/tree/357fe6f70a463ae376e485e5c1ef4f4d24db2c1e/typewhisper-cli). The CLI contracts below distinguish shared behavior from intentional platform differences.

| Area | Windows and Mac behavior | Sources |
| --- | --- | --- |
| Core commands | Both provide `status`, `models`, `transcribe`, `export`, and `import`. Windows additionally exposes model load/unload/delete, dictation sessions, history, and `last`. | [Windows commands](../src/TypeWhisper.Cli/Program.cs), [Mac commands](https://github.com/TypeWhisper/typewhisper-mac/blob/357fe6f70a463ae376e485e5c1ef4f4d24db2c1e/typewhisper-cli/main.swift) |
| Stdin and JSON | Both accept an omitted transcription file or `-` for stdin, print transcription text by default, and provide `--json`. Export JSON uses `file` and `bytes` on both platforms. Windows intentionally limits stdin to 32 MiB. | [Windows commands](../src/TypeWhisper.Cli/Program.cs), [Mac formatter](https://github.com/TypeWhisper/typewhisper-mac/blob/357fe6f70a463ae376e485e5c1ef4f4d24db2c1e/typewhisper-cli/OutputFormatter.swift) |
| Transcription options | Both accept ordered language hints, engine/model overrides, model-download waiting, target-language translation, and `--no-corrections`. Windows requires registry-declared hint support and a ready default workflow LLM for target translation; temporary model selection is restored afterward. Available engines and models remain platform-specific. | [Windows requests](../src/TypeWhisper.Cli/CliSupport.cs), [Windows API](WINUI-HTTP-API.md), [Mac client](https://github.com/TypeWhisper/typewhisper-mac/blob/357fe6f70a463ae376e485e5c1ef4f4d24db2c1e/typewhisper-cli/CLIClient.swift) |
| Errors | Both use input/connection/server exit codes `1`/`2`/`3`. Windows times out all API requests after five minutes; Mac uses ten seconds for ordinary status/model requests and five minutes for transcription and backups. | [Windows commands](../src/TypeWhisper.Cli/Program.cs), [Mac client](https://github.com/TypeWhisper/typewhisper-mac/blob/357fe6f70a463ae376e485e5c1ef4f4d24db2c1e/typewhisper-cli/CLIClient.swift) |
| Profiles | Both accept `--dev`. Mac normally discovers Production; an unbound Windows CLI checks WinUI Dev before Production. Windows additionally supports `--profile`, `TYPEWHISPER_PROFILE`, and an installed profile binding. `--dev` explicitly selects each platform's development profile. | [Windows discovery](../src/TypeWhisper.Cli/CliSupport.cs), [Mac discovery](https://github.com/TypeWhisper/typewhisper-mac/blob/357fe6f70a463ae376e485e5c1ef4f4d24db2c1e/typewhisper-cli/PortDiscovery.swift) |
| Authentication | Both support `--api-token`, `TYPEWHISPER_API_TOKEN`, and discovery. Windows only forwards a discovery token to its matching port and respects optional authentication. | [Windows discovery](../src/TypeWhisper.Cli/CliSupport.cs), [Mac commands](https://github.com/TypeWhisper/typewhisper-mac/blob/357fe6f70a463ae376e485e5c1ef4f4d24db2c1e/typewhisper-cli/main.swift) |
| Installation | Mac creates an administrator-authorized `/usr/local/bin/typewhisper` symlink into its app bundle, following app updates. Windows copies a user-local bundle, manages its user PATH entry and profile binding, and offers an explicit Update action. Check command precedence if another installation already exists. | [Windows installer](../src/TypeWhisper.Presentation/CliInstallation.cs), [Mac installation](https://github.com/TypeWhisper/typewhisper-mac/blob/357fe6f70a463ae376e485e5c1ef4f4d24db2c1e/TypeWhisper/Views/AdvancedSettingsView.swift#L702) |
| Backup results | Export output is shared. Import summaries and backup contents follow each platform's API; Windows may acknowledge a restart with HTTP 202 before the restore completes. | [Windows commands](../src/TypeWhisper.Cli/Program.cs), [Mac formatter](https://github.com/TypeWhisper/typewhisper-mac/blob/357fe6f70a463ae376e485e5c1ef4f4d24db2c1e/typewhisper-cli/OutputFormatter.swift) |
