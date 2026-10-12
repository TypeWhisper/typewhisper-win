# GitHub Copilot portable plugin

Implements `ILlmProviderPlugin` for cleanup, translation and rewriting in WinUI workflows using the official `GitHub.Copilot.SDK` 1.0.13. Requires host 1.1.2 or newer.

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

The plugin aborts the turn and attempts to delete its session on success, failure or cancellation, then stops the owned runtime. Account resolution and the model list are reused in memory per account for 10, 30, 60 or 120 minutes, as chosen in the GitHub Copilot plugin settings (10 minutes by default). The duration is shared by all Copilot profiles. Every turn still binds and verifies the account on its session and switches to the model with `requireAvailable`. Any failed turn, account discovery, model refresh, saved settings change or deactivation discards that cache. Session deletion is best effort if the runtime has crashed or its connection is broken. SDK error text is neither shown nor logged because it may contain prompts or credentials. Model discovery has a 30-second deadline and text requests have a two-minute deadline, followed by bounded RPC cleanup. Workflow text is sent to GitHub Copilot under the user's account.

## Build and package

```powershell
dotnet test plugins/TypeWhisper.Plugin.GitHubCopilot/Tests/TypeWhisper.Plugin.GitHubCopilot.Portable.Tests.csproj -c Release
dotnet msbuild plugins/TypeWhisper.Plugin.GitHubCopilot/portable.proj -t:Package -p:Configuration=Release
```

The independent staged package is at `bin/Release/portable-host/Plugins/com.typewhisper.github-copilot/`; the ZIP and SHA-256 file are at `bin/Release/com.typewhisper.github-copilot-1.1.3.zip[.sha256]`.

The package contains the runtime for the build machine's architecture. Only advertise that architecture in a catalog entry; build and validate other architectures separately. Do not enable `CopilotSkipCliDownload` for distribution builds. The package intentionally excludes the host-owned `TypeWhisper.PluginSDK.dll`. Keep the runtime and all applicable licenses together.

## Validation

- Provider fake tests cover connection state, model discovery, exact prompt/text forwarding, profile persistence, stale/invalid model drafts, failed writes, missing models, sanitized failures, empty output, cancellation and deactivation.
- LSP-framed loopback tests exercise the real SDK's requests and responses, including tool restrictions, signed-out/token-auth rejection, cancellation, remote abort and session deletion. No paid requests or real credentials are used.
- Package tests use the real immutable plugin store and collectible loader for install, host LLM/configuration discovery, restart, uninstall/reinstall, dependency resolution and host-version rejection. Update staging is tested against a deliberately lower-version metadata fixture that is never executed; the actual new package is loaded after restart.
- A smoke test starts the actual packaged runtime with an isolated signed-out profile and verifies its authentication status without a model request.
- Portable test discovery includes this project automatically.

Before publication, complete native workflow execution, two distinct signed-in account checks, package updates and installed coexistence checks in [portable plugin development](../../docs/PLUGIN-MIGRATION-1.1.md). Normal automated tests remain account-free. Package-provider checks do not replace microphone capture or output insertion acceptance.

For a native development host, use the required launcher with the current checkout:

```powershell
F:\typewhisper\typewhisper-dev-tools\build-typewhisper-windows-dev.ps1 --run <current-checkout>
```
