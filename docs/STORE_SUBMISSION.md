# Microsoft Store Submission

TypeWhisper 1.1 supports two Store products and three submission destinations. A successfully published direct-download Daily can also trigger updates to both Store betas. Microsoft Store owns updates for all Store builds; Velopack is disabled.

| Workflow destination | Product and audience | Installed app |
| --- | --- | --- |
| `internal-flight` | TypeWhisper, `Internal Beta` flight for `TypeWhisper Internal Testers` | Updates the existing Store installation |
| `public-beta` | TypeWhisper Beta, public audience with direct-link-only discovery | Separate beta installation and profile |
| `stable` | TypeWhisper, regular public submission | Updates the existing Store installation |

Both beta routes can run together. A normal Store link does not enroll someone in the internal flight: add their Microsoft account to its group. Anyone with the public beta link can install it after certification and publication.

## Partner Center setup

Initial beta submissions on October 5, 2026:

- TypeWhisper has live x64 and ARM64 packages at `1.0.9.0` (Submission 8).
- `Internal Beta` is assigned to `TypeWhisper Internal Testers`. Manage members in Partner Center; do not commit tester email addresses.
- TypeWhisper Beta is a separate MSIX product with public audience, direct-link-only discovery, and a free Store download.
- Both initial betas were submitted for certification at `1.1.1.0`, built from `main` revision `9ceaf011c73f1a285e6c88599478e732c8630ef8`. Check Partner Center for current certification and publication status.
- The public beta listing, properties, and IARC questionnaire are complete. The rating considers possible AI-generated text (Germany: USK 12). Installed-MSIX and native ARM64 acceptance are separate from the completed package checks.

| Identity | TypeWhisper / internal flight | Public beta |
| --- | --- | --- |
| Package name | `TypeWhisper.TypeWhisper` | `TypeWhisper.TypeWhisperBeta` |
| Package family | `TypeWhisper.TypeWhisper_51tqb5623pxja` | `TypeWhisper.TypeWhisperBeta_51tqb5623pxja` |
| Store ID | `9PF42ZCR0JR0` | `9N183R19SQQ8` |
| Display name | TypeWhisper | TypeWhisper Beta |

Both use publisher `CN=C90DFED3-0D3C-493E-8620-903C9B1A1D75` and publisher display name `TypeWhisper`. [StoreProducts.json](../src/TypeWhisper.Windows.StorePackage/StoreProducts.json) supplies the same identity data to manifest generation and the app's Store metadata.

