# TypeWhisper for Windows

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com)

Dictate into Windows applications, transcribe recordings and process text with
reusable workflows. Choose a local transcription plugin or configure a cloud
provider. The current application uses WinUI 3 and runs as `TypeWhisper.exe`.

**This branch contains Windows 1.1 development.** Daily builds are prereleases;
source availability does not imply stable-channel availability.

[Get started](docs/GETTING-STARTED.md) · [Documentation](docs/README.md) ·
[Releases](https://github.com/TypeWhisper/typewhisper-win/releases) ·
[Report an issue](https://github.com/TypeWhisper/typewhisper-win/issues)

## What you can do

- Dictate using global recording shortcuts, with a configurable overlay and
  provider-dependent live text. Automatic insertion follows your output settings.
- Record microphone/system audio, transcribe files and export text or timed
  subtitles when the provider supplies timestamps.
- Apply workflows for rewriting, translation and other text processing, with
  app/site matching, dedicated shortcuts and explicit plugin output actions.
- Keep personal terms, corrections and snippets, and browse optional local History.
- Install portable transcription, LLM, text-processing, speech, memory and action
  plugins through Integrations. Provider configuration and model availability vary.
- Control the running app through its optional local [HTTP API](docs/WINUI-HTTP-API.md)
  and bundled [CLI](docs/WINDOWS-CLI.md).

See the [capability map](docs/WINUI-FUNCTIONAL-STATUS.md) for scope and limitations.

## Install and upgrade

Select an application installer for your architecture from GitHub Releases.
Plugin ZIPs are separate downloads. Setup installs .NET 10 Runtime when missing;
the portable ZIP requires it separately. First setup needs internet access to
install a plugin and download model assets where applicable.

The source configures a minimum Windows build of 19041. Native acceptance on
older Windows and ARM64 remains pending; Store packaging has its own minimum.
Use the requirements attached to the actual release you install.

The original 1.0 installation and early separate 1.1 installations use different
package identities. Read [Daily delivery and migration](docs/DAILY-1.1-CANDIDATE.md)
for update-feed compatibility, imported data and rollout gates.

## Build

Development requires Windows, the .NET 10 SDK and Windows SDK 10.0.26100 or later.
The application project is `src/TypeWhisper.WinUI/TypeWhisper.WinUI.csproj`.

```powershell
dotnet build TypeWhisper.slnx
dotnet publish src/TypeWhisper.WinUI/TypeWhisper.WinUI.csproj -c Release -r win-x64 --self-contained false -o publish/win-x64
```

Use `win-arm64` for ARM64 packages. Published output includes the application
resources and bundled CLI. Cross-building is not hardware execution evidence.

On Marco's development machine, always build and launch the current checkout
through the external helper:

```powershell
& F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1 --run <checkout-path>
```

The helper launches the stable development output with a separate development
profile. Do not launch temporary worktree output or the installed production app
for development validation.

## Test and contribute

```powershell
./eng/Test-WinUIHeadless.ps1 -Configuration Release
```

The script discovers application and portable plugin .NET tests. Windows adds
platform-service tests. See the [test guide](TESTING_GUIDE.md) for package checks,
Python/JavaScript suites, native validation and evidence requirements.

| Directory | Responsibility |
| --- | --- |
| `src/TypeWhisper.WinUI` | Desktop UI, Windows services and application composition |
| `src/TypeWhisper.Presentation` | Application behavior independent of the desktop UI |
| `src/TypeWhisper.Core` | Shared models, persistence and processing |
| `src/TypeWhisper.PluginSDK`, `src/TypeWhisper.PluginHost` | Portable contracts, settings and package lifecycle |
| `src/TypeWhisper.Cli` | Local API command-line client |
| `plugins` | Provider implementations, manifests, package builds and tests |
| `tests`, `eng` | Regression suites, native probes and build/release tooling |
| `docs` | User guides, contracts, release guidance and historical evidence |
| `experiments` | Isolated research; not part of application releases |

Start with [architecture](docs/WINUI-MIGRATION.md), [plugin development](docs/PLUGIN-MIGRATION-1.1.md)
or [release readiness](docs/WINUI-PROGRESS.md). Historical WinUI implementation
journals are kept in the [archive](docs/archive/README.md).

## License

GPLv3; see [LICENSE](LICENSE). Commercial licensing is available under
[LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md). See [TRADEMARK.md](TRADEMARK.md)
for the trademark policy and [SECURITY.md](SECURITY.md) for vulnerability reporting.
