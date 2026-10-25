namespace MemReduct.Core.Cleaners;

/// <summary>
/// The independent memory-reclamation operations the tool can perform.
/// The values are flags so a set of areas can be requested in one call.
/// </summary>
[Flags]
public enum CleanArea
{
    None = 0,

    /// <summary>Trim every accessible process working set (<c>EmptyWorkingSet</c>).</summary>
    WorkingSet = 1 << 0,

    /// <summary>Trim the system file cache working set (<c>NtSetSystemInformation</c> class 21).</summary>
    SystemFileCache = 1 << 1,

    /// <summary>Purge the standby page list (class 80, command 4).</summary>
    StandbyList = 1 << 2,

    /// <summary>Flush the modified page list to disk (class 80, command 3).</summary>
    ModifiedPageList = 1 << 3,

    /// <summary>Flush modified + purge standby + purge low-priority standby, in that order (class 80, commands 3/4/5).</summary>
    CombinedMemoryList = 1 << 4,

    /// <summary>Ask the configuration manager to reconcile the registry hive cache (class 155).</summary>
    RegistryCache = 1 << 5,
}

/// <summary>Named <see cref="CleanArea"/> sets used as defaults.</summary>
public static class CleanAreas
{
    /// <summary>What "Clean memory" does out of the box.</summary>
    public const CleanArea Default =
        CleanArea.WorkingSet |
        CleanArea.SystemFileCache |
        CleanArea.StandbyList |
        CleanArea.ModifiedPageList |
        CleanArea.RegistryCache;

    /// <summary>Every area. <see cref="CleanArea.CombinedMemoryList"/> is included even though it overlaps two others.</summary>
    public const CleanArea All = Default | CleanArea.CombinedMemoryList;

    /// <summary>Areas that need an elevated token to have any effect.</summary>
    public const CleanArea RequiresElevation =
        CleanArea.SystemFileCache |
        CleanArea.StandbyList |
        CleanArea.ModifiedPageList |
        CleanArea.CombinedMemoryList |
        CleanArea.RegistryCache;

    /// <summary>Enumerates the individual flags contained in <paramref name="areas"/>, in execution order.</summary>
    public static IReadOnlyList<CleanArea> Split(CleanArea areas)
    {
        CleanArea[] order =
        [
            CleanArea.WorkingSet,
            CleanArea.SystemFileCache,
            CleanArea.RegistryCache,
            CleanArea.ModifiedPageList,
            CleanArea.StandbyList,
            CleanArea.CombinedMemoryList,
        ];

        return order.Where(area => area != CleanArea.None && areas.HasFlag(area)).ToArray();
    }
}
