namespace MemReduct.Core.Cleaners;

/// <summary>Options threaded through a clean run.</summary>
public sealed class CleanContext
{
    /// <summary>Process names never touched by the working-set cleaner.</summary>
    public static readonly IReadOnlySet<string> DefaultDenyList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "Idle",
        "Registry",
        "Memory Compression",
        "Secure System",
        "System Interrupts",
        "smss",
    };

    /// <summary>Skip the actual kernel calls and only report what would happen.</summary>
    public bool DryRun { get; init; }

    /// <summary>Include the memory cleaner's own process in working-set trimming.</summary>
    public bool IncludeSelf { get; init; } = true;

    /// <summary>Process names to skip. Defaults to <see cref="DefaultDenyList"/>.</summary>
    public IReadOnlySet<string> ProcessDenyList { get; init; } = DefaultDenyList;

    /// <summary>Maximum parallelism used when trimming working sets.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    public static CleanContext Default { get; } = new();
}
