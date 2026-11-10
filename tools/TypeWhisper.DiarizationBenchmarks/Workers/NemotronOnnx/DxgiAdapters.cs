using System.Runtime.InteropServices;

namespace TypeWhisper.DiarizationBenchmarks.Workers;

/// <summary>
/// Lists DXGI adapters in IDXGIFactory1::EnumAdapters1 order, the index ONNX Runtime's DirectML
/// provider takes as device id, so the benchmark can pick the discrete GPU by name.
/// </summary>
internal static unsafe class DxgiAdapters
{
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

    public static List<(int Index, string Name, ulong DedicatedMb, bool Software)> List()
    {
        var result = new List<(int, string, ulong, bool)>();
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(FactoryId, out var factory));
        try
        {
            var factoryVtbl = *(IntPtr**)factory;
            // IDXGIFactory1::EnumAdapters1 is vtable slot 12.
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)factoryVtbl[12];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) < 0)
                {
                    break;
                }

                try
                {
                    var adapterVtbl = *(IntPtr**)adapter;
                    // IDXGIAdapter1::GetDesc1 is vtable slot 10.
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)adapterVtbl[10];
                    var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<AdapterDesc1>());
                    try
                    {
                        Marshal.ThrowExceptionForHR(getDesc1(adapter, buffer));
                        var desc = Marshal.PtrToStructure<AdapterDesc1>(buffer);
                        result.Add(((int)i, desc.Description, (ulong)desc.DedicatedVideoMemory / (1024 * 1024), (desc.Flags & 2) != 0));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        return result;
    }
}
