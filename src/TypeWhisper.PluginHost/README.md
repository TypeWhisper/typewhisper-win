# Portable plugin host groundwork

This library adapts the existing Windows package format and assembly-loading approach for a portable SDK. It is not yet wired into WinUI plugin discovery or the dictation lifecycle.

- `PortablePluginPackage` inspects `manifest.json`, checks identity/minimum host version, shares the host SDK assembly and loads dependencies in a collectible context. Only call `LoadAsync` after explicit enablement of a trusted local package. In-process plugins are not sandboxed.
- The owner must drain in-flight calls before disabling/disposal. `Unload` requests collection; it does not immediately release native resources or prove a hostile plugin has stopped.
- `VocabularyPipeline` supplies dedicated PCM and readonly timing/term snapshots, preserves the original transcript on failures and rejects late cancelled results. Native decoding must drain; it cannot safely be forcibly aborted or unloaded.
- `VocabularyResultValidator` rejects foreign recording IDs, unknown terms, overlapping/out-of-range spans, split Unicode graphemes and non-finite scores before applying any proposal.

Validated with a separate test fixture assembly and 21 portable SDK/host tests. The fixture is **not a CTC implementation**. Host services, install/discovery UI, persisted enablement, model installation, acoustic inference and real audio validation remain open.

The reference checkout `F:\typewhisper\typewhisper-win` contains the existing text-based `VocabularyBoostingService`; its implementation matched this worktree before the snapshot extraction. Text similarity must not be described as acoustic CTC evidence. The existing Parakeet TDT model alone does not provide the additional CTC model used by the Mac rescorer.
