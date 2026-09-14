using System.Runtime.InteropServices;

namespace FUPlayer.Audio.Windows.Interop;

/// <summary>Win32 functions used by the audio back-ends.</summary>
internal static partial class NativeMethods
{
    public const uint Infinite = 0xFFFFFFFF;
    public const uint WaitObject0 = 0x00000000;
    public const uint WaitTimeout = 0x00000102;
    public const int ClsCtxInprocServer = 0x1;
    public const int ClsCtxAll = 0x17;
    public const uint PmRemove = 0x0001;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
    public static partial IntPtr CreateEvent(IntPtr eventAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, IntPtr name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, IntPtr outer, int context, in Guid iid, out IntPtr instance);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(IntPtr reserved, int coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    /// <summary>Unloads in-process COM server DLLs that report they are no longer in use (after <paramref name="unloadDelay"/> ms idle).</summary>
    [LibraryImport("ole32.dll")]
    public static partial void CoFreeUnusedLibrariesEx(uint unloadDelay, uint reserved);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(ref PropVariant value);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(out Msg message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in Msg message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessage(in Msg message);

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    /// <summary>Dispatches pending window messages on the current thread (some ASIO drivers rely on them).</summary>
    public static void PumpMessages()
    {
        while (PeekMessage(out Msg message, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(message);
            DispatchMessage(message);
        }
    }
}
