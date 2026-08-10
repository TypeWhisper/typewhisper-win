# Parakeet Realtime EOU on Windows

Status: blocked for production implementation as of 2026-08-09.

This note records the Windows evaluation for
[`nvidia/parakeet_realtime_eou_120m-v1`](https://huggingface.co/nvidia/parakeet_realtime_eou_120m-v1).
It is a research result, not a release commitment.

## Decision

Do not add Parakeet Realtime EOU to the Windows plugin catalog yet.

The model is technically interesting for low-latency commands and short utterances, but there is no supported, reproducible Windows runtime path for it today. The closest fit is sherpa-onnx because TypeWhisper already ships its C# Windows runtime. Exact support for this model remains open upstream, and the current experimental conversion requires decoder changes that have not shipped in an upstream release.

Revisit this decision when sherpa-onnx publishes and validates exact model support, including its stateful decoder and EOU behavior, in a Windows-capable release.

## Official model constraints

The NVIDIA model card describes the model as:

- a 120 million parameter cache-aware streaming FastConformer RNNT;
- English-only;
- operating on mono 16 kHz audio with chunks of at least 160 ms;
- emitting an optional `<EOU>` token;
- producing no punctuation or capitalization;
- integrated through NeMo 2.5.3 or newer;
- accelerated with CUDA; and
- officially targeted at Linux on NVIDIA hardware.

The published artifact is a 460 MB `.nemo` model. NVIDIA does not publish a supported Windows ONNX, DirectML, or CPU package for this exact model.

## Runtime evaluation

| Path | Windows fit | Current assessment |
| --- | --- | --- |
| NVIDIA NeMo and PyTorch | Poor | Official reference path, but Linux and CUDA focused. Shipping Python, PyTorch, NeMo, and CUDA for one 120M model would add an unreasonable installer and maintenance burden. |
| Generic NeMo ONNX export | Unproven | NeMo documents ONNX export for compatible models, but the exact EOU model needs cache-aware encoder state, a stateful LSTM prediction network, repeated RNNT symbol decoding, and EOU semantics. A generic export claim is not proof that these work together. |
| sherpa-onnx | Best future fit, currently blocked | TypeWhisper already uses its Windows C# runtime. Upstream issue [k2-fsa/sherpa-onnx#2805](https://github.com/k2-fsa/sherpa-onnx/issues/2805) remains open for this exact model. |
| Community ONNX conversions | Experimental only | The upstream discussion reports conversions that require metadata workarounds and an online decoder patch. Initial inference had quality and eventual-output failures before the patch. This is not a stable artifact or runtime contract for TypeWhisper. |
| TensorRT or DirectML | No verified path | There is no exact, supported model package and decoding implementation to validate on these Windows backends. |

sherpa-onnx supports Windows and C# generally, but its published Parakeet paths are not equivalent to this model. Existing Parakeet TDT models use offline or simulated-streaming recognition and do not provide the EOU model's stateful streaming contract.

## TypeWhisper streaming contract

The current `IStreamingSession` contract can publish partial or final text through `StreamingTranscriptEvent`. It cannot distinguish a normal final transcript segment from a provider-detected end of utterance, and it cannot request that the host stop or finalize capture.

Mapping `<EOU>` directly to `IsFinal` would lose that distinction. Passing the raw token through would risk inserting `<EOU>` into user text.

Before a provider is implemented, add an additive SDK signal that preserves the existing two-argument `StreamingTranscriptEvent` constructor for binary compatibility. A suitable direction is an `IsEndOfUtterance` init property with a default of `false`. The host should then decide whether an EOU event finalizes only the transcript segment or also stops capture. That policy must remain opt-in per engine.

The provider must always remove `<EOU>` before raising transcript events. The token is control data, never user text.

## Proposed implementation after the blocker clears

1. Pin a sherpa-onnx release that explicitly supports the exact NVIDIA model on Windows x64.
2. Pin every model file by source revision, filename, byte size, and SHA-256.
3. Add a streaming engine path to the sherpa-onnx plugin rather than treating the model as an offline Parakeet variant.
4. Feed mono 16 kHz PCM in the chunk size required by the validated runtime.
5. Convert the model's EOU token into the additive SDK signal and strip it from all text.
6. Mark the model as experimental, English-only, and without native punctuation or capitalization.
7. Keep the existing Parakeet TDT and Canary models unchanged.
8. Decide separately whether EOU should stop push-to-talk capture, finalize only a live segment, or be used only by an explicit hands-free mode.

## Required tests

### Unit tests

- Strip a complete `<EOU>` token from final output.
- Strip a token split across streamed chunks.
- Preserve normal text around the token without adding or removing words.
- Treat repeated EOU signals idempotently.
- Never expose `<EOU>` through partial text, final text, history, clipboard, or insertion events.
- Keep existing two-argument `StreamingTranscriptEvent` construction and providers compatible.

### Streaming integration tests

- Partial text followed by final text without EOU behaves exactly as today.
- Partial text followed by EOU emits one end-of-utterance signal and one clean final segment.
- EOU with empty text does not create an empty transcription record.
- Audio after an EOU signal follows the selected host policy instead of being silently discarded.
- Cancellation, device loss, and manual stop win over late model events.
- Existing Deepgram, ElevenLabs, OpenAI, Reson8, Smallest AI, and xAI streaming sessions do not trigger EOU behavior.

### Model and hardware tests

- Short single-word commands, short phrases, normal dictation, long speech, and silence.
- Background noise, keyboard noise, different microphones, accents, and false EOU cases.
- Measured partial latency, EOU latency, real-time factor, peak memory, and memory after unload.
- At least three repeated load, stream, stop, and unload cycles without crashes or model-sized memory growth.
- Windows x64 CPU behavior if upstream supports it, plus NVIDIA CUDA on supported hardware.
- Accuracy comparison against the currently shipped Parakeet model before recommending it for dictation.

## Re-entry gate

Implementation can start when all of the following are available:

- an upstream sherpa-onnx release with exact model support;
- a reproducible exporter or official ONNX artifacts with pinned hashes;
- a confirmed Windows x64 C# sample that produces stable partial text and EOU events;
- acceptable short-command and dictation quality on physical hardware; and
- an approved TypeWhisper EOU host policy and additive SDK contract.

Until then, the existing Parakeet TDT and Canary paths remain the supported sherpa-onnx options on Windows.
