# Authenticated Provider CLIs portable plugin

Authenticated CLI providers with process isolation, discovery, status refresh and portable provider/path settings.

Version `1.2.0`; plugin ID `com.typewhisper.authenticated-cli`; minimum host `1.1.2`.
Independent branch: `seofood/authenticatedcli-portable`, based on `4db8f6ac`.

## Setup

Install and sign in to a supported CLI separately, refresh its status in plugin settings, then select it in an LLM workflow.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.AuthenticatedCli` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. The Windows provider retains its supported Codex/Claude default model contracts and verified OpenCode model list. Newer macOS Codex model catalog behavior is not included. Antigravity is unavailable without a supported structured-output contract.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.AuthenticatedCli/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.AuthenticatedCli/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.authenticated-cli` inside the plugin project. Package that directory as the ZIP root.

50 plugin tests pass. Fake CLI protocol, executable discovery, process cancellation and package lifecycle. One real OpenCode test is intentionally opt-in via TYPEWHISPER_LIVE_OPENCODE_TEST=1. Activation can inspect installed CLI availability/authentication status; it does not submit inference requests. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.
