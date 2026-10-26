using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FUPlayer.ServerController;

/// <summary>Windows owns cleanup of the listener and its descendants even if the controller crashes.</summary>
internal sealed class ServerProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    internal ServerProcessJob(Process process)
    {
        _handle = CreateJobObject(0, 0);
        try
        {
            if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
                || !AssignProcessToJobObject(_handle, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { _handle.Dispose(); throw; }
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, nint name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits limits, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
