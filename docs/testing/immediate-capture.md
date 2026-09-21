# Recording startup regression checks

The host starts microphone capture before accessibility inspection, workflow lookup,
dictionary loading and provider setup. Streaming audio is retained until the consumer
exists, then handed over in capture order. Overflow rejects the partial streaming
path so final transcription can use the complete recording. No microphone audio is
captured before an explicit recording request.

## Automated checks

```powershell
dotnet test tests/TypeWhisper.Presentation.Tests --filter FullyQualifiedName~BufferedAudioHandoffTests
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests --filter FullyQualifiedName~BufferedStreamingStartupTests
dotnet test tests/TypeWhisper.Dictation.AudioTests --filter 'Category!=LocalParakeet&Category!=ProviderStartup'
```

These check prefix ownership, order, exactly-once delivery, overflow fallback,
recording reset, capture in hold/toggle mode, and stop while the cloud connection
is still pending.

The opt-in `ProviderStartupAudioTests` use a new local plugin instance and an actual
cloud provider. They require an existing mono 16-bit PCM WAV at 16 kHz containing:
"Morgen besprechen wir die Änderungen und planen die nächsten Schritte."
Leading silence is removed so it cannot conceal lost initial speech.

Set these environment variables before running the tests:

| Variable prefix `TYPEWHISPER_STARTUP_` | Value |
| --- | --- |
| `WAV` | Path to the test WAV described above |
| `LOCAL_PACKAGE` | Existing portable Whisper plugin package directory |
| `LOCAL_ASSETS` | Existing asset directory containing `Models` |
| `LOCAL_MODEL` | Downloaded model ID, e.g. `large-v3-turbo` |
| `CLOUD_PACKAGE` | Existing streaming provider package directory |
| `CLOUD_DATA` | Its existing development-profile settings and DPAPI secret directory |

```powershell
dotnet test tests/TypeWhisper.Dictation.AudioTests --filter Category=ProviderStartup --logger 'console;verbosity=detailed'
```

The tests use isolated settings copies. They do not download models or change the
installed provider settings. Cloud tests send only the specified test recording.

The local test captures the beginning before loading model weights into memory.
The cloud test retains 600 ms before stream creation and delays provider acquisition
another 600 ms. Both compare every captured sample and check the first word and the
end of the transcript. A second capture deliberately starts 300 ms late and reports
its transcript and missing sample count. This is a controlled simulation of delayed
startup, not a measurement of the previous host's startup latency. ASR can sometimes
reconstruct a clipped word, so sample preservation is the decisive assertion.

## Manual host checks

1. Start the development host through the canonical development build script.
2. Select a downloaded local Whisper model, restart the host, focus a text field,
   and speak immediately after the shortcut. Check the first word and final sentence.
3. Repeat with a configured streaming cloud provider, including a short utterance
   stopped before its connection is ready.
4. Repeat in hold and toggle modes. Cancel during startup, then record again; no
   audio from the canceled recording should appear in the next one.
5. Verify that a failed microphone start or a target change during preparation leaves
   recording stopped. Check ordinary paste/review behavior and recording duration.

The automated capture uses a deterministic input adapter; these manual checks cover
the real shortcut, OS microphone driver, UI dispatcher and target-field inspection.

## Development validation (September 21, 2026)

- 1,281 presentation tests, 274 portable host tests and two immediate-capture tests passed.
- Whisper Large V3 Turbo was loaded after capture began. All 70,881 samples and the
  complete German sentence were retained. Soniox passed the same live-cloud comparison.
- The delayed control lost exactly 4,800 samples (300 ms). Both models reconstructed
  the first word in this particular control, illustrating why transcript text alone
  is insufficient to detect missing audio.
- Marco confirmed immediate-speaking microphone tests with local Whisper and Soniox
  in the development app.
