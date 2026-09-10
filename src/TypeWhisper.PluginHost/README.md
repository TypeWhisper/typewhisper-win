# Portable plugin host

This library supports the 1.1 greenfield host. WinUI uses it for installed-package discovery, plugin management, the rebuilt sherpa-onnx engine and CTC vocabulary refinement. Reuse existing provider source; old binary compatibility is not required.

- `PortablePluginPackage` inspects `manifest.json`, checks identity/minimum host version, shares the host SDK assembly and loads dependencies in a collectible context. Only call `LoadAsync` after explicit enablement of a trusted local package. Optional package lifecycle hooks instantiate a verified plugin without activating it. In-process plugins are not sandboxed.
- `PortablePluginInventory` discovers packages through metadata inspection without activation. `PluginManagementController` provides the actual UI's state and action guards through injected runtime bindings and discovery, with no dependency on WinUI or a desktop session.
- The owner must drain in-flight calls before disabling/disposal. `Unload` requests collection; it does not immediately release native resources or prove a hostile plugin has stopped.
- `VocabularyPipeline` supplies dedicated PCM and readonly timing/term snapshots, preserves the original transcript on failures and rejects late cancelled results. Native decoding must drain; it cannot safely be forcibly aborted or unloaded.
- `VocabularyResultValidator` rejects foreign recording IDs, unknown terms, overlapping/out-of-range spans, split Unicode graphemes and non-finite scores before applying any proposal.

Run `eng/Test-WinUIHeadless.ps1` for the mandatory checks: 112 portable SDK/host tests, 96 Presentation tests, and the plugin-owned Groq (29) and Parakeet (25) suites at this checkpoint. The published sherpa-onnx package also passed a separate real PCM inference/token-timing test using existing local weights. Local model management and Groq configuration are connected. Remaining provider capabilities and a published v2 catalog still require integration. See `docs/WINUI-TESTING.md` for test scope and artifacts.

The reference checkout `F:\typewhisper\typewhisper-win` contains the existing text-based `VocabularyBoostingService`; its implementation matched this worktree before the snapshot extraction. Text similarity must not be described as acoustic CTC evidence. The existing Parakeet TDT model alone does not provide the additional CTC model used by the Mac rescorer.

Package storage, the single v2 feed and optional install/uninstall hooks are documented in [the 1.1 package contract](../../docs/PLUGIN-PACKAGES-1.1.md).
