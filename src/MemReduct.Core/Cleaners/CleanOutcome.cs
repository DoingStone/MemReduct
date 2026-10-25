namespace MemReduct.Core.Cleaners;

/// <summary>What one cleaner achieved.</summary>
/// <param name="Area">The area this outcome describes.</param>
/// <param name="Success">False when the operation was refused or failed.</param>
/// <param name="ReleasedBytes">
/// Bytes released from the structure this operation targets: trimmed working set for
/// <see cref="CleanArea.WorkingSet"/>, drained standby or modified list for the page-list
/// areas, shrunk cache for the file cache. Deliberately <i>not</i> "change in available
/// memory", which cannot represent a standby purge (see <see cref="CleanMeasurement"/>).
/// </param>
/// <param name="Message">Human-readable one-line description of the result.</param>
/// <param name="AffectedProcesses">Processes successfully trimmed (working-set area only).</param>
/// <param name="FailedProcesses">Processes that could not be opened or trimmed (working-set area only).</param>
/// <param name="NtStatus">Raw NTSTATUS from the kernel, when the area used <c>NtSetSystemInformation</c>.</param>
/// <param name="Duration">Wall-clock time the operation took.</param>
/// <param name="Measurement">
/// Observed deltas around this call. Populated by the orchestrator, which is the only place
/// that can sample the system on both sides of a cleaner.
/// </param>
public sealed record CleanOutcome(
    CleanArea Area,
    bool Success,
    long ReleasedBytes,
    string Message,
    int AffectedProcesses = 0,
    int FailedProcesses = 0,
    int NtStatus = 0,
    TimeSpan Duration = default,
    CleanMeasurement? Measurement = null)
{
    /// <summary>Observed deltas, or an empty measurement when none was taken.</summary>
    public CleanMeasurement Observation => Measurement ?? CleanMeasurement.Unavailable;

    /// <summary>True when the kernel reported STATUS_SUCCESS (0). Informational codes are 0x40000000+.</summary>
    public static bool IsNtSuccess(int ntStatus) => ntStatus >= 0;

    /// <summary>Formats an NTSTATUS the way WinDbg does, e.g. <c>0xC0000061</c>.</summary>
    public static string FormatNtStatus(int ntStatus) => $"0x{unchecked((uint)ntStatus):X8}";

    /// <summary>
    /// Well-known NTSTATUS values worth naming in output. The symbolic name is kept
    /// alongside the Chinese gloss because it is the searchable Windows identifier.
    /// </summary>
    public static string DescribeNtStatus(int ntStatus) => ntStatus switch
    {
        0 => "STATUS_SUCCESS（成功）",
        unchecked((int)0xC0000061) => "STATUS_PRIVILEGE_NOT_HELD（缺少所需特权，请以管理员身份运行）",
        unchecked((int)0xC0000022) => "STATUS_ACCESS_DENIED（拒绝访问）",
        unchecked((int)0xC000000D) => "STATUS_INVALID_PARAMETER（参数无效）",
        unchecked((int)0xC0000002) => "STATUS_NOT_IMPLEMENTED（该系统未实现此操作）",
        _ => FormatNtStatus(ntStatus),
    };
}
