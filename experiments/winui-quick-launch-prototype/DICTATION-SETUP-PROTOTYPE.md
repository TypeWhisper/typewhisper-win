# Dictation and setup preview

Design question: can everyday dictation be configured through model, language and output, with less-used preferences kept out of the initial view?

One iteration of the already selected Inter / blue-icon design system, not a new style comparison. The Mac reference is `TypeWhisper/Views/SetupWizardView.swift` and Marco's five setup screenshots. Windows-specific access remains an explicit placeholder; Apple-only engines and permission flows are not copied.

## Run

Build this isolated project with `dotnet build experiments/winui-quick-launch-prototype/TypeWhisper.WinUIPrototype.csproj -c Debug -r win-x64`.
Launch the resulting executable with `--settings` or `--setup`. Setup is also available through General → Open setup wizard.

## Scope

- `PrototypeDictationSettings.cs`: primary model/language/output controls, one Advanced disclosure, dependent fields, shared model selection.
- `PrototypeSetupState.cs`: bounded session-only step navigation and prerequisite validation.
- `PrototypeSetupWizard.cs`: five-step setup, local shortcut editor, sample model selection, simulated transcript, logo hover and reduced-motion-aware transitions.
- Existing catalog/window/launcher wiring keeps the prototype isolated. Search reveals Advanced without changing its preferences.

All choices live in the preview dictionary. No recording, permission request, real download, clipboard operation or production configuration occurs. Review-first is a configuration preview, not an implemented production delivery pipeline. Closing/skipping preserves choices within the session. Finishing resets wizard navigation, not the chosen preferences.

## Validation

Model and wizard checks: `dotnet run --project experiments/winui-models-prototype-check/TypeWhisper.ModelsPrototypeCheck.csproj` (40 checks).
Search checks: `dotnet run --project experiments/winui-settings-search-prototype-check/TypeWhisper.SettingsSearchPrototypeCheck.csproj` (8 checks).

Observed at 1040×780: collapsed Dictation, Welcome, Microphone, Shortcut and Model steps. Computer Use sometimes returned a foreground app instead of the bound prototype, so the full sample-dictation path and final hover animation require manual confirmation. Narrow / high-DPI behavior uses the existing responsive settings host and scrollable wizard body but was not separately visually verified in this iteration.

Next production decision is pending user review; do not promote this code or connect real services automatically.
