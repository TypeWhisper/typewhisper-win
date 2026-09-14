# GitHub Copilot portable plugin

Implements `ILlmProviderPlugin` for cleanup, translation and rewriting in WinUI workflows using the official `GitHub.Copilot.SDK` 1.0.13. Requires host 1.1.2 or newer. This is an independent v2 plugin; it does not modify or depend on the legacy AuthenticatedCli plugin.

## Connect

1. Sign in to the official GitHub Copilot CLI with your own GitHub account. The account must have Copilot access; organization policy, model availability and plan limits still apply.
2. Enable this plugin and choose **Refresh GitHub accounts**. Select the desired signed-in account in the profile's **GitHub account** field.
3. Choose **Refresh models**, select a text model, then click **Save profile**. Name, account and model are committed together; refreshing draft values does not change workflow routing.
4. Use **+** to add another profile. Each profile appears separately in workflow provider selections with its own stable identity, name, account and model. Selecting a profile in settings only changes the editor; it does not change an existing workflow's provider.

Sign in to additional accounts through the official Copilot CLI, then refresh accounts in TypeWhisper. The plugin discovers stored OAuth users; a `gh` login or environment token alone is not an account profile. It has no API-key field, token import or shared credentials. Only account host/login and profile preferences are saved in TypeWhisper; SDK-returned tokens are ignored. **Disconnect profile** and **Remove profile** do not sign out of GitHub or affect other profiles. The original/default profile is retained for existing workflow references.

Each model catalog is fetched with the opaque account selection ID returned by the SDK's `account.getAllUsers`; this ID is resolved afresh and is never persisted. Before sending text, the plugin verifies that the bound account is still signed in, checks that account's model availability, installs its OAuth host/login into the new session, verifies the resulting session identity and selects the requested model. These are session-scoped operations; the global CLI account is not switched. Missing accounts, mismatched session identities and invalid models fail without account/model fallback. Calls are serialized and are not eligible for duplicate request hedging.

Legacy single-account preferences migrate automatically only when exactly one stored OAuth account is available. If several accounts are available, the user must choose explicitly. Once a profile is bound, a later missing account never causes rebinding to whichever account is currently active. Unsaved account/model changes require a matching draft catalog before the common save can commit them.

## Text-only runtime

The NuGet build targets acquire Copilot runtime 1.0.83 with release-checksum verification. The complete SDK-staged runtime, managed dependencies and notices are copied into the plugin package. At runtime the plugin starts that exact package-local executable over stdio, with a restricted environment and a plugin-owned working directory. It does not search PATH for an alternate Copilot runtime or download executable code during activation.

Each request creates a new session with the workflow system prompt in `replace` mode and the input text as the user message. No file attachments or active-application context are supplied. Sessions explicitly disable:

- All built-in, custom and MCP tools (empty allowlist plus source-qualified wildcard exclusions); permission and pre-tool callbacks deny requests.
- Configuration discovery, custom instructions, file hooks, Git operations, skills, custom-agent discovery, scheduling and file-change tracking.
- Memory, tool search, embeddings retrieval, the cross-session store, infinite sessions, remote sessions and session telemetry.

The SDK's `CopilotCli` mode preserves the existing keychain login. `Empty` mode changes credential storage, so its relevant restrictions are applied explicitly instead. These flags include experimental SDK APIs and must be revalidated when updating the pinned dependency. They restrict agent capabilities; they are not an OS sandbox for the trusted runtime executable.

The plugin aborts the turn and attempts to delete its session on success, failure or cancellation, then stops the owned runtime. Session deletion is best effort if the runtime has crashed or its connection is broken. SDK error text is neither shown nor logged because it may contain prompts or credentials. Model discovery has a 30-second deadline and text requests have a two-minute deadline, followed by bounded RPC cleanup. Workflow text is sent to GitHub Copilot under the user's account.

