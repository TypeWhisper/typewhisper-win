using System.Runtime.InteropServices;

namespace TypeWhisper.Plugin.WhisperCpp;

internal sealed record GpuDevice(string Name, bool Integrated);

/// <summary>
/// Lists the GPUs of the loaded ggml runtime in the order whisper.cpp's gpu_device indexes them.
/// whisper.cpp uses device 0 by default, which can be integrated graphics next to a dedicated GPU.
/// </summary>
internal static class GpuDevices
{
    private const int GpuType = 1;
    private const int IntegratedGpuType = 2;

    // The first dedicated GPU; integrated graphics only when there is none.
    internal static int Select(IReadOnlyList<GpuDevice> devices)
    {
        for (var index = 0; index < devices.Count; index++)
            if (!devices[index].Integrated) return index;
        return 0;
    }

    // Null when the device list cannot be read; empty when the runtime reports no GPU, so whisper.cpp runs on the CPU.
    internal static IReadOnlyList<GpuDevice>? List(string runtimeDirectory)
    {
        // Whisper.net has already loaded these; the device registry and device properties live in different DLLs.
        // Our handles only add references, so they are released once the list has been read.
        var registry = IntPtr.Zero;
        var core = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad(Path.Join(runtimeDirectory, "ggml-whisper.dll"), out registry)
                || !NativeLibrary.TryLoad(Path.Join(runtimeDirectory, "ggml-base-whisper.dll"), out core)
                || !TryExport<Count>(registry, "ggml_backend_dev_count", out var count)
                || !TryExport<Get>(registry, "ggml_backend_dev_get", out var get)
                || !TryExport<DeviceType>(core, "ggml_backend_dev_type", out var type)
                || !TryExport<Text>(core, "ggml_backend_dev_description", out var description))
                return null;
            var devices = new List<GpuDevice>();
            for (nuint index = 0, total = count(); index < total; index++)
            {
                var device = get(index);
                var kind = type(device);
                if (kind is GpuType or IntegratedGpuType)
                    devices.Add(new(Marshal.PtrToStringUTF8(description(device)) ?? $"GPU {devices.Count}", kind == IntegratedGpuType));
            }
            return devices;
        }
        finally
        {
            if (core != IntPtr.Zero) NativeLibrary.Free(core);
            if (registry != IntPtr.Zero) NativeLibrary.Free(registry);
        }
    }

    private static bool TryExport<T>(IntPtr library, string name, out T function) where T : Delegate
    {
        function = null!;
        if (!NativeLibrary.TryGetExport(library, name, out var address)) return false;
        function = Marshal.GetDelegateForFunctionPointer<T>(address);
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint Count();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Get(nuint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceType(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Text(IntPtr device);
}
