using System.Runtime.InteropServices;
using EasyVocal.Core.Logging;

namespace EasyVocal.Ai.Runtime;

/// <summary>A graphics adapter as DXGI lists it (<see cref="Index"/> = DirectML's device id).</summary>
public sealed record GpuAdapter(int Index, string Name, long DedicatedMemory, bool Software, long Luid);

/// <summary>
/// Which graphics adapter DirectML uses. Its device id is the adapter's place in DXGI's list, where the first is the
/// one showing the desktop: on a laptop usually the integrated GPU. EasyVocal prefers the adapter Windows calls
/// high-performance (the discrete GPU), then any other hardware adapter by its own memory, never a software one;
/// a session that can't be made on one tries the next, then the processor (<see cref="OrtRuntime"/>).
/// </summary>
public static class GpuAdapters
{
    private static readonly Lazy<IReadOnlyList<int>> Order = new(() =>
    {
        if (!OperatingSystem.IsWindows()) return new[] { 0 };
        try
        {
            var list = Dxgi.List(out long? highPerformance);
            var order = DirectMLOrder(list, highPerformance);
            AppLog.Info("AI: graphics adapters " + string.Join("; ", list.Select(a =>
                $"{a.Index} {a.Name} ({a.DedicatedMemory / 1048576} MB{(a.Software ? ", software" : "")}{(a.Luid == highPerformance ? ", high-performance" : "")})"))
                + " → DirectML tries " + string.Join(", ", order));
            return order;
        }
        catch (Exception ex)
        {
            AppLog.Info("AI: the graphics adapters couldn't be listed (" + ex.Message + "); DirectML uses the first");
            return new[] { 0 };
        }
    });

    /// <summary>This computer's adapters (empty off Windows) and the LUID of the one Windows calls high-performance.</summary>
    public static IReadOnlyList<GpuAdapter> List(out long? highPerformanceLuid)
    {
        highPerformanceLuid = null;
        return OperatingSystem.IsWindows() ? Dxgi.List(out highPerformanceLuid) : Array.Empty<GpuAdapter>();
    }

    /// <summary>DirectML's device ids to try, best first (computed once).</summary>
    public static IReadOnlyList<int> DirectMLDevices => Order.Value;

    /// <summary>
    /// The order to try: the high-performance adapter (when it is hardware), then the other hardware adapters by
    /// dedicated memory (more first), in DXGI's order on ties. With no hardware adapter: device 0 (DirectML then
    /// refuses and the processor is used).
    /// </summary>
    public static IReadOnlyList<int> DirectMLOrder(IReadOnlyList<GpuAdapter> adapters, long? highPerformanceLuid)
    {
        var hardware = adapters.Where(a => !a.Software).ToList();
        if (hardware.Count == 0) return new[] { 0 };
        var first = hardware.FirstOrDefault(a => a.Luid == highPerformanceLuid);
        var rest = hardware.Where(a => a != first).OrderByDescending(a => a.DedicatedMemory).ThenBy(a => a.Index);
        return (first == null ? rest : rest.Prepend(first)).Select(a => a.Index).ToList();
    }

    /// <summary>DXGI through its COM interfaces (no extra package): IDXGIFactory1::EnumAdapters1 and IDXGIFactory6's preference.</summary>
    private static unsafe class Dxgi
    {
        private static readonly Guid Factory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid Factory6 = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
        private static readonly Guid Adapter1 = new("29038f61-3839-4626-91fd-086879011a05");
        private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
        private const uint FlagSoftware = 2;

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }

        private static IntPtr Method(IntPtr com, int slot) => (*(IntPtr**)com)[slot];

        private static void Release(IntPtr com)
        {
            if (com != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Method(com, 2))(com);
        }

        private static AdapterDesc1 Describe(IntPtr adapter)
        {
            // IDXGIAdapter1::GetDesc1 (IUnknown 3 + IDXGIObject 4 + IDXGIAdapter 3 → slot 10)
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<AdapterDesc1>());
            try
            {
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Method(adapter, 10))(adapter, buffer);
                Marshal.ThrowExceptionForHR(hr);
                return Marshal.PtrToStructure<AdapterDesc1>(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static List<GpuAdapter> List(out long? highPerformance)
        {
            highPerformance = null;
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(Factory1, out var factory));
            var list = new List<GpuAdapter>();
            try
            {
                // IDXGIFactory1::EnumAdapters1 (IUnknown 3 + IDXGIObject 4 + IDXGIFactory 5 → slot 12)
                var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)Method(factory, 12);
                for (uint i = 0; i < 32; i++)
                {
                    int hr = enumAdapters1(factory, i, out var adapter);
                    if (hr == DxgiErrorNotFound) break;
                    Marshal.ThrowExceptionForHR(hr);
                    try
                    {
                        var d = Describe(adapter);
                        list.Add(new GpuAdapter((int)i, d.Description.Trim(), (long)d.DedicatedVideoMemory, (d.Flags & FlagSoftware) != 0,
                            ((long)d.LuidHigh << 32) | d.LuidLow));
                    }
                    finally
                    {
                        Release(adapter);
                    }
                }
                // IDXGIFactory6::EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE) (slot 29): Windows 10 1803+
                var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, in Guid, out IntPtr, int>)Method(factory, 0);
                if (queryInterface(factory, Factory6, out var f6) >= 0)
                {
                    try
                    {
                        var byPreference = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, in Guid, out IntPtr, int>)Method(f6, 29);
                        if (byPreference(f6, 0, 1, Adapter1, out var best) >= 0)
                        {
                            try
                            {
                                var d = Describe(best);
                                highPerformance = ((long)d.LuidHigh << 32) | d.LuidLow;
                            }
                            finally
                            {
                                Release(best);
                            }
                        }
                    }
                    finally
                    {
                        Release(f6);
                    }
                }
            }
            finally
            {
                Release(factory);
            }
            return list;
        }
    }
}
