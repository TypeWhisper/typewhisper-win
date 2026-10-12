# Shared explicit save for portable plugin settings

Portable plugin configuration uses one persistent Save settings footer for all ordinary fields and an optional replacement API key. Providers with their own profile contract retain the existing per-profile save implementation. Key-only providers use the shared footer too.

Editing a field keeps changes in a draft until saved. Conditional fields respond to draft values. Drafts survive model/action refreshes and the existing navigation guard protects unsaved edits. A blank key retains the saved secret; removing it remains an explicit separate action. Model download/load operations, connection checks and provider actions remain separate operations. Generic provider actions require pending edits to be saved first.

The host validates published choice/length constraints before starting writes, uses the provider's configuration lease and saves only changed fields. Existing providers expose individual setters, so the generic operation cannot promise a transaction across their files and secret stores. If a later write fails, the result identifies completed fields, retains the remaining edits and reports partial completion. Provider exceptions and credential values are not copied into error feedback.

## Validation

Use the current source and the [test guide](../TESTING_GUIDE.md):

```powershell
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests -c Release
& F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1 --run <checkout-path>
```
