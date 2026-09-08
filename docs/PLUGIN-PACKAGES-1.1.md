# Plugin packages for TypeWhisper 1.1

The only WinUI catalog is `https://typewhisper.github.io/typewhisper-win/plugins-v2.json`.
There is no community feed or fallback to the legacy catalog in the 1.1 host.
The old WPF host retains its existing feeds for maintenance releases.

## Plugin-owned builds and tests

Each plugin owns its project, tests, manifest and complete output folder. The application
does not reference provider assemblies or copy provider-specific dependencies itself.
The in-repository development build discovers `plugins/*/portable.proj` and asks each
descriptor to build and supply its folder. `eng/PortablePlugin.targets` copies that output
to the development bundle. Independently distributed plugins do not need a descriptor
in the application repository; they only need to provide the package described below.

Groq, NVIDIA Parakeet and Deepgram have independent `Tests/*.csproj` suites. Run one suite directly
with `dotnet test`, or use `eng/Test-WinUIHeadless.ps1`, which discovers plugin-owned
test projects alongside the SDK/host and presentation checks. Tests do not require
Computer Use, a desktop, downloaded models, or live API credentials.

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
LLM, text-processing and explicit action capabilities. NVIDIA Parakeet retains its
dedicated local-model adapter. Other SDK capabilities still need host consumers;
installing a package does not by itself implement every capability's application UI.

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
uses the existing Windows field names:

- `id`, `name`, `version`, `minHostVersion`, `author`, `description`, `category`
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

At implementation time the v2 endpoint returned HTTP 404. Discover shows a retryable
unavailable state, while installed providers continue working. Publishing the feed and
its actual ZIP artifacts is still required for a live remote install walkthrough.

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
Groq and Deepgram remain batch transcription providers in WinUI.

NVIDIA and Groq package versions are 1.1.0. Existing installations are not overwritten by development
builds: use a package update to adopt the new declarations. Deepgram is built but is not bootstrapped
into existing or fresh profiles and has not been published to the v2 catalog. Its own tests run without
keys, models or network requests and verify its installation through the generic package/registry APIs.

## Keep the legacy catalog and packages independent

`manifest.json` remains the legacy Windows manifest. Multi-target providers use
`manifest.portable.json` for the net10.0 package, publishing it under the expected
`manifest.json` name in the portable output. WPF targets retain their original version;
portable versions can evolve independently. Deepgram also has a separate portable
implementation; it does not replace the existing Windows provider implementation.

Publishing v2 requires separate portable ZIP assets and entries containing their actual
URLs, sizes and SHA-256 hashes. Do not replace existing release assets or edit `plugins.json`
when publishing `plugins-v2.json`. Local development installation is not publication.
