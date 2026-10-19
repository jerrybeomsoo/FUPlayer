using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FUPlayer.Audio.Windows;

/// <summary>Requests the player's Windows process priority and records what Windows actually granted.</summary>
public static class ProcessScheduling
{
    /// <summary>The actual class, including a refusal or demotion by Windows, for diagnostics.</summary>
    public static string Status { get; private set; } = "Not requested";

    /// <summary>Requests real-time priority at launch without requiring an elevation prompt.</summary>
    public static void RequestRealtime()
    {
        if (!OperatingSystem.IsWindows())
        {
            Status = "Managed by the operating system";
            return;
        }

        using Process process = Process.GetCurrentProcess();
        nint token = 0;
        TokenPrivileges previous = default;
        bool enabled = false;
        try
        {
            // An administrator's token can contain the privilege while leaving it disabled. Enable only the
            // privilege already assigned to this process; this never changes account policy or prompts for elevation.
            if (OpenProcessToken(process.Handle, 0x28, out token)
                && LookupPrivilegeValue(null, "SeIncreaseBasePriorityPrivilege", out Luid luid))
            {
                TokenPrivileges requested = new() { Count = 1, Luid = luid, Attributes = 2 };
                enabled = AdjustTokenPrivileges(token, false, ref requested,
                    (uint)Marshal.SizeOf<TokenPrivileges>(), out previous, out _) && Marshal.GetLastPInvokeError() == 0;
            }

            process.PriorityClass = ProcessPriorityClass.RealTime;
            process.Refresh();
            Status = $"{process.PriorityClass} (requested RealTime)";
            if (process.PriorityClass != ProcessPriorityClass.RealTime && !enabled)
                Status += "; SeIncreaseBasePriorityPrivilege is unavailable";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            Status = $"RealTime request refused: {ex.Message}";
            try
            {
                process.PriorityClass = ProcessPriorityClass.High;
                process.Refresh();
                Status += $"; actual {process.PriorityClass}";
            }
            catch (Exception fallback) when (fallback is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                Status += $"; High request refused: {fallback.Message}";
            }
        }
        finally
        {
            if (enabled)
                _ = AdjustTokenPrivileges(token, false, ref previous, 0, out _, out _);
            if (token != 0) _ = CloseHandle(token);
        }

        Trace.WriteLine($"Process priority: {Status}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges requested, uint length, out TokenPrivileges previous, out uint returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
