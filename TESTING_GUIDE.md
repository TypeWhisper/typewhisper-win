# WinUI-Testanleitung

## Voraussetzungen

- Windows 11 24H2 oder neuer (Build 26100+), .NET 10 SDK.
- Für Audio-Smoke-Tests: Mikrofon und ein konfigurierter portabler Provider.
- Lokaler Build und Start des aktuellen Checkouts:

```powershell
& F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1 --run <checkout-pfad>
```

## Automatisierte Prüfungen

```powershell
./eng/Test-WinUIHeadless.ps1 -Configuration Release
./eng/Get-ChangedPluginProjects.Tests.ps1
./eng/Test-WinUIDailyCandidate.Tests.ps1
./eng/Test-WinUILocalAudio.Tests.ps1
```

Die Headless-Suites prüfen Core, CLI, Plugin-Host, Anwendungslogik und alle portablen Provider. Ergebnisse stehen unter `artifacts/test-results/winui-headless`.

Die Windows-Suite `TypeWhisper.Platform.Tests` prüft die übernommenen Lizenz-, Update- und Zwischenablage-Dienste ohne WPF. Die alten WPF-Testprogramme sind entfernt.

## Manueller Smoke-Test

- [ ] WinUI startet aus dem vorgesehenen Entwicklungsverzeichnis; Einstellungen und Tray sind erreichbar.
- [ ] Navigation, Suche, Hell-/Dunkelmodus und Overlay funktionieren.
- [ ] Installierte portable Plugins laden; Einstellungen lassen sich speichern.
- [ ] Diktat starten und stoppen; Ergebnis in einen Editor einfügen und in History prüfen.
- [ ] Eine Audiodatei transkribieren, Ergebnis öffnen und exportieren.
- [ ] Recorder starten, stoppen und gespeicherte Aufnahme abspielen.
- [ ] Lizenzstatus, Hotkeys und Einstellungen nach Neustart prüfen.

## Paketierung

- `Package Dry Run`: WinUI-Installer und portable ZIPs für x64 und ARM64.
- `WinUI Daily`: validierte WinUI-Kandidaten und Daily-Veröffentlichung.
- `Store Package`: WinUI-MSIX mit Mindestversion Windows Build 26100.

Die alten WPF-Builds, UI-Automationsskripte und Release-Workflows sind entfernt. Historische Abnahmeberichte unter `docs/` beschreiben frühere Versionsstände.
