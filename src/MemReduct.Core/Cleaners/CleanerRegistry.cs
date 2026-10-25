namespace MemReduct.Core.Cleaners;

/// <summary>Maps <see cref="CleanArea"/> flags onto cleaner instances.</summary>
public static class CleanerRegistry
{
    /// <summary>All registered cleaners, in execution order.</summary>
    public static IReadOnlyList<IMemoryCleaner> All { get; } =
    [
        new WorkingSetCleaner(),
        new SystemFileCacheCleaner(),
        MemoryListCleaner.ModifiedPageList,
        MemoryListCleaner.StandbyList,
        MemoryListCleaner.CombinedMemoryList,
        new RegistryCacheCleaner(),
    ];

    /// <summary>Returns the cleaner implementing <paramref name="area"/>.</summary>
    public static IMemoryCleaner For(CleanArea area)
    {
        foreach (var cleaner in All)
        {
            if (cleaner.Area == area)
            {
                return cleaner;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(area), area, "No cleaner registered for this area.");
    }

    /// <summary>Returns the cleaners needed for <paramref name="areas"/>, in execution order.</summary>
    public static IReadOnlyList<IMemoryCleaner> Resolve(CleanArea areas) =>
        All.Where(cleaner => areas.HasFlag(cleaner.Area)).ToArray();

    /// <summary>Parses a comma/semicolon/space separated list such as <c>workingset,standby</c>.</summary>
    public static bool TryParseAreas(string? text, out CleanArea areas, out string? error)
    {
        areas = CleanArea.None;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "No areas given.";
            return false;
        }

        var tokens = text.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty))
            {
                case "all":
                    areas |= CleanAreas.All;
                    break;
                case "default":
                    areas |= CleanAreas.Default;
                    break;
                case "workingset":
                case "ws":
                    areas |= CleanArea.WorkingSet;
                    break;
                case "filecache":
                case "systemfilecache":
                case "cache":
                    areas |= CleanArea.SystemFileCache;
                    break;
                case "standby":
                case "standbylist":
                    areas |= CleanArea.StandbyList;
                    break;
                case "modified":
                case "modifiedpagelist":
                case "modifiedlist":
                    areas |= CleanArea.ModifiedPageList;
                    break;
                case "combined":
                case "combinedmemorylist":
                case "lists":
                    areas |= CleanArea.CombinedMemoryList;
                    break;
                case "registry":
                case "registrycache":
                    areas |= CleanArea.RegistryCache;
                    break;
                default:
                    error = $"Unknown area '{token}'. Known: workingset, filecache, standby, modified, combined, registry, default, all.";
                    return false;
            }
        }

        if (areas == CleanArea.None)
        {
            error = "No areas selected.";
            return false;
        }

        return true;
    }

    /// <summary>The canonical CLI token for an area.</summary>
    public static string ToToken(CleanArea area) => area switch
    {
        CleanArea.WorkingSet => "workingset",
        CleanArea.SystemFileCache => "filecache",
        CleanArea.StandbyList => "standby",
        CleanArea.ModifiedPageList => "modified",
        CleanArea.CombinedMemoryList => "combined",
        CleanArea.RegistryCache => "registry",
        _ => area.ToString().ToLowerInvariant(),
    };
}
