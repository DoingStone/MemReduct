using System.Diagnostics;
using MemReduct.Core.Interop;

namespace MemReduct.Core.Cleaners;

/// <summary>
/// Asks the configuration manager to reconcile the registry hive cache.
/// <para>
/// A null payload to <c>SystemRegistryReconciliationInformation</c> tells the kernel to
/// write out and shrink cached hive pages — the equivalent of a registry "flush and
/// release". This is the least predictable of the areas: it shells out to the same code
/// path a hive unload uses, so it can legitimately report no work to do.
/// </para>
/// <para>Requires an elevated token.</para>
/// </summary>
public sealed class RegistryCacheCleaner : IMemoryCleaner
{
    public CleanArea Area => CleanArea.RegistryCache;

    public string DisplayName => "注册表缓存";

    public string Description => "重整并释放缓存的注册表配置单元页面（NtSetSystemInformation 类别 155）。";

    public bool RequiresElevation => true;

    public CleanOutcome Clean(CleanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();

        if (!PrivilegeEnabler.IsElevated)
        {
            stopwatch.Stop();
            return new CleanOutcome(
                Area,
                Success: false,
                ReleasedBytes: 0,
                Message: "注册表重整需要管理员权限。",
                Duration: stopwatch.Elapsed);
        }

        if (context.DryRun)
        {
            stopwatch.Stop();
            return new CleanOutcome(
                Area,
                Success: true,
                ReleasedBytes: 0,
                Message: "试运行：将请求重整注册表缓存。",
                Duration: stopwatch.Elapsed);
        }

        int ntStatus;
        try
        {
            ntStatus = NativeMethods.NtSetSystemInformation(
                SystemInformationClass.SystemRegistryReconciliationInformation,
                IntPtr.Zero,
                0);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new CleanOutcome(
                Area,
                Success: false,
                ReleasedBytes: 0,
                Message: $"重整注册表时出错：{ex.Message}",
                Duration: stopwatch.Elapsed);
        }

        stopwatch.Stop();

        var success = CleanOutcome.IsNtSuccess(ntStatus);
        return new CleanOutcome(
            Area,
            Success: success,
            ReleasedBytes: 0,
            Message: success
                ? "注册表缓存已重整。"
                : $"注册表重整被拒绝：{CleanOutcome.DescribeNtStatus(ntStatus)}。",
            NtStatus: ntStatus,
            Duration: stopwatch.Elapsed);
    }
}
