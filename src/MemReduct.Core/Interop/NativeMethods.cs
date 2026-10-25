using System.Runtime.InteropServices;

namespace MemReduct.Core.Interop;

/// <summary>Raw P/Invoke surface. Every entry point is documented in the Win32 / ntdll ABI.</summary>
internal static class NativeMethods
{
    // ---------------------------------------------------------------- memory info

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

    // ---------------------------------------------------------------- kernel commands

    /// <summary>
    /// Undocumented ntdll entry point used by RAMMap / Mem Reduct to drive the kernel's
    /// page-list and cache managers. Returns an NTSTATUS (0 == STATUS_SUCCESS).
    /// </summary>
    [DllImport("ntdll.dll")]
    internal static extern int NtSetSystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength);

    // ---------------------------------------------------------------- process handles

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    // ---------------------------------------------------------------- working set

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize, uint flags);

    // ---------------------------------------------------------------- privileges

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        IntPtr newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);
}
