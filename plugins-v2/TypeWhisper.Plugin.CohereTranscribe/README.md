# Cohere Transcribe (Local) portable plugin

Local CrispASR transcription with managed runtime/model downloads and download requirements.

Version `1.2.2`; plugin ID `com.typewhisper.cohere-transcribe`; minimum host `1.1.2`.
Independent branch: `seofood/coheretranscribe-portable`, based on `4db8f6ac`.

## Setup

Select the engine, review its download requirements, and download a model and runtime. Q5_0 is the recommended default. The selected model loads automatically on the first transcription, including after an app restart. Choose a processing device and save the settings together with the shared Save button.

The plugin supports local live preview through the host PCM transcription path. It transcribes growing audio snapshots while recording and produces the final result after stopping. No audio is sent to a cloud provider. The provider icon appears in the settings sidebar and provider selector.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.CohereTranscribe` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. Compared with macOS CohereLocal: this package uses the Windows CrispASR runtime and Windows process isolation, not the macOS runtime assets.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.CohereTranscribe/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.CohereTranscribe/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.cohere-transcribe` inside the plugin project. Package that directory as the ZIP root.

63 plugin tests pass, covering runtime provisioning, archive/path validation, download credentials, process contracts, package lifecycle, persisted device settings, cold model loading, and PCM conversion/cancellation. Native Windows acceptance downloaded the pinned Q5_0 model and CUDA runtime and correctly transcribed a synthetic English WAV. After a new process start, cold transcription completed in 1.80 seconds; warm PCM previews for two and five seconds of audio completed in 0.08 and 0.16 seconds. These timings are observations on the development machine, not performance guarantees. WinUI visual acceptance verified the provider icon, selected Q5_0 model, German language, shared Save button and enabled Live transcription switch, including persistence after restart. The owner confirmed successful live transcription in the development app during the microphone acceptance test. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.

After the WinUI-only merge, native packaged CUDA transcription passed with the job-assignment launch gate. Regression coverage also verifies uncommitted model loads, persistence failures, canceled download status and unreadable checksum markers.

Runtime files are rehashed before reuse. Missing or damaged dependencies are repaired from the pinned archive. Preview runtime caches without the per-file manifest are downloaded again once. Automatic device selection prefers an already installed compatible runtime before provisioning another backend.

Model cache markers include the verified file timestamp. Older preview caches may show Download once so their existing weights can be verified locally; matching weights are reused without a network transfer. Changed files are marked unavailable until verified or repaired.
