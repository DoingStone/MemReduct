namespace MemReduct.Core;

/// <summary>Counters describing what the current process is doing, for diagnostics.</summary>
public static class RuntimeInfo
{
    /// <summary>Summary line for the CLI banner and the GUI about box.</summary>
    public static string Describe() =>
        $"MemReduct {typeof(RuntimeInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"} | " +
        $"{Environment.OSVersion.VersionString} | " +
        $"{(Environment.Is64BitProcess ? "x64" : "x86")} 进程 | " +
        $"{(PrivilegeEnabler.IsElevated ? "管理员权限" : "标准用户")}";

    /// <summary>True when the current process holds an elevated admin token.</summary>
    public static bool IsElevated => PrivilegeEnabler.IsElevated;
}
