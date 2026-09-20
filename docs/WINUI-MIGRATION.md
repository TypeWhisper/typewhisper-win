# WinUI application architecture

`src/TypeWhisper.WinUI` is the sole application host. It owns the UI, platform services, icon, provider logos, audio cues and license strings. It has no source links or runtime references to a WPF application.

`TypeWhisper.Presentation` contains application logic independent of the desktop framework. `TypeWhisper.Core` supplies shared models, persistence and processing. `TypeWhisper.PluginHost` loads portable providers through the UI-independent SDK; provider settings are rendered by the WinUI host.

## Build and validation

Use the [current build instructions](../README.md#build) and [test guide](../TESTING_GUIDE.md). On the development machine, pass the current checkout to `F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1 --run`.

`eng/Test-WinUIHeadless.ps1` runs application and portable provider tests. Windows also runs the platform-service tests without a desktop UI framework. `Package Dry Run`, `WinUI Daily` and `Store Package` all target the WinUI application.

Existing user-data import and published-package compatibility boundaries remain explicit application behavior; they do not require the removed host or its plugin assemblies.
