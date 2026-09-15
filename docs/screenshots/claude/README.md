# Claude native settings acceptance

Captured on September 15, 2026 from the Windows x64 WinUI development app built from `7370e62b`, with Claude portable package `1.1.0`. These are actual native-window captures in the English dark theme, at 1040 × 780 pixels.

- [Settings](settings-dark.jpg): enabled plugin, Claude navigation logo, empty API-key field, connection/model actions, default model, provider-default temperature and disabled Save button.
- [Model list](models-dark.jpg): all four initial models are selectable and have readable labels. This is the built-in catalog, not an authenticated model discovery result.
- [Custom temperature](temperature-dark.jpg): choosing Custom reveals the numeric field and activates the unsaved-changes state and Save button. The provider-default mode was restored afterward without saving a custom value.

The plugin was enabled through its native management menu. No API key was entered, and no connection check, model refresh or inference request was sent. Live API/workflow acceptance is deferred because no Anthropic account is available. Light-theme and German-layout checks are not covered by these captures.

Earlier capture/input failures were resolved once the RDP session was reopened and the settings window activated.
