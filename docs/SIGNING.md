# Windows release signing

The Candidate workflow can sign the x64 and ARM64 builds through the SignPath
Foundation organization `855e873b-1597-4361-b8d9-2d871b7c7d0c`, project
`typewhisper-win`, artifact configuration `github-executables`.

The matching XML is tracked in `eng/signpath/artifact-configuration.xml`.
It signs the application and CLI executables, the portable launcher, the
Velopack updater and the final setup executable. Dependency DLLs retain their
original signatures and the shared CLI runtime hashes remain unchanged.

## Test the integration

1. Install the official [SignPath GitHub App](https://github.com/apps/signpath)
   for `TypeWhisper/typewhisper-win`. Select only this repository and review the
   app's repository and organization administration permissions before installing.
   The current connector rejects requests when the app is missing or suspended.
2. Confirm the SignPath CI user's notification address and store that user's API
   token in the repository Actions secret `SIGNPATH_API_TOKEN`.
3. Run **Candidate** manually with `signing_policy=test-signing` and both
   publication options disabled. A repository branch can be used for this test.
4. Check the SignPath request links in each packaging job's summary and the
   signature verification results for the candidate, setup, portable archive
   and update package. Both architectures must pass.
5. Ask SignPath to review the setup and issue the production certificate.

The test certificate thumbprint is pinned in `eng/signpath/run.cjs`. The test
certificate is not installed in the runner's trust store. Test signatures must
match that certificate and pass integrity checks, allowing only its expected
untrusted-root status. Test-signed packages cannot enter the release job.

## Enable production signing

After SignPath has activated `release-signing`:

1. Set repository variable `SIGNPATH_CERTIFICATE_THUMBPRINT` to the production
   certificate's 40-character SHA-1 thumbprint from SignPath. This is a public
   certificate identifier, not a private key.
2. Run Candidate on `main` with `signing_policy=release-signing`, publishing
   disabled, and inspect both architectures.
   The Foundation-provided release policy currently requires one approval by
   Marco for each signing request. Each architecture submits five batches:
   the candidate executables, each package's generated helpers and each final
   installer. Each batch waits up to 30 minutes for completion. Approve requests
   in SignPath as they arrive during this production test.
3. Before enabling unattended Daily builds, agree the production approval process
   with SignPath. The current policy needs interactive approvals and will time
   out if nobody approves the requests; do not disable those safeguards as part
   of onboarding.
4. Set repository variable `SIGNPATH_SIGNING_POLICY=release-signing` to enable
   signing for scheduled Daily releases. Explicit publication then also requires
   selecting `release-signing`.

During onboarding the variable is unset and existing scheduled releases remain
unsigned. A signing failure never falls back to unsigned output. Production
signatures must be trusted, match the configured certificate and have a timestamp.
Production signing is limited to `main`; signing is never performed for PR runs.
The workflow passes the SignPath token only to manual or scheduled signing runs.
Pull-request and unsigned runs receive an empty token input, independently of the
local action's policy checks.

## Packaging integration

Velopack must sign files at several points during packaging. Its `--signTemplate`
callback invokes `eng/signpath/sign.cjs`, which uploads each batch as a GitHub
Actions artifact and runs the official SignPath GitHub action. Both upstream
actions are pinned to commit SHAs in `.github/actions/signpath/action.yml`.
Each batch has a unique artifact name and retains the input for seven days.

The Node action wrapper keeps GitHub's artifact credentials in the packaging
process, rather than exporting them to later workflow steps. Signed files are
verified before replacing the originals. Velopack then calculates its feeds and
package hashes from the signed bytes; release SHA-256 files are generated last.

The GitHub connector verifies the originating workflow and GitHub-hosted runners.
The GitHub App also supports repository audit-log policies. Any production
approval/origin requirements remain controlled by SignPath's release signing policy.

Code signing identifies the publisher and supports SmartScreen reputation. It
does not guarantee that a newly published file immediately avoids SmartScreen.

References: [SignPath GitHub integration](https://docs.signpath.io/trusted-build-systems/github),
[Velopack signing](https://docs.velopack.io/packaging/signing),
[Microsoft SmartScreen reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).
