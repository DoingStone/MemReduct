using System.Runtime.InteropServices;

namespace MemReduct.Core.Hardware;

/// <summary>
/// P/Invoke surface for the hardware collectors. Kept separate from
/// <see cref="Interop.NativeMethods"/> because none of it is needed by cleaning — the monitoring
/// side is allowed to fail without the cleaner noticing.
/// </summary>
internal static class HardwareNative
{
    // ------------------------------------------------------------------ processors

    /// <summary>
    /// SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION, one entry per logical processor. Every field is
    /// a 100-nanosecond tick count except <c>InterruptCount</c>. The counters are monotonic since
    /// boot, so utilisation only exists as the difference between two samples.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    internal const int SystemProcessorPerformanceInformation = 8;

    /// <summary>
    /// Views <paramref name="count"/> processor entries packed in <paramref name="buffer"/> as a
    /// span, without copying them out.
    /// </summary>
    /// <remarks>
    /// The obvious loop calls <see cref="Marshal.PtrToStructure{T}(IntPtr)"/> once per processor,
    /// but that boxes every struct it returns — 64 bytes of garbage per logical processor per
    /// sample, twelve of them here, every second, for counters that are read and discarded
    /// immediately. Casting the bytes instead allocates nothing.
    /// </remarks>
    internal static unsafe ReadOnlySpan<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION> ReadProcessorEntries(
        IntPtr buffer,
        int count)
    {
        if (buffer == IntPtr.Zero || count <= 0)
        {
            return default;
        }

        var stride = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        var bytes = new ReadOnlySpan<byte>((void*)buffer, checked(stride * count));
        return MemoryMarshal.Cast<byte, SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(bytes);
    }

    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    /// <summary>
    /// PROCESSOR_POWER_INFORMATION from <c>CallNtPowerInformation(ProcessorInformation)</c>.
    /// On parts that do not expose per-core clock control all three Mhz fields carry the rated
    /// clock, which is why this is used for the base frequency only and never for the live one.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    internal const int ProcessorInformation = 11;

    [DllImport("powrprof.dll")]
    internal static extern int CallNtPowerInformation(
        int informationLevel,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize);

    /// <summary>RelationProcessorCore — one entry per physical core, each with its thread mask.</summary>
    internal const int RelationProcessorCore = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION
    {
        public UIntPtr ProcessorMask;
        public int Relationship;
        public long Reserved1;
        public long Reserved2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnedLength);

    // ------------------------------------------------------------------------ NVML

    // NVML is the only documented way to read a discrete NVIDIA adapter's temperature. It ships
    // with the driver as nvml.dll in System32, so it is loaded by name and a machine without an
    // NVIDIA driver simply fails the first call.

    internal const int NVML_TEMPERATURE_GPU = 0;

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    internal static extern int NvmlInit();

    [DllImport("nvml.dll")]
    internal static extern int nvmlShutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    internal static extern int NvmlDeviceGetCount(out uint count);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    internal static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport("nvml.dll")]
    internal static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);

    [DllImport("nvml.dll")]
    internal static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temperature);

    [DllImport("nvml.dll")]
    internal static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NVML_UTILIZATION rates);

    [DllImport("nvml.dll")]
    internal static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NVML_MEMORY memory);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NVML_UTILIZATION
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NVML_MEMORY
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    // ------------------------------------------------------------------------ DXGI

    // DXGI is the authority on which graphics adapters exist right now, what they are called,
    // how much dedicated video memory they have, and — the part nothing else offers — their
    // LUID. The LUID is the only identifier shared with the performance counters, so it is what
    // lets a counter be labelled with a real adapter name.

    [DllImport("dxgi.dll")]
    internal static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    internal static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;

        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    /// <summary>DXGI_ADAPTER_FLAG_SOFTWARE — the Basic Render Driver, not a real device.</summary>
    internal const uint DXGI_ADAPTER_FLAG_SOFTWARE = 0x2;

    // Vtable slots, counted through IUnknown. IDXGIFactory1 inherits IDXGIObject (4 entries)
    // and IDXGIFactory (5 entries) before adding EnumAdapters1.
    internal static readonly int Vtable_EnumAdapters1 = 12;

    // IDXGIAdapter1 inherits IDXGIObject (4) and IDXGIAdapter (3: EnumOutputs, GetDesc,
    // CheckInterfaceSupport) before adding GetDesc1.
    internal static readonly int Vtable_GetDesc1 = 10;

    /// <summary>IUnknown::Release, the third slot every COM object starts with.</summary>
    internal static readonly int Vtable_Release = 2;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int EnumAdapters1Delegate(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetDesc1Delegate(IntPtr self, out DXGI_ADAPTER_DESC1 description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate uint ReleaseDelegate(IntPtr self);

    // ------------------------------------------------------------- storage devices

    // STORAGE_PROPERTY_QUERY carrying StorageDeviceTemperatureProperty. The payload is
    // STORAGE_TEMPERATURE_DATA_DESCRIPTOR, which is variable length: a 12-byte header
    // (version, size, count) then 16-byte STORAGE_TEMPERATURE_INFO entries. It is parsed by
    // offset rather than through a struct precisely because the entry array makes it variable.

    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    internal const uint StorageDeviceTemperatureProperty = 52;

    /// <summary>ERROR_INVALID_FUNCTION — the drive does not implement the property.</summary>
    internal const int ERROR_INVALID_FUNCTION = 1;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        IntPtr device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}
