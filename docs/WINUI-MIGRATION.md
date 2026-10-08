# WinUI application architecture

`src/TypeWhisper.WinUI` is the sole application host. It owns the UI, platform services, icon, provider logos, audio cues and license strings. It has no source links or runtime references to a WPF application.

`TypeWhisper.Presentation` contains application logic independent of the desktop framework. `TypeWhisper.Core` supplies shared models, persistence and processing. `TypeWhisper.PluginHost` loads portable providers through the UI-independent SDK; provider settings are rendered by the WinUI host.

## Build and validation

Use the [current build instructions](../README.md#build) and [test guide](../TESTING_GUIDE.md); the maintainer's helper script is described in [Maintainer's local setup](../TESTING_GUIDE.md#maintainers-local-setup).

`CI` builds the WinUI solution and runs `eng/Test-WinUIHeadless.ps1 -Suite App` for application and plugin host/SDK tests on Windows and Linux. Windows also runs the platform-service tests without a desktop UI framework. `Plugins` runs the plugin-owned tests on both systems with `-Suite Plugins`. The script defaults to all suites for local checks and `Candidate`. `Packaging`, `Candidate` and `Store` all target the WinUI application.

Existing user-data import and published-package compatibility boundaries remain explicit application behavior; they do not require the removed host or its plugin assemblies.
