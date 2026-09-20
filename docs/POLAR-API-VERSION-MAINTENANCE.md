# Polar license API maintenance

Tracking: [issue #471](https://github.com/TypeWhisper/typewhisper-win/issues/471).

## Contract and evidence (2026-09-20)

`LicenseService` pins activation, validation, deactivation, and activation rollback
requests to `Polar-Version: 2026-04`. The header belongs to each request, not the
shared `HttpClient`. There is no retry against unversioned Current.
Both dedicated production clients (WPF and WinUI) disable automatic redirects so
license keys and activation IDs cannot be forwarded to a redirected origin.
Redirect responses fail the operation and retain stored license state.

The [versioning documentation](https://polar.sh/docs/api-reference/2026-04/versioning.md)
states that omitted headers follow Current, unsupported versions return 404, and
responses identify the contract in `Polar-Version`. Stable versions eventually
expire; this pin is an interim measure.

Public sandbox probes used an invented key and invented UUIDv4 organization and
activation IDs. No real license or account credentials were used. Observations:

| Request | HTTP | Response header | Response body |
| --- | --- | --- | --- |
| Validate with `1900-01` | 404 | No `Polar-Version` | `{"detail":"Not Found"}` |
| Validate with malformed `invalid` | 404 | No `Polar-Version` | `{"detail":"Not Found"}` |
| Validate nonexistent key with `2026-04` | 404 | `Polar-Version: 2026-04` | `{"error":"ResourceNotFound","detail":"Not found"}` |
| Deactivate nonexistent key with `2026-04` | 404 | `Polar-Version: 2026-04` | `{"error":"ResourceNotFound","detail":"Not found"}` |

This confirms that `2026-04` is accepted, but is not a successful license lifecycle
test. No TypeWhisper sandbox organization/test license is configured locally.

Polar's [license service source at the inspected revision](https://github.com/polarsource/polar/blob/7115a0d76944a4b256bada1c52f33a9727f6d3c0/server/polar/license_key/service.py)
also raises `ResourceNotFound` for a missing activation, revoked/inactive license,
or expired license. The client requires HTTP 404, the exact structured `error`
value, and the matching response version before clearing local state. A bare 404,
generic missing-resource text, legacy `type` alone, or an absent/mismatched version
preserves credentials, entitlement, and the last successful validation timestamp.
Unknown 404s expose an update/contact-support message. Existing validation
intervals (commercial: 7 days; supporter: 30 days) and offline behavior are
unchanged. Network failures do not advance validation timestamps or erase licenses.

## Local verification

```powershell
dotnet test tests/TypeWhisper.PluginSystem.Tests/TypeWhisper.PluginSystem.Tests.csproj --filter FullyQualifiedName~LicenseServiceTests
F:\typewhisper\typewhisper-dev-tools\build-typewhisper-windows-dev.ps1 --run <current-checkout-or-worktree>
F:\typewhisper\typewhisper-dev-tools\build-typewhisper-windows-dev.ps1 --run --winui <current-checkout-or-worktree>
```

The tests simulate both entitlement types, all license operations, isolated request
headers, unsupported/ambiguous responses, persistence across restart, confirmed
missing/revoked/expired licenses, inactive validation results, and offline retries.
They do not require a Polar account and do not send network requests.

Local results on 2026-09-20: all 75 `LicenseServiceTests` and all 30
`AppLocalizationResourcesTests` passed. The supported development script built
this worktree and started `F:\typewhisper\dev-output\typewhisper-win\Build\TypeWhisper.exe`;
the process was responding with its Settings window open. Existing MVVM analyzer
warnings were reported during the build. No live license activation was performed.
The WinUI build also passed using the same script with `--winui`, and the script
launched `F:\typewhisper\dev-output\typewhisper-win\WinUI\TypeWhisper.WinUI.exe`.
WinUI compiles the same `LicenseService.cs` through a linked source entry and embeds
the English license strings; `WinUILicensing` forwards the service errors to its
license notice. No separate copy of the API integration needs updating.

## Release and migration follow-up

Proposed accountable owner: Marco / TypeWhisper Windows release maintainer.
The dates below are planning targets, not scheduled jobs or completed releases.
Confirm ownership and record shipped versions in #471 before closing it.

| Target date | Work | Status |
| --- | --- | --- |
| 2026-09-28 | Ship the pin and credential-preservation fix through direct/Velopack and Microsoft Store channels, allowing for Store review before the October 1 default change reported in #471. Include supported x64/Arm64 packages and any distributed WinUI builds that share this service. | Pending release |
| 2026-11-16 | Provision a dedicated sandbox license; review `2026-10` changes and run activation → validation → deactivation for commercial and supporter entitlements. Check the returned version and verify validation of the removed activation. | Pending; sandbox not configured |
| 2026-12-01 | Ship the tested `2026-10` migration through both channels, ahead of the January 2027 retirement reported in #471. Recheck Polar's actual supported versions and retirement dates before release. | Pending migration |

For the real lifecycle check, use a sandbox-only organization/key, a unique device
label, and delete that test activation afterward. Record date, app revision, HTTP
statuses and returned versions; never log license keys or request bodies. Exercise
both an unsupported version and a missing activation, then reload the local
credential store to verify the different outcomes.

Older installations remain important: unupdated builds still send unversioned
requests and can erase saved state on generic 404s. Builds containing this pin
preserve credentials when `2026-04` is removed, but cannot complete online license
operations until updated. Publish an update notice with each migration, verify
direct updater and Store delivery, and retain a recovery path for users who skip
releases. Never silently redirect old clients to an untested Current contract.
Review this plan ahead of every quarterly Polar release and record the next owner,
migration target, sandbox evidence, and minimum fixed app versions in the tracking
issue. Do not close #471 on local tests alone.