- [TypeWhisper in Partner Center](https://partner.microsoft.com/en-us/dashboard/products/9PF42ZCR0JR0/overview)
- [TypeWhisper Beta in Partner Center](https://partner.microsoft.com/en-us/dashboard/products/9N183R19SQQ8/overview)
- Beta download link after publication: <https://apps.microsoft.com/detail/9N183R19SQQ8>
- Beta protocol link: `ms-windows-store://pdp/?productid=9N183R19SQQ8`

## Build packages

Open GitHub Actions -> **Store** -> **Run workflow**. Choose the source branch/tag, destination, and a new numeric version. Build both beta products from the same revision and verify their `sourceRevision` receipts match.

Each x64/ARM64 artifact includes its MSIX and a `package-info-<rid>.json` receipt with the source commit, destination, Store identity, and SHA-256. The workflow does not upload to Partner Center, enroll testers, or publish.

## Automatic beta updates after Daily

The **Store Beta Daily** workflow runs after **Candidate** completes on `main`. It verifies that the upstream run belongs to this repository, used the scheduled or manual trigger, completed successfully, and actually published its Daily release. Validation-only manual runs and PR runs cannot publish Store updates.

The automation then:

1. Reads the two Store products and all flights on TypeWhisper. An existing submission or draft makes the run wait without changing it; a failed submission requires review. The first submissions must finish publishing before automatic updates can start.
2. Compares the Daily source with each beta's last published source. Unchanged destinations are skipped, and an older or diverged source cannot replace a newer beta.
3. Allocates a version on the `1.1` release line above the packages currently visible in the regular submission, all flights, and public beta. The fourth component remains zero. Exhausting the numeric build component requires deliberately updating the release line.
4. Calls **Store** to build x64 and ARM64 packages for the selected destinations at the exact Daily commit. The receipts record the checked-out source, not the newer workflow commit.
5. Verifies receipts, hashes, manifest identities, architectures and required payloads, then rechecks Store state before writing. It creates new submissions, retains listing/rating/availability data, replaces the packages, and requests publication after certification.

The workflow uses one concurrency group and never cancels an in-progress upload to make room for the next Daily. It never deletes existing drafts, submits a regular stable update, or modifies tester groups. There is no guaranteed daily publication time: every update still goes through Microsoft certification and Store delivery.

### One-time API setup

Associate a Microsoft Entra application with the Partner Center account and grant the Store submission permissions described in Microsoft's [API prerequisites](https://learn.microsoft.com/en-us/windows/uwp/monetize/create-and-manage-submissions-using-windows-store-services). Use a dedicated application for this automation.

Create the GitHub environment **`store-beta`**, restricted to deployments from `main`, with these environment secrets:

- `PARTNER_CENTER_TENANT_ID`
- `PARTNER_CENTER_CLIENT_ID`
- `PARTNER_CENTER_CLIENT_SECRET`

Store the client secret directly as a GitHub secret. Do not put its value in source files, issues, logs, or workflow inputs. Track its expiration and rotate it before it expires. The submission API does not need a Seller ID.

Set repository variable **`STORE_BETA_AUTOPUBLISH_ENABLED=true`** after the access check. Setting it to `false` disables future automatic runs; it does not cancel an already submitted certification.

For a read-only check, run **Store Beta Daily** on `main`, supply the numeric ID of a successful published Daily run, and leave **dry_run** enabled. Inspect `store-beta-plan` and the job summary. With **dry_run** disabled, the same command builds and submits eligible updates. No arbitrary source branch or stable destination is accepted by this workflow.

### Receipts and recovery

Each run retains a plan, the package artifacts, and `store-beta-submission-receipt` when a submission job starts. The receipt identifies every newly created submission and its last observed status, including partial failures. It contains no access tokens or upload SAS URLs. Certification notes retain the source SHA and Daily run URL for future duplicate and rollback checks.

If a build fails, nothing is submitted. If Store state changes during a build, the submission job stops before creating a draft. If upload or commit fails after draft creation, inspect the receipt and Partner Center; the draft remains available for diagnosis. Do not automatically delete it or repeatedly retry a commit whose outcome is unknown.

For submissions created through the API, make further edits through the API. Microsoft warns that mixing dashboard edits into an API-created submission can prevent subsequent API commits. Resolve a failed or abandoned submission deliberately before rerunning. Once a partially successful destination has published, a later run skips that source there and can update the other destination.

Validate the automation without credentials or an app build:

```powershell
python -m unittest discover -s eng/tests -p test_store_beta_release.py -v
```

| Destination | Packaging argument | Artifact directory |
| --- | --- | --- |
| `internal-flight` / `stable` | `-StoreProduct stable` | `artifacts/store/stable/packages/` |
| `public-beta` | `-StoreProduct beta` | `artifacts/store/beta/packages/` |

The script sets `TypeWhisperStoreBuild=true`; public beta also sets `TypeWhisperStoreProduct=beta`. Both executables remain `TypeWhisper.exe`. Microsoft signs Store packages after certification; local MSIX installation needs suitable signing or an explicitly authorized unsigned-install flow.

For local development builds and launches, use the workspace development launcher with the current checkout/worktree:

```powershell
& F:\typewhisper\typewhisper-dev-tools\build-typewhisper-windows-dev.ps1 --run <current-checkout-or-worktree>
```

Use the Store workflow for submission artifacts. A successful packaging run does not establish certification, installed activation, or native ARM64 execution.

## Version numbers

Use `Major.Minor.Build.0`, for example `1.1.1.0`. Major must be 1-65535, the middle components 0-65535, and the last component zero (reserved for Microsoft Store). Incomplete versions, extra components, and suffixes such as `-beta.1`, `-rc.1`, or `+build.1` are rejected before building.

Increment the numeric build component for every changed package within a product. Check the regular submission and all flights in Partner Center first; local validation cannot detect already-uploaded versions. For example:

| Stage | Version |
| --- | --- |
| First beta | `1.1.1.0` |
| Updated beta | `1.1.2.0` |
| Release candidate | `1.1.3.0` |
| Stable using the unchanged internal-flight package | `1.1.3.0` |

Beta/RC status belongs to the submission, not a version suffix. The app displays the numeric build version. Do not reset the final package to `1.1.0.0` after distributing higher beta versions.

## Submit and promote

For the internal flight, open **Internal Beta** in TypeWhisper, upload both `internal-flight` architecture packages after acceptance, review Flight options, and submit for certification. Testers install/update the normal TypeWhisper product while signed into a listed Microsoft account. Membership changes may take up to 30 minutes.

For stable, create a regular TypeWhisper submission and copy the accepted packages from **Internal Beta**. Keep the flight on the accepted build or update it deliberately; its members continue to receive flight packages. Removing a tester or flight does not automatically downgrade an installed package: updates need an eligible higher version.

For public beta, use TypeWhisper Beta with **Public audience -> Make this product available but not discoverable -> Direct link only**. The link can be forwarded. The Store download is free; normal in-app licensing still applies.

Public-beta packages cannot be promoted into TypeWhisper because their identities differ. Build the accepted source for `internal-flight`, validate that package, then promote it within TypeWhisper.

## Public beta isolation and acceptance

Public beta uses `TypeWhisper-WinUI-StoreBeta` instead of `TypeWhisper-WinUI` for its profile, including sign-ins and plugin data. It has its own instance key, import scratch files, and CLI destination (`TypeWhisper-Beta/1.1/Cli`). It neither automatically imports the old 1.0 profile nor deletes stable's legacy data on reset. Windows and tray labels identify TypeWhisper Beta. Beta changes are not automatically merged into stable.

Both Store products declare the existing `typewhisper:` callback protocol. Store builds do not write an unpackaged protocol registration. If Windows asks which app should open a sign-in callback, choose the app that started sign-in; the other rejects unmatched pending state. Test this with both installed products. Global dictation shortcuts also need coordination when both apps run together.

Device acceptance and release checks remain necessary alongside automation:

- Verify installation, Store/Start-menu activation, microphone capture, curated plugin installation, restart, and Store-controlled updates on the supported architectures.
- Verify the internal flight's installed upgrade and data migration. Both 1.1 Store packages require Windows 11 build 26100 or later; the existing 1.0.9 packages support older Windows.
- Install public beta alongside stable. Check separate histories/settings/sign-ins, CLI installation, callback routing, and that resetting beta leaves stable data intact.
- Complete the public beta listing, screenshots, properties, and age ratings, clearly identifying it as a beta.

Store packages contain the host only. Plugins use the curated channel and require a `sha256` registry value. Certification notes should describe full-trust desktop speech-to-text, microphone use, local storage, optional user-configured cloud providers, and hash-verified curated plugins.

## Microsoft references

- [Package flights](https://learn.microsoft.com/en-us/windows/apps/publish/package-flights)
- [Known user groups](https://learn.microsoft.com/en-us/windows/apps/publish/create-known-user-groups)
- [Visibility options](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/visibility-options)
- [MSIX package and version requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/app-package-requirements?pivots=store-installer-msix)
- [Manage app submissions through the API](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions)
- [Manage flight submissions through the API](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-flight-submissions)
