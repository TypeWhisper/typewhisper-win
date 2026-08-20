# Windows UI automation and screenshots

TypeWhisper has a debug-only UI automation mode and a FlaUI/UIA3 runner for deterministic Windows screenshots. The runner starts an isolated app process, seeds sample data at a fixed reference time, selects the requested UI language, and drives controls through stable AutomationIds.

The automation does not change the website. Its PNG output under `artifacts/screenshots/windows` is the source material that documentation, the website, or release metadata can consume separately.

## Requirements

- Windows 10 or newer with the .NET 10 SDK
- An active, unlocked interactive desktop session
- 100% display scaling for the canonical 1240 x 800 settings window images
- No minimized or disconnected Remote Desktop session during capture

The runner captures the visible desktop rectangle reported by UIA. Keep the automated window in the foreground and do not cover it while a run is active.

## Capture the app

From PowerShell:

```powershell
.\eng\Capture-WindowsScreenshots.ps1 -Scope App -Locales en
```

The script derives the displayed release version from the newest local stable tag such as `v1.0.8`, even though the isolated process is a Debug build. Pass `-DisplayVersion 1.0.9` explicitly when preparing assets before the matching stable tag exists.

The app catalog creates 20 PNG files per locale. It covers all 18 settings routes plus separate installed/marketplace integration views and the active premium state. Statistics and recovery are included even though they were absent from the original manual screenshot set.

To capture every supported app locale:

```powershell
.\eng\Capture-WindowsScreenshots.ps1 -Scope App -Locales de,en,ja,ru,zh-Hans
```

## Capture plugin settings

Build and capture selected plugin settings surfaces by project name or manifest ID:

```powershell
.\eng\Capture-WindowsScreenshots.ps1 `
    -Scope Plugins `
    -Locales en,de `
    -Plugins TypeWhisper.Plugin.OpenAi,com.typewhisper.script
```

Only plugins that expose a settings view produce a screenshot. Files are written to `<locale>/plugins/<plugin-id>.png`. The script also generates a local registry fixture from every checked-in plugin manifest so the marketplace page does not depend on the network.

Plugin windows are measured independently and grow to their actual settings content, so short and tall plugins keep different PNG heights. The runner verifies that the outer settings content is fully visible before capture and fails instead of writing a clipped image when the active desktop is too small. At 100% scaling, a 1080p desktop fits every currently checked-in Windows plugin settings view.

Plugin screenshots are platform-specific assets. Windows settings captures use `artifacts/screenshots/windows/<locale>/plugins/<plugin-id>.png`. The macOS pipeline must keep its own `macos/<locale>/plugins/<plugin-id>.png` counterpart because the available controls, layout, and capabilities can differ substantially. A later website integration should select or present both platform variants explicitly instead of treating either one as a shared fallback.

Pass every plugin project when a complete plugin catalog is needed:

```powershell
$plugins = Get-ChildItem .\plugins -Directory | Select-Object -ExpandProperty Name
.\eng\Capture-WindowsScreenshots.ps1 -Scope All -Locales en -Plugins $plugins
```

## General UI automation

The runner supports four commands:

```text
capture  Capture the maintained app and plugin screenshot catalog
smoke    Navigate to every settings route and verify its AutomationId
run      Execute a declarative JSON flow
tree     Print the current UI Automation tree
```

After the app and runner have been built, a custom flow can be executed like this:

```powershell
dotnet .\artifacts\ui-automation\runner\typewhisper-ui.dll run `
    --app .\artifacts\ui-automation\app\TypeWhisper.exe `
    --flow .\eng\ui-automation\history-flow.json `
    --output .\artifacts\ui-automation\flows `
    --language en
```

A flow supports `wait`, `invoke`, `click`, `capture`, `close`, and `settle` actions. `click` is useful for modal actions that must return immediately. `capture.file` is always resolved below the supplied output directory. Use `tree` to discover stable AutomationIds before authoring a flow.

## Isolation and safety

The app accepts `--ui-automation` only in Debug builds. Every run receives a unique mutex/event name and a temporary user-data root. In this mode TypeWhisper does not perform user-data migration, protocol registration, update checks, plugin downloads, license validation, microphone enumeration, hotkey registration, audio warmup, or API-server startup. A release build rejects the fixture mode.
