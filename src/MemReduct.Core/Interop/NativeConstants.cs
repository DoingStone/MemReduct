namespace MemReduct.Core.Interop;

/// <summary>
/// SYSTEM_INFORMATION_CLASS values consumed by <c>NtSetSystemInformation</c>.
/// Values are stable across Windows NT 6.x .. 11 (they are part of the undocumented
/// but frozen ntdll ABI that RAMMap / Mem Reduct / Process Hacker all rely on).
/// </summary>
internal static class SystemInformationClass
{
    /// <summary>Trim (or resize) the system file cache working set.</summary>
    internal const int SystemFileCacheInformation = 21;

    /// <summary>Issue a SYSTEM_MEMORY_LIST_COMMAND against the kernel's page lists.</summary>
    internal const int SystemMemoryListInformation = 80;

    /// <summary>Ask the configuration manager to reconcile/flush the registry hive cache.</summary>
    internal const int SystemRegistryReconciliationInformation = 155;
}

/// <summary>
/// SYSTEM_MEMORY_LIST_COMMAND — the 4-byte payload for <see cref="SystemInformationClass.SystemMemoryListInformation"/>.
/// </summary>
internal enum SystemMemoryListCommand
{
    MemoryCaptureAccessedBits = 0,
    MemoryCaptureAndResetAccessedBits = 1,
    MemoryEmptyWorkingSets = 2,
    MemoryFlushModifiedList = 3,
    MemoryPurgeStandbyList = 4,
    MemoryPurgeLowPriorityStandbyList = 5,
}

/// <summary>Process access rights used when opening a target for working-set trimming.</summary>
internal static class ProcessAccess
{
    internal const uint PROCESS_SET_QUOTA = 0x0100;
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Minimum rights documented for <c>EmptyWorkingSet</c>.</summary>
    internal const uint ForEmptyWorkingSet = PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA;
}

/// <summary>Token access rights and privilege attributes.</summary>
internal static class TokenAccess
{
    internal const uint TOKEN_QUERY = 0x0008;
    internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    internal const uint SE_PRIVILEGE_ENABLED = 0x0002;
}

/// <summary>Well-known Win32 error codes we branch on.</summary>
internal static class Win32Error
{
    internal const int ERROR_SUCCESS = 0;
    internal const int ERROR_NOT_ALL_ASSIGNED = 1300;
}
