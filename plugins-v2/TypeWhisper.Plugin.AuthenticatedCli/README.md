# Authenticated Provider CLIs portable plugin

Authenticated CLI providers with process isolation, discovery, status refresh and portable provider/path settings.

Version `1.2.2`; plugin ID `com.typewhisper.authenticated-cli`; minimum host `1.1.2`.
Independent branch: `seofood/authenticatedcli-portable`, based on `4db8f6ac`.

## Setup

Install and sign in to a supported CLI separately, refresh its status in plugin settings, then select it in an LLM workflow.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.AuthenticatedCli` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared during migration; it was not modified. The Windows provider discovers Codex models through the installed CLI app-server model/list protocol, offers documented Claude model aliases, and retains the verified free OpenCode Zen model list. Per-provider model settings are passed explicitly to the CLI, with workflow model overrides taking precedence. Antigravity is unavailable without a supported structured-output contract.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.AuthenticatedCli/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.AuthenticatedCli/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.authenticated-cli` inside the plugin project. Package that directory as the ZIP root.

Plugin regression tests cover installer junctions, npm native binary discovery, model pagination and explicit model arguments. Fake CLI protocol, executable discovery, process cancellation and package lifecycle. One real OpenCode test is intentionally opt-in via TYPEWHISPER_LIVE_OPENCODE_TEST=1. Activation can inspect installed CLI availability/authentication status; it does not submit inference requests. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.

## Automatic discovery and model selection

Automatic detection resolves local installer junctions to their native executable targets and checks the standard user-install and npm OpenCode locations. Script shims are not executed; remote and device paths remain excluded. Automatic selections are not pinned to a version directory. The settings picker displays the detected path and current availability, and one host Save button commits changes.

Refresh CLIs and models queries availability and the current Codex catalog without starting an inference session. Codex model discovery uses the documented [app-server model/list API](https://developers.openai.com/codex/app-server#list-models-modellist). Claude's named choices are [CLI aliases](https://code.claude.com/docs/en/model-config#model-aliases), not a fetched account catalog. OpenCode continues to expose only verified free Zen models.

Live development verification found Codex and npm OpenCode automatically. Model discovery returned six Codex entries and seven free Zen entries. Claude and Antigravity were not installed in the test environment. Existing user credentials are consumed by their respective CLIs; the plugin does not copy them.

A real synthetic text-processing request through the saved Codex model choice (`gpt-5.6-luna`) completed successfully with the expected German correction. Current Codex tool-disable configuration uses `features.view_image`; the obsolete `tools.view_image` override was removed.
