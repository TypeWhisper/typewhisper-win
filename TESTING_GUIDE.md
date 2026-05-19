# Feature-Parität: Manuelle Testanleitung

## Voraussetzungen

- Windows 10/11 mit .NET 10 Desktop Runtime
- Mikrofon angeschlossen
- App starten (aus WSL):
  ```bash
  # Erst PluginSDK bauen
  "/mnt/c/Program Files/dotnet/dotnet.exe" build "\\\\wsl.localhost\\Ubuntu-24.04\\home\\marco\\projects\\typewhisper-win\\src\\TypeWhisper.PluginSDK\\TypeWhisper.PluginSDK.csproj"
  # Dann Solution
  "/mnt/c/Program Files/dotnet/dotnet.exe" build "\\\\wsl.localhost\\Ubuntu-24.04\\home\\marco\\projects\\typewhisper-win"
  # Run
  "/mnt/c/Program Files/dotnet/dotnet.exe" run --project "\\\\wsl.localhost\\Ubuntu-24.04\\home\\marco\\projects\\typewhisper-win\\src\\TypeWhisper.Windows" --no-build
  ```

---

## Phase 1: PostProcessingPipeline + DB Migration v6

### 1.1 DB Migration
1. App starten → prüfen, dass keine Fehler in der Konsole
2. In `%LOCALAPPDATA%\TypeWhisper\Data\typewhisper.db` mit SQLite-Tool prüfen:
   ```sql
   PRAGMA user_version;  -- Sollte 6 sein
   SELECT sql FROM sqlite_master WHERE name='prompt_actions';  -- target_action_plugin_id, hotkey_key vorhanden
   SELECT sql FROM sqlite_master WHERE name='transcription_history';  -- model_used vorhanden
   SELECT sql FROM sqlite_master WHERE name='profiles';  -- hotkey_data vorhanden
   ```

### 1.2 PostProcessingPipeline
1. Modell laden (z.B. Parakeet via Marketplace)
2. Dictation starten → Text wird normal transkribiert
3. Prüfen, dass Dictionary-Korrekturen weiterhin angewendet werden
4. Prüfen, dass Snippet-Expansion weiterhin funktioniert

### 1.3 Action Plugin Routing
1. Prompt Action erstellen mit `TargetActionPluginId` (z.B. `com.typewhisper.linear`)
2. Plugin installieren und konfigurieren
3. Prompt Action ausführen → Text sollte an Plugin statt ins Textfeld gehen

---

## Phase 2: HTTP API

> **Hinweis:** API Server muss in Einstellungen → Erweitert → API Server aktiviert sein. Standardmäßig ist Auth aus. Wenn "API-Token verlangen" aktiv ist, bleibt `/v1/status` öffentlich; alle anderen Routen brauchen `Authorization: Bearer <token>` oder `X-TypeWhisper-API-Token`.

### 2.1 Discovery + Auth
```powershell
$discovery = Get-Content "$env:LOCALAPPDATA\TypeWhisper\api-discovery.json" | ConvertFrom-Json
$token = $discovery.token
curl http://localhost:$($discovery.port)/v1/status
curl -H "Authorization: Bearer $token" http://localhost:$($discovery.port)/v1/models
curl -i -X OPTIONS http://localhost:$($discovery.port)/v1/models
```
→ `api-discovery.json` enthält `version`, `port`, `token`; `api-port` existiert weiter. `OPTIONS` antwortet mit `204` ohne JSON-Body.

### 2.2 History
```bash
curl "http://localhost:8978/v1/history?limit=5"
curl "http://localhost:8978/v1/history?q=test"
curl -X DELETE "http://localhost:8978/v1/history?id=SOME_ID"
```

### 2.3 Workflows / Rules / Profiles
```bash
curl http://localhost:8978/v1/rules
curl http://localhost:8978/v1/profiles
curl -X PUT "http://localhost:8978/v1/rules/toggle?id=SOME_WORKFLOW_ID"
curl -X PUT "http://localhost:8978/v1/profiles/toggle?id=SOME_WORKFLOW_ID"
```
→ Windows liefert im Feld `bundle_identifiers` die vorhandenen Prozessnamen.

### 2.4 Dictionary Terms + Corrections
```bash
curl http://localhost:8978/v1/dictionary/terms
curl -X PUT http://localhost:8978/v1/dictionary/terms ^
  -H "Content-Type: application/json" ^
  -d "{\"terms\":[\"TypeWhisper\",\"Raycast\"],\"replace\":false}"
curl -X DELETE http://localhost:8978/v1/dictionary/terms ^
  -H "Content-Type: application/json" ^
  -d "{\"term\":\"Raycast\"}"

curl http://localhost:8978/v1/dictionary/corrections
curl -X PUT http://localhost:8978/v1/dictionary/corrections ^
  -H "Content-Type: application/json" ^
  -d "{\"original\":\"teh\",\"replacement\":\"the\",\"caseSensitive\":false}"
curl -X DELETE http://localhost:8978/v1/dictionary/corrections ^
  -H "Content-Type: application/json" ^
  -d "{\"original\":\"teh\"}"
```

### 2.5 Dictation Control
```bash
curl http://localhost:8978/v1/dictation/status
curl -X POST http://localhost:8978/v1/dictation/start
curl -X POST http://localhost:8978/v1/dictation/stop
```

### 2.6 Transcribe
```bash
# Multipart oder raw audio
curl -X POST http://localhost:8978/v1/transcribe \
  -F "file=@test.wav" \
  -F "language=de"

# Mac-kompatible Local-File-Route ohne Byte-Upload
curl -X POST "http://localhost:8978/v1/transcribe/local-file?await_download=1" ^
  -H "Content-Type: application/json" ^
  -d "{\"path\":\"C:\\Audio\\test.wav\",\"language_hints\":[\"de\",\"en\"],\"task\":\"transcribe\"}"
```

