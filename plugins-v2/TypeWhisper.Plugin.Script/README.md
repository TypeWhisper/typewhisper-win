# Script Runner portable plugin

Ordered text post-processing scripts configured through portable settings, with shell, timeout and enabled state per script.

Version `1.2.0`; plugin ID `com.typewhisper.script`; minimum host `1.1.2`.
Independent branch: `seofood/script-portable`, based on `4db8f6ac`.

## Setup

Add a script, supply its command and shell, then explicitly enable it. New entries are disabled. Reorder/remove entries through settings actions.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.Script` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. Windows uses cmd, PowerShell or pwsh. macOS shell commands and paths require manual adaptation; they are not copied implicitly.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.Script/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.Script/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.script` inside the plugin project. Package that directory as the ZIP root.

7 plugin tests pass. Harmless local PowerShell fixtures verify text transformation, timeout, cancellation, fail-open chaining, corrupt-store protection and package lifecycle. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.
