using System.Runtime.InteropServices;
using System.Text;
using FUPlayer.Audio.Windows.Interop;
using FUPlayer.Core.Capture;

namespace FUPlayer.Audio.Windows;

/// <summary>
/// Mutes the devices a captured application plays to, for as long as it is captured.
///
/// Capturing an application does not take its sound away from its own device. Where that device is the player's
/// output too, both copies are heard, and they are not simultaneous: the player's is later by its whole chain. A
/// WASAPI exclusive stream locks other applications out of the device, which hides this; an ASIO driver that also
/// plays Windows audio, as RME's does at a matching rate, does not, and the result is an echo.
///
/// Muting the application's audio session would silence the capture as well, because the process loopback is
/// taken after the session volume. Muting the endpoint does not: its volume is applied after the loopback, so
/// the capture keeps every sample while the device plays none of the application's. Measured on this machine:
/// the session mute took the capture from −72 to −200 dBFS; the endpoint mute left it at −72.
///
/// One case cannot be helped: a shared WASAPI output on the very device the application plays to. The device
/// cannot be muted without muting the player, and the application's session cannot be muted without silencing
/// the capture, so the note says so and what silences it instead (an exclusive or ASIO output, or sending the
/// application to another device in Windows' own per-application setting).
///
/// Three endpoints are left alone: the player's own WASAPI device, whose mute would silence the player; any
/// endpoint whose mute is done in hardware while the output is ASIO, since that hardware may be the ASIO device;
/// and any the user unmutes while the capture runs. Whatever is muted here is written down before it is muted,
/// so a player that crashes mid-capture puts it back the next time it starts.
/// </summary>
internal sealed class DirectOutputMuter : IDisposable
{
    private const uint HardwareMute = 0x2;
    private static readonly Guid Context = new("5f1c2d6e-8a1b-4a6f-9d3e-2b7c4e1f0a93");
    private static readonly object FileGate = new();

    private readonly int _processId;
    private readonly CaptureOptions _options;
    private readonly string? _stateFile;
    private readonly Dictionary<string, string> _muted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _leftAlone = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private readonly object _gate = new();
    private volatile string? _note;
    private bool _disposed;