### 2.7 CLI / Raycast-Pfad
```powershell
$env:TYPEWHISPER_API_TOKEN = $token
typewhisper status
typewhisper models
typewhisper transcribe C:\Audio\test.wav --language de --json
typewhisper transcribe C:\Audio\test.wav --api-token $token --await-download
```

---

## Phase 3: Model Auto-Unload

1. Einstellungen → (AppSettings.json oder via Code): `ModelAutoUnloadSeconds` auf z.B. `30` setzen
2. Modell laden → Transkription durchführen
3. 30 Sekunden warten
4. Prüfen, dass Modell automatisch entladen wird (Debug-Output: "Auto-unloading model after 30s idle")
5. Nächste Aufnahme → Modell wird automatisch wieder geladen

---

## Phase 4: History Export

1. Settings → History öffnen
2. Mindestens 2-3 Transkriptionen in der History
3. Export-Button klicken
4. **Alle 4 Formate testen:**
   - Text (*.txt): Einfaches Textformat mit Header
   - CSV (*.csv): Komma-separiert, in Excel öffnen
   - Markdown (*.md): Heading, Metadaten, Trennlinien
   - JSON (*.json): Strukturiertes Array, valid JSON
5. Jede exportierte Datei öffnen und Inhalt prüfen

---

## Phase 5A: GeminiPlugin

1. Plugin via Marketplace installieren (oder manuell: `plugins/TypeWhisper.Plugin.Gemini/` → App Plugins-Ordner kopieren)
2. Settings → Plugins → Gemini → API Key eingeben
3. "Testen" Button → Grüne Bestätigung
4. Prompt Action erstellen mit Provider "Google Gemini"
5. Transkribieren → Prompt Action auslösen → Gemini verarbeitet Text

---

## Phase 5B: LinearPlugin

1. Plugin installieren
2. Settings → Plugins → Linear → API Key eingeben
3. "Teams laden" → Teams sollten erscheinen
4. Team und Projekt auswählen
5. Prompt Action mit `TargetActionPluginId = com.typewhisper.linear` erstellen
6. Text diktieren → Prompt Action → Issue wird in Linear erstellt

---

## Phase 5C: ObsidianPlugin

1. Plugin installieren
2. Obsidian muss installiert sein mit mindestens einem Vault
3. Settings → Plugins → Obsidian
4. Vault sollte automatisch erkannt werden (oder manuell Pfad setzen)
5. Optional: Daily-Note-Modus aktivieren
6. Prompt Action mit `TargetActionPluginId = com.typewhisper.obsidian` erstellen
7. Text diktieren → Prompt Action → Markdown-Datei im Vault prüfen

---

## Phase 5D: ScriptPlugin

1. Plugin installieren
2. Settings → Plugins → Script Runner
3. "Add Script" → Name: "Uppercase", Command: `powershell -c "$input | ForEach-Object { $_.ToUpper() }"`, Shell: PowerShell
4. Script aktivieren (Checkbox)
5. Diktieren → Text sollte durch Script verarbeitet werden (hier: alles Großbuchstaben)
6. Zweites Script hinzufügen, Reihenfolge testen (Move Up/Down)

---

## Phase 5E: LiveTranscriptPlugin

1. Plugin installieren
2. Settings → Plugins → Live Transcript → Aktivieren
3. Optional: Font-Größe und Opazität anpassen
4. Dictation starten (mit einem Streaming-fähigen Modell, z.B. AssemblyAI)
5. Während der Aufnahme: schwebendes Fenster sollte am unteren Bildschirmrand erscheinen
6. Text erscheint live während des Sprechens
7. Nach Aufnahme: Fenster verschwindet nach 3 Sekunden

---

## Phase 6: Per-Profile Hotkeys

1. Settings → Profile → Profil auswählen/erstellen
2. Im Detail-Panel: neues "Profil-Hotkey" Feld
3. HotkeyRecorder anklicken → Tastenkombination drücken (z.B. Ctrl+Shift+1)
4. Profil speichern
5. **Test:**
   - Normales Hotkey drücken → Standard-Profil-Matching (nach App/URL)
   - Profil-Hotkey drücken → Dieses spezifische Profil wird aktiviert, unabhängig vom aktiven Fenster
6. Im Overlay: Profilname sollte angezeigt werden
7. Zweites Profil mit anderem Hotkey erstellen → Beide funktionieren unabhängig

---

## Phase 7: Audio Device Preview

1. Settings → Aufnahme
2. Level-Meter (Fortschrittsbalken) neben dem "Aktualisieren"-Button sichtbar
3. Ins Mikrofon sprechen → Balken reagiert in Echtzeit
4. Anderes Mikrofon im Dropdown auswählen → Level-Meter wechselt zum neuen Gerät
5. Zu einer anderen Settings-Sektion navigieren → Preview stoppt (kein CPU-Verbrauch)
6. Zurück zu Aufnahme → Preview startet wieder
7. Settings-Fenster schließen → Preview stoppt

---

## Allgemeine Checks

- [ ] App startet ohne Fehler
- [ ] Alle Settings-Sektionen laden korrekt
- [ ] Bestehende Funktionalität nicht gebrochen (Hotkeys, Overlay, Dictionary, Snippets)
- [ ] DB-Migration funktioniert (frische DB und Upgrade von v5)
- [ ] Export-Dateien haben korrekten Inhalt
- [ ] API-Endpoints antworten korrekt
- [ ] Plugins laden und zeigen Settings-UI
