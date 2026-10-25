using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using MemReduct.Core.Interop;

namespace MemReduct.Core;

/// <summary>Result of trying to raise one named privilege on the current process token.</summary>
public enum PrivilegeState
{
    /// <summary>The privilege is now held and enabled.</summary>
    Enabled,

    /// <summary>The token does not hold the privilege (usually: not running elevated).</summary>
    NotHeld,

    /// <summary>The privilege exists but the enable call failed for another reason.</summary>
    Failed,
}

/// <summary>
/// Raises privileges on the current process token. Memory-management operations each need a
/// different one:
/// <list type="bullet">
///   <item><c>SeDebugPrivilege</c> — open other users' processes for working-set trimming.</item>
///   <item><c>SeIncreaseQuotaPrivilege</c> — trim the system file cache working set.</item>
///   <item><c>SeProfileSingleProcessPrivilege</c> — drive the kernel page-list commands.</item>
/// </list>
/// </summary>
public static class PrivilegeEnabler
{
    public const string SeDebugPrivilege = "SeDebugPrivilege";
    public const string SeIncreaseQuotaPrivilege = "SeIncreaseQuotaPrivilege";
    public const string SeProfileSingleProcessPrivilege = "SeProfileSingleProcessPrivilege";

    private static readonly ConcurrentDictionary<string, PrivilegeState> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the current process token is a member of the local Administrators group.</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Enables <paramref name="privilegeName"/> on the current process token.
    /// Idempotent and cached — repeated calls for the same name cost nothing.
    /// </summary>
    public static PrivilegeState Enable(string privilegeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegeName);

        if (Cache.TryGetValue(privilegeName, out var cached) && cached == PrivilegeState.Enabled)
        {
            return PrivilegeState.Enabled;
        }

        var state = EnableCore(privilegeName);
        Cache[privilegeName] = state;
        return state;
    }

    /// <summary>Enables several privileges and returns the outcome for each.</summary>
    public static IReadOnlyDictionary<string, PrivilegeState> EnableAll(params string[] privilegeNames)
    {
        var result = new Dictionary<string, PrivilegeState>(privilegeNames.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var name in privilegeNames)
        {
            result[name] = Enable(name);
        }

        return result;
    }

    private static PrivilegeState EnableCore(string privilegeName)
    {
        var token = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(
                    NativeMethods.GetCurrentProcess(),
                    TokenAccess.TOKEN_ADJUST_PRIVILEGES | TokenAccess.TOKEN_QUERY,
                    out token))
            {
                return PrivilegeState.Failed;
            }

            if (!NativeMethods.LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                // Unknown privilege name on this platform.
                return PrivilegeState.Failed;
            }

            var state = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = TokenAccess.SE_PRIVILEGE_ENABLED },
            };

            var size = Marshal.SizeOf<TOKEN_PRIVILEGES>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(state, buffer, fDeleteOld: false);
                if (!NativeMethods.AdjustTokenPrivileges(token, false, buffer, (uint)size, IntPtr.Zero, IntPtr.Zero))
                {
                    return PrivilegeState.Failed;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            // AdjustTokenPrivileges succeeds even when it assigned nothing, so the
            // error code is the only reliable signal. Capture it before any other call.
            var error = Marshal.GetLastWin32Error();
            return error switch
            {
                Win32Error.ERROR_SUCCESS => PrivilegeState.Enabled,
                Win32Error.ERROR_NOT_ALL_ASSIGNED => PrivilegeState.NotHeld,
                _ => PrivilegeState.Failed,
            };
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(token);
            }
        }
    }
}
