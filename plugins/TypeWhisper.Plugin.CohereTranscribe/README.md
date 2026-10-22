# Cohere Transcribe (Local) portable plugin

Local CrispASR transcription with managed runtime/model downloads and download requirements.

Version `1.2.5`; plugin ID `com.typewhisper.cohere-transcribe`; minimum host `1.1.6`.

## Setup

Select the engine, review its download requirements, and download a model and runtime. Q5_0 is the recommended default. The selected model loads automatically on the first transcription, including after an app restart. Choose a processing device and save the settings together with the shared Save button.

Before each download the plugin checks that the drive has room for the files still missing plus a 256 MB reserve. Partial model downloads are kept and count toward that amount, so an interrupted download still resumes. Abandoned runtime staging folders are removed first, and the runtime archive's extracted size is checked again before unpacking. A full drive stops the download immediately with the required and available space, and is not retried.

The plugin supports local live preview through the host PCM transcription path. It transcribes growing audio snapshots while recording and produces the final result after stopping. No audio is sent to a cloud provider. The provider icon appears in the settings sidebar and provider selector.

## Platform scope

Compared with macOS CohereLocal: this package uses the Windows CrispASR runtime and Windows process isolation, not the macOS runtime assets.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.CohereTranscribe/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.CohereTranscribe/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.cohere-transcribe` inside the plugin project. Package that directory as the ZIP root.

Tests cover runtime provisioning, archive/path validation, download credentials, process contracts, package lifecycle, persisted device settings, cold model loading, and PCM conversion/cancellation.

Regression coverage also verifies the job-assignment launch gate, uncommitted model loads, persistence failures, canceled download status and unreadable checksum markers.

The CrispASR archives do not include the Visual C++ runtime that `crispasr.exe` needs. The package therefore ships `msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll` and `vcomp140.dll` under `runtimes/win-x64` and copies them next to `crispasr.exe`. This also applies to runtimes that 1.2.4 or earlier installed. Without these files the runtime does not start on PCs without the redistributable (exit code `0xC0000135`). Building the release package requires the files from a Visual Studio installation; the Windows build fails without them.

Runtime files are rehashed before reuse. Missing or damaged dependencies are repaired from the pinned archive. Preview runtime caches without the per-file manifest are downloaded again once. Automatic device selection prefers an already installed compatible runtime before provisioning another backend.

Model cache markers include the verified file timestamp. Older preview caches may show Download once so their existing weights can be verified locally; matching weights are reused without a network transfer. Changed files are marked unavailable until verified or repaired.
