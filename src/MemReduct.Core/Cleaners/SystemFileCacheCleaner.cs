using System.Diagnostics;
using System.Runtime.InteropServices;
using MemReduct.Core.Interop;

namespace MemReduct.Core.Cleaners;

/// <summary>
/// Trims the system file cache working set.
/// <para>
/// Windows caches file data in a kernel working set that grows opportunistically and
/// only shrinks under pressure. Passing a zero target size to the file-cache
/// information class makes the cache manager release what it can immediately, which is
/// the same operation RAMMap exposes as "Empty System Working Set".
/// </para>
/// <para>Requires <c>SeIncreaseQuotaPrivilege</c> (elevated).</para>
/// </summary>
public sealed class SystemFileCacheCleaner : IMemoryCleaner
{
    public CleanArea Area => CleanArea.SystemFileCache;

    public string DisplayName => "系统文件缓存";

    public string Description => "收缩内核文件缓存工作集（NtSetSystemInformation 类别 21）。";

    public bool RequiresElevation => true;

    public CleanOutcome Clean(CleanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();

        var privilege = PrivilegeEnabler.Enable(PrivilegeEnabler.SeIncreaseQuotaPrivilege);
        if (privilege != PrivilegeState.Enabled)
        {
            stopwatch.Stop();
            return new CleanOutcome(
                Area,
                Success: false,
                ReleasedBytes: 0,
                Message: $"SeIncreaseQuotaPrivilege 不可用（{privilege}）；请以管理员身份运行。",
                Duration: stopwatch.Elapsed);
        }

        if (context.DryRun)
        {
            stopwatch.Stop();
            return new CleanOutcome(
                Area,
                Success: true,
                ReleasedBytes: 0,
                Message: "试运行：将请求收缩系统文件缓存。",
                Duration: stopwatch.Elapsed);
        }

        // MinimumWorkingSet/MaximumWorkingSet = (SIZE_T)-1 keeps the configured cache
        // limits; CurrentSize = 0 is the requested target, i.e. "release everything you can".
        var info = new SYSTEM_FILECACHE_INFORMATION
        {
            CurrentSize = 0,
            PeakSize = 0,
            MinimumWorkingSet = unchecked((nuint)(-1)),
            MaximumWorkingSet = unchecked((nuint)(-1)),
        };

        var size = Marshal.SizeOf<SYSTEM_FILECACHE_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        int ntStatus;
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            ntStatus = NativeMethods.NtSetSystemInformation(
                SystemInformationClass.SystemFileCacheInformation,
                buffer,
                size);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new CleanOutcome(
                Area,
                Success: false,
                ReleasedBytes: 0,
                Message: $"收缩系统文件缓存时出错：{ex.Message}",
                Duration: stopwatch.Elapsed);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        stopwatch.Stop();

        var success = CleanOutcome.IsNtSuccess(ntStatus);
        return new CleanOutcome(
            Area,
            Success: success,
            ReleasedBytes: 0, // measured by the orchestrator from the page lists
            Message: success
                ? "系统文件缓存已收缩。"
                : $"系统文件缓存收缩被拒绝：{CleanOutcome.DescribeNtStatus(ntStatus)}。",
            NtStatus: ntStatus,
            Duration: stopwatch.Elapsed);
    }
}
