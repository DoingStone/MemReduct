using System.Runtime.InteropServices;

namespace MemReduct.Core.Interop;

/// <summary>
/// PDH (Performance Data Helper) P/Invoke surface, used to read the <c>Memory</c> counter set.
/// <para>
/// These counters are the only supported way to see the individual page lists. That matters
/// because "available memory" as reported by <c>GlobalMemoryStatusEx</c> is
/// <i>free + standby</i>: purging the standby list moves pages between two categories that
/// both count as available, so it cannot be measured by watching available memory. The
/// standby counters show the real effect.
/// </para>
/// </summary>
internal static class Pdh
{
    internal const uint PDH_FMT_LARGE = 0x00000400;
    internal const uint PDH_FMT_DOUBLE = 0x00000200;
    internal const uint ERROR_SUCCESS = 0;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    /// <summary>
    /// Adds a counter addressed by its <b>English</b> path regardless of the installed UI
    /// language. This localised build of Windows would reject the plain English names through
    /// <c>PdhAddCounter</c>.
    /// </summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    /// <summary>
    /// Reads every instance of a counter that was added with a wildcard path. Used for the
    /// per-engine GPU counters, whose instance count depends on what is running.
    /// </summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhGetFormattedCounterArray(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        out uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(IntPtr query);

    /// <summary>PDH_MORE_DATA — the caller's buffer was too small; re-issue with the size given.</summary>
    internal const uint PDH_MORE_DATA = 0x800007D2;
}

/// <summary>
/// PDH_FMT_COUNTERVALUE_ITEM_W — one instance of a wildcard counter: a name pointer followed
/// by the same 16-byte value block as <see cref="PDH_FMT_COUNTERVALUE"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PDH_FMT_COUNTERVALUE_ITEM
{
    public IntPtr SzName;
    public uint CStatus;
    public long LargeValue;
}

/// <summary>
/// PDH_FMT_COUNTERVALUE — a status word followed by a union. Only the <c>largeValue</c> member
/// is read here (requested via <see cref="Pdh.PDH_FMT_LARGE"/>); declaring it as <c>long</c>
/// reproduces the native 8-byte union payload and the 16-byte total size on x64.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PDH_FMT_COUNTERVALUE
{
    public uint CStatus;
    public long LargeValue;
}
