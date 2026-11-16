using System.Runtime.InteropServices;

namespace TypeWhisper.Plugin.Diarization.Nemotron;

/// <summary>A hardware graphics adapter DirectML can run on.</summary>
/// <param name="Index">The IDXGIFactory1::EnumAdapters1 index, which ONNX Runtime's DirectML provider takes as device ID.</param>
/// <param name="Name">The adapter's description.</param>
/// <param name="DedicatedMegabytes">Dedicated video memory.</param>
internal sealed record DirectMlAdapter(int Index, string Name, ulong DedicatedMegabytes);

/// <summary>Lists DXGI adapters in IDXGIFactory1::EnumAdapters1 order, without software adapters.</summary>
internal static unsafe class DxgiAdapters
{
    private const uint SoftwareAdapter = 2;
    private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    /// <summary>The hardware adapters, or none when DXGI is unavailable.</summary>
    internal static IReadOnlyList<DirectMlAdapter> List()
    {
        var result = new List<DirectMlAdapter>();
        if (!OperatingSystem.IsWindows()) return result;
        IntPtr factory;
        try { if (CreateDXGIFactory1(FactoryId, out factory) < 0) return result; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return result; }
        try
        {
            var factoryVtbl = *(IntPtr**)factory;
            // IDXGIFactory1::EnumAdapters1 is vtable slot 12.
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)factoryVtbl[12];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) < 0) break;
                try
                {
                    var adapterVtbl = *(IntPtr**)adapter;
                    // IDXGIAdapter1::GetDesc1 is vtable slot 10.
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)adapterVtbl[10];
                    var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<AdapterDesc1>());
                    try
                    {
                        if (getDesc1(adapter, buffer) < 0) continue;
                        var desc = Marshal.PtrToStructure<AdapterDesc1>(buffer);
                        if ((desc.Flags & SoftwareAdapter) == 0)
                            result.Add(new((int)i, desc.Description.Trim(), (ulong)desc.DedicatedVideoMemory / (1024 * 1024)));
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }

        return result;
    }
}
