# Supertonic TTS portable plugin

Local Supertonic 3 speech synthesis with ONNX Runtime, 10 preset voices and 31 supported languages. Version `1.2.2`; plugin ID `com.typewhisper.supertonic-tts`; minimum host `1.1.4`.

## Settings and model setup

The host displays a local model card with a license link and an acceptance checkbox. Checking or unchecking it saves the license decision immediately, independently of the common **Save settings** button. Downloads remain blocked until the current model license is accepted.

**Download & Load** downloads the model, shows progress and a percentage, then initializes the CPU synthesis engine. Cancellation retains completed files for a retry and removes incomplete downloads. Downloaded assets show 100%; the engine can be loaded or unloaded separately. Voice, speech speed and quality are saved together. Per-request voice and playback-output selection remain supported.

The 383 MiB model bundle is pinned to Hugging Face revision `3cadd1ee6394adea1bd021217a0e650ede09a323`. Every ONNX, JSON and voice-style download is verified by length and SHA-256 before atomic promotion to its final path. Loading, downloading, unloading and synthesis are serialized. Cancelled native initialization is drained and its candidate engine disposed before releasing the operation.

The optional `ILocalTtsModelManagement` SDK contract supplies the model card without a plugin-owned UI. The host compatibility version is raised to `1.1.4` so older hosts reject this package before loading an unsupported SDK interface.

## Platform scope

The Windows backend uses ONNX Runtime for synthesis and WASAPI for playback.

The provider icon reuses the unchanged official Supertone website icon. Model weights use OpenRAIL-M; the download gate requires explicit acceptance of the linked license. Optional Hugging Face credentials remain available through the SDK requirement contract; public downloads do not require a token.

## Validation

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.SupertonicTts/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.SupertonicTts/Tests -c Release
```

Plugin tests cover license persistence, download integrity failures and cleanup, completion progress, native-load cancellation cleanup, unload/reload state, saved voice/speed/quality, and package installation, restart and removal. The package loader rejects host `1.1.3` and accepts `1.1.4`.

Validate audible playback through **Test voice**, interactive download cancellation, unload/reload and synthesis cancellation in the native host. See [settings screenshots](../../docs/screenshots/supertonic/).
