# Plugin packages for TypeWhisper 1.1

The WinUI catalog is shown to users as **Plugins**. Its current feed URL remains
`https://typewhisper.github.io/typewhisper-win/plugins-v2.json` so existing Daily builds
continue to receive updates. The older `plugins.json` URL serves historical clients and
must not be overwritten with portable packages.
There is no community feed or fallback to the legacy catalog in the 1.1 host.
The WPF host and its legacy plugin release workflow have been removed. Previously published legacy packages and feeds are unchanged.

## Plugin-owned builds and tests

Each plugin owns its project, tests, manifest and complete output folder. The application
does not reference provider assemblies or copy provider-specific dependencies itself.
Portable build descriptors live under `plugins/*/portable.proj`.
Each descriptor builds and supplies its own folder. `eng/PortablePlugin.targets` copies that output
to the development bundle. Independently distributed plugins do not need a descriptor
in the application repository; they only need to provide the package described below.

Providers have independent `Tests/*.csproj` suites where supplied. Run one suite directly
with `dotnet test`, or use `eng/Test-WinUIHeadless.ps1`, which discovers plugin-owned
test projects alongside the SDK/host and presentation checks. Tests do not require
Computer Use, a desktop, downloaded models, or live API credentials.

The Plugins workflow builds only packages with changes under their own directory
on pull requests and pushes to `main`.
Shared SDK, host and workflow edits do not expand that build matrix. For a full
cross-plugin compatibility sweep, start Plugins manually with **Run workflow**.
Manifest validation and the separate headless test suites still run normally.

A portable package is a folder containing:

```text
manifest.json
Provider.dll
Provider.deps.json
other managed dependencies
runtimes/win-x64/native/...
runtimes/win-arm64/native/...
Localization/...
```

The assembly and entry class are declared in `manifest.json`. The host supplies the SDK
assembly. Package the folder's **contents** at the ZIP root, without an additional wrapper
directory. Do not include model downloads, API keys, user settings, or test binaries.
NVIDIA Parakeet bundles its CTC component under
`Dependencies/com.typewhisper.parakeet-ctc/`; it is not a separate installable integration.

The package manager is provider-independent. WinUI connects portable transcription,
LLM, text-processing, speech, explicit memory context and action capabilities.
NVIDIA Parakeet retains its dedicated local-model adapter. Installing a package
does not by itself implement an application UI for every possible SDK capability.

## Host-rendered settings and models

`IPluginTextSettings` supplies bounded text fields. A `PluginTextSetting` is single-line
by default; set `IsMultiline = true` for word lists or other multiline input. Credentials
belong in the plugin's credential settings rather than a text field.

Portable transcription engines that expose `SupportsModelDownload` use the SDK's
`IsModelDownloaded`, `DownloadModelAsync` and `LoadModelAsync` methods. Plugin settings
show actual asset state and reported progress, including when the provider is not yet
ready. Downloading does not select or load a model: the user chooses **Use model**
afterwards. Cancellation and shutdown wait for the plugin operation to finish.

Model commands are bound to the captured package activation and engine instance.
`IModelDownloadRequirementsProvider` prerequisites are displayed and rechecked before
download; unmet required prerequisites block the request. Generic credential/license
editors for these prerequisites remain future work. The host never assumes license
acceptance or invents a credential.

## Lifecycle

`ITypeWhisperPlugin.ActivateAsync(host)` loads resources when the provider is enabled.
The host drains requests before `DeactivateAsync()` and then calls `Dispose()` and
releases the collectible assembly context. Native DLLs can remain mapped until process
exit; an unload request is not a guarantee of immediate file deletion.

A plugin may additionally implement `IPluginInstallationLifecycle`:

