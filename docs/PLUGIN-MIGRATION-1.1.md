# Side-by-side plugin migration for 1.1

Status: migration plan and repository inventory, 2026-09-09. This document does not mark the pending plugins as ported or publish any packages.

## Preserve the old generation

- Freeze the legacy plugin source, project files, manifests, published ZIPs and catalog during v2 ports. Do not turn the remaining WPF projects into multi-target projects as part of this work.
- Add new implementations under `plugins-v2/<plugin>/`, with independent projects, manifests, package outputs and tests. Existing portable implementations can remain where they are; relocating them is not a prerequisite.
- Keep stable logical plugin IDs for workflow references, but separate catalogs and installation roots. The WinUI host already uses `plugins-v2.json` and its own immutable `PluginPackages` store; the WPF host retains its legacy feeds. Never replace an old ZIP at its existing URL.
- Reuse protocol knowledge and test fixtures. Port or snapshot necessary implementation into the new project with source attribution; do not reference the legacy provider DLL or compile its WPF settings view into WinUI.
- Build/test discovery must support the new root before the first port lands. The legacy build graph must not include v2 projects or copy their output into legacy app folders.

## Settings migration is separate from code migration

Portable host services currently report `AllowLegacyDataMigration = false`. Keep implicit provider-side migration disabled.

A future explicit importer should inspect the old profile read-only, show compatible installed plugins, and copy a whitelist of settings into the new profile. Preserve an existing new-profile value and record an idempotent per-plugin completion marker only after the entire copy succeeds. Resolve renamed model/setting identifiers through a versioned mapping. Never move or delete the source.

Credentials need an explicit supported export/decrypt-and-reencrypt path under the current Windows user, into the new secret store. Do not blindly copy encrypted files or print credentials. Unsupported credentials require re-entry. Downloaded models should initially be copied or downloaded independently; do not introduce writable shared directories where removing a model in one generation breaks the other. The current normal-development NVIDIA asset sharing is a development optimization, not the production migration contract.

## Port acceptance

1. Independent portable build, complete ZIP with manifest/version/hash, no WPF references.
2. Fake-transport tests for requests, responses, errors and cancellation; preserve each provider's protocol differences.
3. All advertised capabilities connected to an actual WinUI host consumer. In particular, a TTS or memory package is not complete merely because it loads.
4. Isolated-profile install, configure, execute, restart, update and uninstall/reinstall checks. No real paid API requests without a separate acceptance run.
5. Legacy source/catalog/package diff check against the migration baseline and independent legacy regression checks. Install both generations and verify neither changes the other's settings or files.
6. Publish only after those checks; migrate users only for available, compatible packages. Missing ports remain visible as unavailable rather than silently substituting providers.

## Order

Start with one cloud provider as a pilot (OpenAI-compatible), then port cloud transcription/LLM providers individually. Follow with text processors and actions, then native model engines and capabilities requiring additional host consumers. Shared HTTP helpers are useful only where contracts are actually identical.

The repository contains 39 top-level manifests: five existing portable builds, one CTC dependency bundled with NVIDIA, and 33 remaining standalone ports. This is a repository count, not a claim about the published catalog.

| Plugin | Status |
| --- | --- |
| AssemblyAi | Separate v2 port pending |
| AuthenticatedCli | Separate v2 port pending |
| Cerebras | Separate v2 port pending |
| Claude | Separate v2 port pending |
| CloudflareAsr | Separate v2 port pending |
| Cohere | Separate v2 port pending |
| CohereTranscribe | Separate v2 port pending |
| Deepgram | Existing portable build |
| ElevenLabs | Separate v2 port pending |
| FileMemory | Separate v2 port pending |
| FillerWords | Existing portable build |
| Fireworks | Separate v2 port pending |
| Gemini | Separate v2 port pending |
| GemmaLocal | Separate v2 port pending |
| Gladia | Separate v2 port pending |
| GoogleCloudStt | Separate v2 port pending |
| GraniteSpeech | Separate v2 port pending |
| Groq | Existing portable build |
| Linear | Separate v2 port pending |
| LiveTranscript | Separate v2 port pending |
| Meta | Separate v2 port pending |
| Obsidian | Existing portable build |
| OpenAi | Separate v2 port pending |
| OpenAiCompatible | Separate v2 port pending |
| OpenAiVectorMemory | Separate v2 port pending |
| OpenRouter | Separate v2 port pending |
| ParakeetCtc | Bundled dependency |
| Qwen3Stt | Separate v2 port pending |
| Reson8 | Separate v2 port pending |
| Script | Separate v2 port pending |
| SherpaOnnx | Existing portable build |
| SmallestAi | Separate v2 port pending |
| Soniox | Separate v2 port pending |
| Speechmatics | Separate v2 port pending |
| SupertonicTts | Separate v2 port pending |
| Voxtral | Separate v2 port pending |
| Webhook | Separate v2 port pending |
| WhisperCpp | Separate v2 port pending |
| Xai | Separate v2 port pending |
