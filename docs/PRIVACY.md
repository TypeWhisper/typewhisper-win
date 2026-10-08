# Privacy Policy

TypeWhisper does not collect telemetry or analytics. Local transcription and text processing run on the user's device.

Audio, transcripts, prompts, or API keys leave the device only when the user explicitly configures and uses a cloud provider or integration.

Local history and settings are stored on the user's machine and can be deleted by the user.

## Network connections

Besides the cloud providers and integrations you configure, the Windows app makes these connections:

- **Plugin catalog** (`typewhisper.github.io`): fetched about one minute after every start and then once a day while "Update plugins automatically" is on (the default), and when you open Account & about or choose "Check for updates". A plain download without account data; plugin packages are then downloaded from the GitHub release URLs listed in the catalog.
- **App update check** (`api.github.com`): only when you choose "Check for updates" in Settings or the tray menu. Lists the releases of the `TypeWhisper/typewhisper-win` repository and downloads the selected package from GitHub. Microsoft Store builds do not use it; the Store delivers their updates.
- **License activation and validation** (`api.polar.sh`): only if you enter a license key. Activation sends the key, the TypeWhisper organization ID, your computer name as the activation label, the platform (`windows`), the app version and whether this is the Store or direct build. Validation and deactivation send the key, the organization ID and the activation ID; validation runs at start when due, every 7 days for commercial licenses and every 30 days for supporter licenses.
- **Premium account** (`app.typewhisper.com`): only after you sign in. Sign-in opens your browser; afterwards each request carries your session and a random device ID that is generated locally and stored in the profile folder (`premium-account-device.txt`). While signed in, the entitlement is refreshed at start.
- **Model and runtime downloads**: only when you download a model or install an acceleration runtime in a local plugin. Depending on the plugin, files come from `huggingface.co`, GitHub releases (`github.com`), `pypi.org`, `download.pytorch.org` and `developer.download.nvidia.com`. An optional Hugging Face token, if you enter one, is sent only to `huggingface.co`.

## Local data

- **Profile folder**: `%LocalAppData%\TypeWhisper-WinUI` (the public Store beta uses `TypeWhisper-WinUI-StoreBeta`). Settings, dictionary, snippets, workflows, history, license data and plugin data live there.
- **History**: on by default and kept until you delete entries or choose a retention period in Settings; stored in `history.json`.
- **History audio**: off by default. When on, the audio of eligible dictations is kept in `history-audio` next to the history.
- **Recovery audio**: off by default. When on, recordings are kept in `dictation-recovery` for 30 days by default.
- **Microphone pre-roll**: off by default. When enabled, the microphone stays active between dictations and the newest half-second is held in memory. Idle audio is not saved or uploaded. Starting a dictation includes that prefix in the recording; locking the screen, sleeping, disconnecting the session, or using Remote Desktop releases the idle microphone and clears the buffer.
- **License keys, the Premium account session and plugin API keys** are stored encrypted with Windows Data Protection for your Windows user account.

Settings > Advanced > Support diagnostics can export a local JSON support report.
It includes app and system details, microphone names and access status, model and
plugin state, workflow metadata, selected behavior settings, data counts, and
retained diagnostic log entries. Microphone names appear as shown in Windows and
may include the owner's name, for example for Bluetooth devices.
The report excludes API keys, audio, transcripts, workflow names and prompts,
app and website bindings, and file paths. Collection failures contain only the
affected section, exception type, and HRESULT.
Ambiguous identifiers containing directory separators or Windows drive prefixes
are omitted, including model IDs that use slash-separated namespaces.

The support report remains available when diagnostic logging is off; in that
case it contains no log entries. The separate JSON Lines log export remains
available while logging is on. Neither export is sent automatically.
