using System.Diagnostics;
using System.Runtime.InteropServices;
using FUPlayer.Audio.Windows.Interop;
using FUPlayer.Core.Audio;
using FUPlayer.Core.Capture;
using FUPlayer.Core.Decoding;

namespace FUPlayer.Audio.Windows;

/// <summary>Lists the applications holding an audio session and opens a capture stream over one of them.</summary>
public sealed class ProcessLoopbackProvider : ICaptureProvider
{
    private static readonly Guid SessionManagerIid = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    private readonly string? _stateDirectory;

    /// <param name="stateDirectory">
    /// Where to note the devices muted during a capture, so that a crash does not leave them muted: they are
    /// unmuted here, on the next start. Null keeps no note.
    /// </param>
    public ProcessLoopbackProvider(string? stateDirectory = null)
    {
        _stateDirectory = stateDirectory;
        if (IsSupported)
        {
            DirectOutputMuter.RestoreAfterCrash(stateDirectory);
        }
    }

    public bool IsSupported => LoopbackConstants.IsSupported;

    public string? UnsupportedReason => IsSupported
        ? null
        : "Capturing one application's audio needs Windows 10 build 20348 or later.";

    public IReadOnlyList<CaptureTarget> List()
    {
        if (!IsSupported)
        {
            return [];
        }

        var found = new Dictionary<int, CaptureTarget>();
        IMMDeviceEnumerator enumerator = WasapiBackend.CreateEnumerator();
        try
        {
            IMMDevice device = WasapiBackend.OpenDevice(enumerator, null);
            try
            {
                Guid iid = SessionManagerIid;
                if (device.Activate(ref iid, NativeMethods.ClsCtxAll, IntPtr.Zero, out object raw) != WasapiConstants.SOk)
                {
                    return [];
                }

                var manager = (IAudioSessionManager2)raw;
                try
                {
                    Collect(manager, found);
                }
                finally
                {
                    Marshal.ReleaseComObject(manager);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return [];
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return [.. found.Values.OrderByDescending(t => t.IsPlaying).ThenBy(t => t.ProcessName, StringComparer.OrdinalIgnoreCase)];
    }

    public IAudioDecoder Open(int processId, int channels, CaptureOptions? options = null)
    {
        (int rate, int mixChannels) = MixFormat();
        var decoder = new ProcessLoopbackDecoder(processId, rate, Math.Clamp(channels <= 0 ? mixChannels : channels, 1, 8));
        if (options is { SilenceDirectOutput: true })
        {
            decoder.SilenceDirectOutput(options, _stateDirectory);
        }

        return decoder;
    }

    private static void Collect(IAudioSessionManager2 manager, Dictionary<int, CaptureTarget> found)
    {
        if (manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions) != WasapiConstants.SOk)
        {
            return;
        }

        try
        {
            if (sessions.GetCount(out int count) != WasapiConstants.SOk)
            {
                return;
            }

            int self = Environment.ProcessId;
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out IAudioSessionControl2 session) != WasapiConstants.SOk)
                {
                    continue;
                }

                try
                {
                    // The system sounds session belongs to no process worth listing, and capturing our
                    // own output would feed the player back into itself.
                    if (session.IsSystemSoundsSession() == WasapiConstants.SOk
                        || session.GetProcessId(out uint pid) != WasapiConstants.SOk
                        || pid == 0
                        || pid == self)
                    {
                        continue;
                    }

                    string name = ProcessName((int)pid);
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    bool playing = session.GetState(out AudioSessionState state) == WasapiConstants.SOk
                        && state == AudioSessionState.Active;
                    string display = session.GetDisplayName(out string given) == WasapiConstants.SOk ? given : string.Empty;

                    // One application can hold several sessions; keep the one that is making sound.
                    if (!found.TryGetValue((int)pid, out CaptureTarget? existing) || (playing && !existing.IsPlaying))
                    {
                        found[(int)pid] = new CaptureTarget((int)pid, name, Clean(display, name), playing);
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

    /// <summary>Some applications put a resource path such as "@%SystemRoot%\..." in the display name.</summary>
    private static string Clean(string display, string fallback) =>
        string.IsNullOrWhiteSpace(display) || display.StartsWith('@') ? fallback : display;

    private static string ProcessName(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The rate Windows mixes at. Capturing at that rate means the only conversion in the chain is the
    /// player's own, rather than the audio engine's resampler followed by it.
    /// </summary>
    public static unsafe (int Rate, int Channels) MixFormat()
    {
        IMMDeviceEnumerator enumerator = WasapiBackend.CreateEnumerator();
        try
        {
            IMMDevice device = WasapiBackend.OpenDevice(enumerator, null);
            IAudioClient client = WasapiBackend.ActivateClient(device);
            try
            {
                if (client.GetMixFormat(out IntPtr mix) != WasapiConstants.SOk)
                {
                    return (48_000, 2);
                }

                try
                {
                    return (*(int*)(mix + 4), Math.Max(1, (int)*(ushort*)(mix + 2)));
                }
                finally
                {
                    Marshal.FreeCoTaskMem(mix);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(client);
                Marshal.ReleaseComObject(device);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return (48_000, 2);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }
}

/// <summary>
/// A decoder over a live application. It has no length and no end: when the application falls silent
/// the reader returns zeros, so the player keeps its clock and the chain stays warm.
/// </summary>
internal sealed class ProcessLoopbackDecoder : IAudioDecoder, IDiagnosticCapture
{
    private readonly ProcessLoopbackCapture _capture;
    private readonly string _name;
    private readonly int _processId;
    private DirectOutputMuter? _muter;
    private float[] _scratch = [];
    private long _position;

    public ProcessLoopbackDecoder(int processId, int sampleRate, int channels)
    {
        _processId = processId;
        _capture = new ProcessLoopbackCapture(processId, sampleRate, channels);
        _capture.Start();
        Format = StreamFormat.Pcm(sampleRate, channels, 32);

        string name;
        try
        {
            using Process process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            name = processId.ToString();
        }

        _name = name;
    }

    public StreamFormat Format { get; }

    public long Length => -1;

    public long Position => _position;

    public bool CanSeek => false;

    public string CodecName => $"Live capture ({_name})";

    public long CapturedFrames => _capture.CapturedFrames;

    public long SilentFrames => _capture.SilentFrames;

    public string? DirectOutputNote => _muter?.Note;

    public void SilenceDirectOutput(CaptureOptions options, string? stateDirectory) =>
        _muter ??= new DirectOutputMuter(_processId, options, stateDirectory);

    public int ReadPcm(double[][] destination, int offset, int maxFrames)
    {
        int channels = Format.Channels;
        int samples = maxFrames * channels;
        if (_scratch.Length < samples)
        {
            _scratch = new float[samples];
        }

        _capture.Read(_scratch, maxFrames);

        for (int channel = 0; channel < channels; channel++)
        {
            double[] target = destination[channel];
            int source = channel;
            for (int frame = 0; frame < maxFrames; frame++, source += channels)
            {
                target[offset + frame] = _scratch[source];
            }
        }

        _position += maxFrames;
        return maxFrames;
    }

    public int ReadDsd(byte[][] destination, int offset, int maxBytes) => 0;

    public void Seek(long position)
    {
    }

    public void Dispose()
    {
        _capture.Dispose();
        _muter?.Dispose();
    }
}