```csharp
public Task OnInstallAsync(PluginInstallationContext context, CancellationToken ct)
{
    context.Progress?.Report(new("Preparing plugin resources…", 0.25));
    // Prepare plugin-owned data. context.PreviousVersion identifies an update.
    // Be idempotent and cancellation-aware. Do not load transcription models here.
    return Task.CompletedTask;
}

public Task OnUninstallAsync(PluginInstallationContext context, CancellationToken ct)
{
    context.Progress?.Report(new("Releasing plugin registrations…"));
    // Clean up plugin-owned registrations. Preserve settings, secrets and model files.
    return Task.CompletedTask;
}
```

The host renders plugin-provided status as plain text during installation and uninstallation. Optional step progress is between zero and one; omit it when the duration is unknown. Reports must never include credentials. Download, verification, extraction and unloading also have host-provided status messages.

These methods default to no-ops and are independent of activation. Install hooks run
after download, size/hash checks, safe extraction and manifest validation, before the
installation is committed. No hook runs while browsing the catalog. An install-hook
failure keeps the previous installation registered. An uninstall-hook failure leaves
the plugin installed and disabled. Hook-created external side effects cannot be rolled
back by the host, so plugins must make preparation retryable and avoid modifying data
that the currently running version still needs. Hooks run as trusted in-process code.

Updates use immutable package directories and become active at the next launch.
Uninstallation unregisters the disabled package immediately; unused binaries are
collected at startup. Settings, secrets and downloaded models remain available for
reinstallation. Removing the selected provider never silently switches to another
provider or starts sending audio to a cloud service.

The isolated WinUI development profile stores an atomic `PluginPackages/installed.json`
index. Bundled providers are imported once; an empty index is intentional and must not
reinstall removed providers on the next dev build. Later provider changes must be
installed as package updates; copying new files into the dev bundle is not an update
of an already imported installation.

## Catalog contract

The feed accepts an array of entries, or an object with a `plugins` array. Each entry
uses these field names:

- `id`, `name`, `version`, `minHostVersion`, `author`, `description`, `categories`
- `downloadUrl`: absolute HTTPS URL of the ZIP
- `sha256`: hexadecimal SHA-256 of the exact ZIP bytes
- `size`: exact ZIP size in bytes, up to 1 GiB
- `platforms`: array containing `windows`
- `supportedArchitectures`: explicit array such as `["x64", "arm64"]`

IDs must be unique and match the package manifest. The manifest version must match
the catalog version. The host rejects incompatible architecture/host versions,
WPF assemblies, incomplete downloads, checksum mismatches and unsafe ZIP paths.
A checksum establishes consistency with this HTTPS feed; the UI does not present it
as a publisher signature or a sandbox. Production publishing/attestation policy remains
a separate release decision.

The v2 feed and portable archives are published. Discover shows a retryable state
when the feed is unavailable, while installed providers remain available.
See [Plugin releases](PLUGIN-RELEASES.md) for publication and public-download verification.

## Declarative package dependencies and preview (1.1)

Declare required package IDs in `bundledDependencies` and ship each complete child folder at
`Dependencies/<id>`. The store validates identity and compatibility without knowing provider names.
Mark internal component manifests with `isInternalDependency: true`; they cannot be installed as
standalone integrations. The parent owns their distribution/removal. This declaration does not
implicitly activate dependency capabilities or call their hooks; the parent integration still owns
runtime initialization and cleanup.

`ITranscriptionEnginePlugin.MaximumAudioUploadBytes` limits encoded WAV uploads, including the
header. `SupportsLocalLivePreview` explicitly permits repeated local PCM snapshots and defaults
to false. The WinUI host also requires PCM support and local package metadata for generic preview;
`SupportsStreaming` alone does not mean that a cloud streaming transport is connected.
Groq remains file-based. Deepgram supports the complete streaming contract described below.

Existing installations are not overwritten by development builds: use a package
update to adopt changed declarations. Resolve current versions from committed
manifests and the published feed rather than an old example version. Independent
package tests run without provider credentials and exercise generic installation APIs.

## Keep the legacy catalog and packages independent

