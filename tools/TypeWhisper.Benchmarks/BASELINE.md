# Preliminary legacy API baseline — 2026-09-05

The owner deferred the full comparison until the end of the migration. Do not run more benchmarks automatically or publish a performance claim from this initial probe. Add the controlled comparison and limitations to PR #446 at the final validation stage.

One already-started API-only probe completed against the owner's running legacy TypeWhisper 1.0.9.0, localhost port 8978. Status reported sherpa-onnx, Parakeet TDT 0.6B, CPU. System: 16 logical processors. Voice: Microsoft David Desktop. Synthetic 16-kHz mono PCM; the same short sentence repeated 1, 3 and 6 times. Each size had one warm-up plus three measured runs, sequentially.

| Audio duration | Median API wall time | Min–max |
| --- | --- | --- |
| 4.695 s | 180.24 ms | 158.01–189.79 ms |
| 14.085 s | 495.36 ms | 451.55–497.41 ms |
| 28.170 s | 873.35 ms | 871.91–1002.55 ms |

Fixture PCM SHA-256, in the same order:

- `0B76B8D7FD0C8ED8B5CE7A8CB2704A33A0EA40437A3DCCC9046841506FB5FA49`
- `125B36CB6F0280B875E8E678D21FBE0F41FB2C78FCCC8EDED613984B1369ED08`
- `81D0388EBE29686D076BABD670FAAA7BD03F116B91585B8FA27590FBD36A0CC6`

The short transcript was “Bananas are yellow. This is a test of immediate recording.” Longer transcripts contained repeated copies of that sentence. No formal WER evaluation was performed. No personal recordings, microphone, clipboard or history endpoints were accessed; generated temporary WAV files were removed by the probe.

This is **not** a cold-start measurement, benchmark of the new app, or evidence of a speedup. Model-file hashes, actual loaded thread configuration and runtime versions were not captured; concurrent resident app models and workload were not controlled. Repeat under matched conditions for the final report. Three measured samples do not support a meaningful P95 claim.
