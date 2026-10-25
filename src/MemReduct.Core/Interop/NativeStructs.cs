using System.Runtime.InteropServices;

namespace MemReduct.Core.Interop;

/// <summary>MEMORYSTATUSEX — physical / page file / virtual memory counters.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

/// <summary>
/// PERFORMANCE_INFORMATION from <c>GetPerformanceInfo</c>.
/// Every SIZE_T field below is measured in <b>pages</b> — multiply by <see cref="PageSize"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PERFORMANCE_INFORMATION
{
    public uint cb;
    public nuint CommitTotal;
    public nuint CommitLimit;
    public nuint CommitPeak;
    public nuint PhysicalTotal;
    public nuint PhysicalAvailable;
    public nuint SystemCache;
    public nuint KernelTotal;
    public nuint KernelPaged;
    public nuint KernelNonpaged;
    public nuint PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
}

/// <summary>
/// SYSTEM_FILECACHE_INFORMATION — payload for
/// <see cref="SystemInformationClass.SystemFileCacheInformation"/>.
/// Setting both working-set bounds to <c>(SIZE_T)-1</c> leaves the configured cache
/// limits untouched while the call still trims the cache down to its minimum.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_FILECACHE_INFORMATION
{
    public nuint CurrentSize;
    public nuint PeakSize;
    public uint PageFaultCount;
    public nuint MinimumWorkingSet;
    public nuint MaximumWorkingSet;
    public nuint CurrentSizeIncludingTransitionInPages;
    public nuint PeakSizeIncludingTransitionInPages;
    public uint TransitionRePurposeCount;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID_AND_ATTRIBUTES
{
    public LUID Luid;
    public uint Attributes;
}

/// <summary>
/// TOKEN_PRIVILEGES carrying exactly one privilege. The native type is variable-length;
/// callers always pass <c>Marshal.SizeOf&lt;TOKEN_PRIVILEGES&gt;()</c> for a single entry.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_PRIVILEGES
{
    public uint PrivilegeCount;
    public LUID_AND_ATTRIBUTES Privileges;
}