Current portable providers use `manifest.json` in both source and package output.
The old WPF targets and duplicate `manifest.portable.json` source files have been
removed. Previously published legacy manifests and packages remain immutable;
portable packages, including Deepgram, evolve independently without replacing
those historical release assets.

Publishing portable plugins requires separate ZIP assets and entries containing their actual
URLs, sizes and SHA-256 hashes. Do not replace existing release assets or edit `plugins.json`
when publishing `plugins-v2.json`. Local development installation is not publication.

## Catalog publication evidence and updates

The catalog published on 23 September 2026 contains 34 Windows x64 plugins: 29 archives in
[the Windows Daily plugin release](https://github.com/TypeWhisper/typewhisper-win/releases/tag/plugins-winui-20260923)
plus [xAI / Grok 1.3.2](https://github.com/TypeWhisper/typewhisper-win/releases/tag/plugin-xai-1.3.2-20260923),
and four retained versioned archives. The public feed, every ZIP URL, exact size, SHA-256,
and package identity were checked. xAI's automated tests passed; live inference was not
tested because the connected xAI team lacks API credits.

For the maintained Actions workflow, tag/dispatch instructions, validation and recovery,
see [Plugin releases](PLUGIN-RELEASES.md). File Memory 1.4.0 was subsequently published
with verified downloads, bringing that catalog snapshot to 35 entries. These counts
are dated evidence; read the public feed for the current inventory.

Use `eng/Build-PortablePluginCatalog.py` in a clean Windows checkout to build changed portable
projects and stage ZIP archives plus catalog JSON. For example, run
`python eng/Build-PortablePluginCatalog.py --source . --existing-feed ../current-plugins-v2.json --tag plugins-winui-YYYYMMDD --output ../staged-catalog`.
The current feed can use either supported top-level shape: a plugin array or an object
containing a `plugins` array. Keep the downloaded feed and staging output outside the
source checkout, which must have no uncommitted or untracked files. Choose a new release
tag and output directory for each update. A changed plugin must have a higher three-part
numeric version than the published entry.
The `--exclude-id` option leaves an intentionally deferred plugin out of the feed. A plugin
whose version already exists in the current feed is retained unchanged; bump its manifest
version before publishing changed binaries.

Upload the staged archives to a new prerelease with `latest=false`. Verify every public
download against its staged size and SHA-256, then update only `plugins-v2.json` on
`gh-pages`. Check the GitHub Pages build and fetch the exact WinUI feed URL afterward.
Keep previous release assets and the legacy `plugins.json` intact.

The 1.1 feed uses `categories`, an array of capability IDs. For example, Groq declares
`["transcription", "llm"]`. The staging script normalizes older singular `category`
metadata into this feed field. The legacy Windows target retains its existing manifest contract.

## Live cloud transcription

Deepgram 1.1.2 supports live PCM16 mono 16 kHz audio in the WinUI host. The host
retains its runtime lease until the session is finalized or canceled, bounds its audio
queue, and uses the complete saved capture for batch fallback after a transport failure.
A successful live session supplies the final transcript directly, without a second upload.
Live text disabled keeps the existing post-recording request path. Groq remains file-based.

`SupportsStreamingCompletion` opts into the host's complete-result contract in addition to
`SupportsStreaming`: `FinalizeAsync` must await all final segments, and an interrupted stream
must fail. Older packages without this capability remain on the post-recording path.
Deepgram uses `language=multi` for Auto in streaming, and the explicit language otherwise.
Its portable transport is separate from the unchanged legacy Windows transport.

Protocol references: [CloseStream](https://developers.deepgram.com/docs/close-stream),
[multilingual streaming](https://developers.deepgram.com/docs/language-detection).

The initial portable catalog published NVIDIA/Groq 1.1.1 and Deepgram 1.1.2. All three public
archives were installed and activated in an isolated profile through the actual portable host.
Deepgram's release is at https://github.com/TypeWhisper/typewhisper-win/releases/tag/plugins-v2-streaming-20260908.
The native Restart now action and live microphone transcription remain manual acceptance checks.
