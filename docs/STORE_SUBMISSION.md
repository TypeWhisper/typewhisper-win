# Microsoft Store Submission

TypeWhisper's Microsoft Store package is an MSIX build that uses the product identity reserved in Partner Center.

## Partner Center Identity

- Package/Identity/Name: `TypeWhisper.TypeWhisper`
- Package/Identity/Publisher: `CN=C90DFED3-0D3C-493E-8620-903C9B1A1D75`
- Package/Properties/PublisherDisplayName: `TypeWhisper`
- Package Family Name: `TypeWhisper.TypeWhisper_51tqb5623pxja`
- Store ID: `9PF42ZCR0JR0`
- Store protocol link: `ms-windows-store://pdp/?productid=9PF42ZCR0JR0`

## Build

Run the packaging workflow manually from GitHub Actions:

Open `Actions` -> `Store Package` -> `Run workflow`, enter the package version, and download the generated MSIX artifacts after the workflow completes.

The workflow builds and uploads the x64 and Arm64 MSIX artifacts only. Upload the artifacts manually in Partner Center for the current Store hotfix. Full Store submission automation can be added later with the Microsoft Store Developer CLI or Store submission API after Partner Center credentials are configured as GitHub secrets.

For a local package smoke test on a machine with the Windows SDK installed:

```powershell
.\eng\Build-StorePackage.ps1 -Version 1.0.1.1 -RuntimeIdentifier win-x64
.\eng\Build-StorePackage.ps1 -Version 1.0.1.1 -RuntimeIdentifier win-arm64
```

The script builds the app with `TypeWhisperStoreBuild=true`, which disables the Velopack runtime path and uses Microsoft Store metadata.

Packages created by this script are intended for Partner Center upload. Microsoft re-signs MSIX/AppX packages after Store certification, so a CA-trusted certificate is not required for Store submission. Local installation still requires signing or an explicit unsigned-package install flow.

## Store Build Rules

- App updates are owned by Microsoft Store, not Velopack.
- Store MSIX packages include the host app only; plugins are installed through the curated plugin channel.
- First-run plugin auto-install is disabled.
- Store plugin packages must include a `sha256` registry value before they can be installed.
- The package declares microphone, internet client, and full-trust desktop capabilities.

## Submission Notes

Certification notes should explain that TypeWhisper is a full-trust desktop speech-to-text app, uses the microphone, stores settings/history locally, optionally connects to user-configured cloud providers, and supports curated TypeWhisper plugins. Mention that remote plugin packages are hash-verified before installation in the Store build.
