namespace MemReduct.Core.Cleaners;

/// <summary>One reclamation strategy bound to exactly one <see cref="CleanArea"/>.</summary>
public interface IMemoryCleaner
{
    /// <summary>The area this cleaner implements.</summary>
    CleanArea Area { get; }

    /// <summary>Short label for UI and CLI listings.</summary>
    string DisplayName { get; }

    /// <summary>One-sentence explanation of what the operation does to the system.</summary>
    string Description { get; }

    /// <summary>True when the operation has no effect without an elevated token.</summary>
    bool RequiresElevation { get; }

    /// <summary>Performs the operation. Implementations must not throw for expected failures.</summary>
    CleanOutcome Clean(CleanContext context);
}
