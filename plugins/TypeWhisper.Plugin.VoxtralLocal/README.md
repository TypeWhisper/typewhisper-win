# Voxtral (Local)

Portable .NET 10 package `com.typewhisper.voxtral-local`, version `1.0.0`, requiring host `1.1.6`. It runs Mistral's
[Voxtral Mini 4B Realtime](https://huggingface.co/mistralai/Voxtral-Mini-4B-Realtime-2602) offline through
[transcribe.cpp](https://github.com/handy-computer/transcribe.cpp) 0.3.1, the runtime the NVIDIA Parakeet plugin uses
for its graphics card path.

The Mac counterpart is the local MLX plugin `com.typewhisper.voxtral`. On Windows that ID belongs to the Mistral cloud
plugin, whose ID stays so its saved keys and selections survive, so this plugin has its own ID.

## Behavior

- **Model:** one model, the Q4_K_M GGUF of the transcribe.cpp project (2.8 GB), pinned to revision `7fd3d424` and its
  SHA-256. On 40 own German dictation clips it matched Q8_0 (9.6 % against 9.8 % word errors) and ran faster;
  NVIDIA Parakeet scored 12.3 to 14.2 % on the same clips.
- **Download:** the 17 MB runtime archive from the transcribe.cpp release, then the model. An interrupted model download
  resumes. Removing the model keeps the runtime, because a running worker may still hold its libraries.
- **Processing device:** Automatic and GPU place the model on the first dedicated graphics card with Vulkan, otherwise
  on integrated graphics. Automatic falls back to the processor when no graphics card can load it. CPU does not register
  Vulkan at all. The status names the device and warns on the processor and on integrated graphics.
- **Speed:** on an RTX 4060 Ti a 4 to 9 second clip takes about 0.6 s (real-time factor 0.11). On a Ryzen 7 7800X3D
  with 8 threads it takes about as long as the audio (real-time factor 1.04), which is too slow for dictation.
  The first run after installing compiles the graphics card's shaders once and takes several seconds.
- **Languages:** the 13 evaluated languages (ar, de, en, es, fr, hi, it, ja, ko, nl, pt, ru, zh). The model detects
  the language itself; the host's language choice is not passed on.
- **Transcription:** the whole recording in one pass after it ends, with transcribe.cpp's offline default delay of
  2.4 s, its most accurate setting. One call accepts about 2.9 hours.
- **Not yet:** live preview, Voxtral's streaming decoder, translation, dictionary terms.

The engine runs in the transcription worker process, so a crash in native code cannot end the app.

## Verification

```powershell
dotnet test plugins/TypeWhisper.Plugin.VoxtralLocal/Tests -c Release
```

The opt-in live test loads the real runtime and model:

```powershell
$env:VOXTRAL_LOCAL_MODEL = "C:\path\to\Voxtral-Mini-4B-Realtime-2602-Q4_K_M.gguf"
$env:VOXTRAL_LOCAL_RUNTIME = "C:\path\to\transcribe-native-0.3.1-windows-x86_64-cpu-vulkan.tar.gz"
$env:VOXTRAL_LOCAL_WAV = "C:\path\to\speech-16khz-mono.wav"
# $env:VOXTRAL_LOCAL_DEVICE = "cpu"
dotnet test plugins/TypeWhisper.Plugin.VoxtralLocal/Tests -c Release --filter TranscribesRealAudio --logger "console;verbosity=detailed"
```

Keep the model on the same volume as the temp directory: the test hard-links it into a throwaway profile.

`tools/TypeWhisper.Benchmarks` scores the same GGUF on a folder of recordings with `files <gguf> <folder> --native <runtime>`.