## Build and package

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.GitHubCopilot/Tests/TypeWhisper.Plugin.GitHubCopilot.Portable.Tests.csproj -c Release
dotnet msbuild plugins-v2/TypeWhisper.Plugin.GitHubCopilot/portable.proj -t:Package -p:Configuration=Release
```

The independent staged package is at `bin/Release/portable-host/Plugins/com.typewhisper.github-copilot/`; the ZIP and SHA-256 file are at `bin/Release/com.typewhisper.github-copilot-1.1.0.zip[.sha256]`.

The package contains the runtime for the build machine's architecture. Only advertise that architecture in a catalog entry; build and validate other architectures separately. Do not enable `CopilotSkipCliDownload` for distribution builds. The package intentionally excludes the host-owned `TypeWhisper.PluginSDK.dll`. Keep the runtime and all applicable licenses together. This change does not publish a catalog entry or package.

## Acceptance evidence and remaining release checks

Validated on Windows x64: 35 plugin tests passed in Release, including two-account routing, editor/workflow isolation, failed atomic saves, missing-account rejection, session identity verification, secret non-forwarding, proxy environment preservation and the packaged-runtime smoke test. Package tests retry transient cleanup failures and fail if cleanup remains unsuccessful. The independent ZIP was generated and its SHA-256 and required runtime entries verified; a planted stale staging file was excluded on rebuild. The full Windows headless suite also passed before the review corrections.

Live packaged-provider acceptance passed on 2026-09-14 using the existing user's GitHub account, with no paid-plan upgrade. The initial `gh` keyring login alone was not recognized by the runtime; signing in through the official Copilot CLI browser flow resolved this. The SDK returned only `auto`, which the plugin listed, selected and used successfully. Input: `Hello, this is a short TypeWhisper test.` Instruction: `Translate the user's text into German. Return only the translation.` Output: `Hallo, dies ist ein kurzer TypeWhisper-Test.` This exercised the real package loader, connection/settings actions, model catalog and `ILlmProviderPlugin.ProcessAsync` against GitHub; it was not a native WinUI interaction test. The model list alone does not establish the account's exact Copilot plan.

Additional live acceptance: six `auto` requests exercised German dictation cleanup, translation, email drafting, literal preservation and a two-request isolation probe. All requests completed without API errors in 3.52–8.72 seconds. Cleanup retained the leading filler “Also”; the remaining outputs met the manually reviewed criteria. Full synthetic prompts, outputs and limitations are in [the live acceptance report](../../docs/copilot-live-acceptance.md).

The account-bound implementation also passed a live translation. A second live check created two profiles for the one available real account in an isolated package store, restarted the real runtime registry, restored both provider roles and executed the secondary role after selecting the primary profile in settings. It returned `SECONDARY_PROFILE_OK`. Two different GitHub identities are covered by fake-provider and real-SDK loopback tests; a live run with two distinct signed-in accounts remains outstanding. Functional assertions passed, but the helper exited with a DLL cleanup error. Automatic approval review blocked a subsequent deletion attempt; the temporary directory remains.

Earlier native WinUI smoke checks confirmed the development build from this checkout opens with the installed plugin, renders its Copilot icon, restores `Auto`, and exposes one **Save profile** footer instead of per-field save buttons. The final multi-account settings check also displayed the GitHub account selector and add-profile button. A concurrent task was operating the same app and publishing another checkout to the shared development output, so the final check did not revalidate host branding or exercise a native save click. Model configuration was persisted through the real profile capability before app startup. The host bundles GitHub's Octicons mark with light/dark variants and its MIT license.

- Provider fake tests cover connection state, model discovery, exact prompt/text forwarding, profile persistence, stale/invalid model drafts, failed writes, missing models, sanitized failures, empty output, cancellation and deactivation.
- LSP-framed loopback tests exercise the real SDK's requests and responses, including tool restrictions, signed-out/token-auth rejection, cancellation, remote abort and session deletion. No paid requests or real credentials are used.
- Package tests use the real immutable plugin store and collectible loader for install, host LLM/configuration discovery, restart, uninstall/reinstall, dependency resolution and host-version rejection. Update staging is tested against a deliberately lower-version metadata fixture that is never executed; the actual new package is loaded after restart.
- A smoke test starts the actual packaged runtime with an isolated signed-out profile and verifies its authentication status without a model request.
- The existing portable test discovery includes this project's tests automatically. Legacy source, manifests, catalogs and build graph remain unchanged.

Before publication, complete the remaining native end-to-end workflow execution and coexistence/update checks in `docs/PLUGIN-MIGRATION-1.1.md`. The recorded live tests use the packaged provider, not microphone capture or output insertion. Further live requests require their own acceptance scope; normal tests remain account-free. A prior released Copilot plugin is not available for a real version-to-version upgrade run yet.

For a native development host, use the required launcher with the current checkout:

```powershell
F:\typewhisper\typewhisper-dev-tools\build-typewhisper-windows-dev.ps1 --winui --run <current-checkout>
```