    public DirectOutputMuter(int processId, CaptureOptions options, string? stateDirectory)
    {
        _processId = processId;
        _options = options;
        _stateFile = stateDirectory is null ? null : Path.Combine(stateDirectory, StateFileName);
        _timer = new Timer(_ => Scan(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    public const string StateFileName = "muted-outputs.txt";

    /// <summary>What was muted, or why a device was left playing; null until the application is found playing.</summary>
    public string? Note => _note;

    /// <summary>Unmutes whatever a previous run muted and did not live to unmute.</summary>
    public static void RestoreAfterCrash(string? stateDirectory)
    {
        if (stateDirectory is null)
        {
            return;
        }

        string file = Path.Combine(stateDirectory, StateFileName);
        string[] ids;
        lock (FileGate)
        {
            try
            {
                if (!File.Exists(file))
                {
                    return;
                }

                ids = File.ReadAllLines(file).Select(line => line.Trim('\ufeff', ' ')).ToArray();
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }

        RunInMta(() =>
        {
            IMMDeviceEnumerator enumerator = WasapiBackend.CreateEnumerator();
            try
            {
                foreach (string id in ids.Where(i => i.Length > 0))
                {
                    if (enumerator.GetDevice(id, out IMMDevice device) != WasapiConstants.SOk)
                    {
                        continue;
                    }

                    try
                    {
                        SetMute(device, false);
                    }
                    finally
                    {
                        WasapiBackend.Release(device);
                    }
                }
            }
            finally
            {
                WasapiBackend.Release(enumerator);
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        using (var stopped = new ManualResetEvent(false))
        {
            _timer.Dispose(stopped);
            stopped.WaitOne(TimeSpan.FromSeconds(3));
        }

        RunInMta(Unmute);
    }

    private void Scan()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                ScanEndpoints();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
            {
                // The audio service is restarting or a device is going away; the next scan tries again.
            }
        }
    }

    private void ScanEndpoints()
    {
        HashSet<int> tree = ProcessTree.Of(_processId);
        IMMDeviceEnumerator enumerator = WasapiBackend.CreateEnumerator();
        IMMDeviceCollection? collection = null;
        try
        {
            string? own = OwnEndpoint(enumerator);
            if (enumerator.EnumAudioEndpoints(DataFlow.Render, WasapiConstants.DeviceStateActive, out collection) != WasapiConstants.SOk)
            {
                return;
            }

            collection.GetCount(out int count);
            var notes = new List<string>();
            bool shared = false;
            for (int i = 0; i < count; i++)
            {
                if (collection.Item(i, out IMMDevice device) != WasapiConstants.SOk)
                {
                    continue;
                }

                try
                {
                    device.GetId(out string id);
                    string name = WasapiBackend.ReadFriendlyName(device) ?? id;
                    if (!PlaysOn(device, tree))
                    {
                        if (_muted.ContainsKey(id))
                        {
                            notes.Add($"muted {name}");
                        }

                        continue;
                    }

                    if (string.Equals(id, own, StringComparison.OrdinalIgnoreCase))
                    {
                        // Muting it would silence the player too. Under a shared stream the application keeps
                        // playing to the same device, which is the echo this class exists to stop and the one
                        // case it cannot.
                        shared = IsShared;
                        notes.Add(shared
                            ? $"left {name} playing: the player shares it, so the application is still heard through it directly"
                            : $"left {name} playing: it is the player's own output");
                        continue;
                    }

                    IAudioEndpointVolume volume = EndpointVolume(device);
                    try
                    {
                        volume.GetMute(out bool isMuted);
                        if (_muted.ContainsKey(id))
                        {
                            if (!isMuted)
                            {
                                // Unmuted by hand while the capture runs: the user wants to hear it.
                                _muted.Remove(id);
                                _leftAlone.Add(id);
                                Persist();
                            }
                            else
                            {
                                notes.Add($"muted {name}");
                            }

                            continue;
                        }

                        if (isMuted || _leftAlone.Contains(id))
                        {
                            continue;
                        }

                        volume.QueryHardwareSupport(out uint hardware);
                        if (IsAsio && (hardware & HardwareMute) != 0)
                        {
                            notes.Add($"left {name} playing: its mute is in hardware, which may be the ASIO device's");
                            continue;
                        }

                        _muted[id] = name;
                        Persist();
                        Guid context = Context;
                        volume.SetMute(true, ref context);
                        notes.Add($"muted {name}");
                    }
                    finally
                    {
                        WasapiBackend.Release(volume);
                    }
                }
                finally
                {
                    WasapiBackend.Release(device);
                }
            }

            if (notes.Count > 0)
            {
                string done = string.Join("; ", notes.Distinct());
                _note = shared
                    ? "Heard twice: a shared WASAPI stream leaves the application playing to the same device, and muting that "
                        + "device would silence the player with it. WASAPI exclusive or ASIO output locks other applications "
                        + $"out of the device; or send the application to another device under Windows' app volume and "
                        + $"device preferences, which leaves the capture as it is. ({done}.)"
                    : $"So it is heard once, through the player: {done}.";
            }
        }
        finally
        {
            WasapiBackend.Release(collection);
            WasapiBackend.Release(enumerator);
        }
    }

    private bool IsAsio => string.Equals(_options.OutputBackendId, AsioBackend.BackendId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The player's own output is a shared WASAPI stream, which other applications play beside.</summary>
    private bool IsShared => string.Equals(_options.OutputBackendId, WasapiBackend.SharedId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The endpoint the player itself renders to, when its output is a WASAPI device.</summary>
    private string? OwnEndpoint(IMMDeviceEnumerator enumerator)
    {
        if (_options.OutputBackendId is not (WasapiBackend.ExclusiveId or WasapiBackend.SharedId))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(_options.OutputDeviceId))
        {
            return _options.OutputDeviceId;
        }

        if (enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out IMMDevice device) != WasapiConstants.SOk)
        {
            return null;
        }

        try
        {
            device.GetId(out string id);
            return id;
        }
        finally
        {
            WasapiBackend.Release(device);
        }
    }

    private static bool PlaysOn(IMMDevice device, HashSet<int> tree)
    {
        Guid iid = typeof(IAudioSessionManager2).GUID;
        if (device.Activate(ref iid, NativeMethods.ClsCtxAll, IntPtr.Zero, out object raw) != WasapiConstants.SOk)
        {
            return false;
        }

        var manager = (IAudioSessionManager2)raw;
        try
        {
            if (manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions) != WasapiConstants.SOk)
            {
                return false;
            }

            try
            {
                sessions.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out IAudioSessionControl2 session) != WasapiConstants.SOk)
                    {
                        continue;
                    }

                    try
                    {
                        if (session.GetProcessId(out uint pid) == WasapiConstants.SOk
                            && tree.Contains((int)pid)
                            && session.GetState(out AudioSessionState state) == WasapiConstants.SOk
                            && state != AudioSessionState.Expired)
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(session);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessions);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }

        return false;
    }

    private static IAudioEndpointVolume EndpointVolume(IMMDevice device)
    {
        Guid iid = typeof(IAudioEndpointVolume).GUID;
        int hr = device.Activate(ref iid, NativeMethods.ClsCtxAll, IntPtr.Zero, out object raw);
        if (hr != WasapiConstants.SOk)
        {
            throw new COMException("The endpoint has no volume control.", hr);
        }

        return (IAudioEndpointVolume)raw;
    }

    private static void SetMute(IMMDevice device, bool mute)
    {
        IAudioEndpointVolume volume = EndpointVolume(device);
        try
        {
            Guid context = Context;
            volume.SetMute(mute, ref context);
        }
        finally
        {
            WasapiBackend.Release(volume);
        }
    }

    private void Unmute()
    {
        if (_muted.Count == 0)
        {
            return;
        }

        IMMDeviceEnumerator enumerator = WasapiBackend.CreateEnumerator();
        try
        {
            foreach (string id in _muted.Keys)
            {
                if (enumerator.GetDevice(id, out IMMDevice device) != WasapiConstants.SOk)
                {
                    continue;
                }

                try
                {
                    SetMute(device, false);
                }
                catch (COMException)
                {
                    // Gone with its device; there is nothing left to unmute.
                }
                finally
                {
                    WasapiBackend.Release(device);
                }
            }
        }
        finally
        {
            WasapiBackend.Release(enumerator);
            _muted.Clear();
            Persist();
        }
    }

    private void Persist()
    {
        if (_stateFile is null)
        {
            return;
        }

        lock (FileGate)
        {
            try
            {
                if (_muted.Count == 0)
                {
                    File.Delete(_stateFile);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
                File.WriteAllLines(_stateFile, _muted.Keys, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without the note a crash would leave the device muted, but muting is still what the user asked for.
            }
        }
    }

    /// <summary>Endpoint volume objects live in the multithreaded apartment; a caller on a UI thread is moved there.</summary>
    private static void RunInMta(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
        {
            Run(action);
            return;
        }

        var thread = new Thread(() => Run(action)) { IsBackground = true, Name = "FUPlayer endpoint mute" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(5));
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            // The audio service is unavailable; nothing can be muted or unmuted.
        }
    }
}

/// <summary>A process and every process it started, which is what a process-loopback capture of it collects.</summary>
internal static class ProcessTree
{
    private const uint SnapProcess = 0x2;
    private const uint QueryLimitedInformation = 0x1000;

    public static HashSet<int> Of(int root)
    {
        var parents = new Dictionary<int, List<int>>();
        IntPtr snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == new IntPtr(-1))
        {
            return [root];
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                int parent = (int)entry.ParentProcessId;
                if (!parents.TryGetValue(parent, out List<int>? children))
                {
                    children = [];
                    parents[parent] = children;
                }

                children.Add((int)entry.ProcessId);
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var tree = new HashSet<int> { root };
        var pending = new Stack<int>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            int parent = pending.Pop();
            if (!parents.TryGetValue(parent, out List<int>? children))
            {
                continue;
            }

            // A process outlives the one that started it, and that one's number goes to some later process: a child
            // that started before the process now holding its parent's number was not started by it.
            long? parentStart = StartTime(parent);
            foreach (int child in children)
            {
                if (child == parent || (parentStart is long started && StartTime(child) is long childStart && childStart < started))
                {
                    continue;
                }

                if (tree.Add(child))
                {
                    pending.Push(child);
                }
            }
        }

        return tree;
    }

    private static long? StartTime(int processId)
    {
        IntPtr handle = OpenProcess(QueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return GetProcessTimes(handle, out long creation, out _, out _, out _) ? creation : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
