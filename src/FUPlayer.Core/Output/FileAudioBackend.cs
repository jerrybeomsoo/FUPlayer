using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FUPlayer.Core.Dsp.Dsd;

namespace FUPlayer.Core.Output;

/// <summary>
/// Renders the processed output to disk: PCM and DoP as WAVE (switching to RF64 above 4 GiB), native DSD as DSF.
/// Runs faster than real time.
/// </summary>
public sealed class FileAudioBackend : IAudioBackend
{
    public const string BackendId = "file";

    private readonly Func<string> _directory;

    public FileAudioBackend(Func<string> directory)
    {
        _directory = directory;
    }

    public string Id => BackendId;

    public string DisplayName => "Render to file";

    public string Description => "Writes the processed stream to WAV/RF64 (PCM, DoP) or DSF (native DSD) files.";

    public bool IsAvailable => true;

    public bool SupportsNativeDsd => true;

    public bool IsBitPerfect => true;

    public bool HasControlPanel => false;

    /// <summary>Path of the most recently created file.</summary>
    public string? LastFilePath { get; private set; }

    public IReadOnlyList<AudioDevice> GetDevices() => [new AudioDevice(BackendId, "file", "Output folder", true)];

    public DeviceCapabilities GetCapabilities(string? deviceId, int channels) => DeviceCapabilities.Unrestricted();

    public IAudioStream OpenStream(string? deviceId, OutputFormat format, AudioStreamOptions options, IAudioRenderSource source)
    {
        string directory = _directory();
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        }

        Directory.CreateDirectory(directory);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string extension = format.Kind == OutputSampleKind.NativeDsd ? ".dsf" : ".wav";
        string path = Path.Combine(directory, $"FUPlayer-{stamp}-{format.DescribeShort().Replace('/', '-').Replace(' ', '_')}{extension}");
        LastFilePath = path;
        return new FileRenderStream(path, format, source);
    }

    public void ShowControlPanel(string? deviceId)
    {
    }

    private sealed class FileRenderStream : IAudioStream
    {
        private const int DsfBlockSize = 4096;

        /// <summary>RIFF(12) + JUNK(8+28) + fmt(8+40) + "data" id: the data chunk size field starts at byte 100.</summary>
        private const int WaveDataSizeOffset = 100;

        private readonly string _path;
        private readonly IAudioRenderSource _source;
        private readonly object _gate = new();
        private FileStream? _file;
        private Thread? _thread;
        private volatile bool _running;
        private long _dataBytes;
        private long _dsdBytesPerChannel;
        private byte[][]? _dsfBlocks;
        private int _dsfFill;

        public FileRenderStream(string path, OutputFormat format, IAudioRenderSource source)
        {
            _path = path;
            _source = source;
            Format = format;
            BufferFrames = Math.Max(1024, format.CanonicalFrameRate / 20);
        }

        public event EventHandler<Exception>? Failed;

        public OutputFormat Format { get; }

        public int BufferFrames { get; }

        public double LatencySeconds => 0.0;

        public bool IsRealtime => false;

        public void Start()
        {
            lock (_gate)
            {
                if (_running)
                {
                    return;
                }

                _file ??= new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
                if (_file.Length == 0)
                {
                    WriteHeader();
                }

                _running = true;
                _thread = new Thread(Run) { IsBackground = true, Name = "FUPLAYER file output" };
                _thread.Start();
            }
        }

        public void Stop()
        {
            _running = false;
            _thread?.Join();
            _thread = null;
        }

        public void Dispose()
        {
            Stop();
            lock (_gate)
            {
                if (_file is null)
                {
                    return;
                }

                FlushDsfBlocks(final: true);
                FinalizeHeader();
                _file.Dispose();
                _file = null;
            }
        }

        private void Run()
        {
            var canonical = new byte[BufferFrames * Format.CanonicalBytesPerFrame];
            var converted = new byte[canonical.Length];
            try
            {
                while (_running)
                {
                    int frames = _source.Render(canonical, BufferFrames, realtime: false);
                    if (frames == 0)
                    {
                        if (_source.IsDrained)
                        {
                            break;
                        }

                        Thread.Sleep(1);
                        continue;
                    }

                    lock (_gate)
                    {
                        WriteFrames(canonical.AsSpan(0, frames * Format.CanonicalBytesPerFrame), converted);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failed?.Invoke(this, ex);
            }
        }

        private void WriteFrames(ReadOnlySpan<byte> canonical, byte[] scratch)
        {
            if (Format.Kind == OutputSampleKind.NativeDsd)
            {
                AppendDsf(canonical);
                return;
            }

            int container = Format.Kind == OutputSampleKind.Dop ? 24 : Format.ContainerBits;
            int bytes = canonical.Length / 4 * (container / 8);
            CanonicalSamples.ToInteger(canonical, scratch, container);
            _file!.Write(scratch, 0, bytes);
            _dataBytes += bytes;
        }

        private void AppendDsf(ReadOnlySpan<byte> interleaved)
        {
            int channels = Format.Channels;
            _dsfBlocks ??= Enumerable.Range(0, channels).Select(_ => new byte[DsfBlockSize]).ToArray();
            int frames = interleaved.Length / channels;
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < channels; c++)
                {
                    _dsfBlocks[c][_dsfFill] = DsdConstants.Reverse(interleaved[f * channels + c]);
                }

                if (++_dsfFill == DsfBlockSize)
                {
                    FlushDsfBlocks(final: false);
                }
            }

            _dsdBytesPerChannel += frames;
        }

        private void FlushDsfBlocks(bool final)
        {
            if (_dsfBlocks is null || _dsfFill == 0)
            {
                return;
            }

            if (final)
            {
                foreach (byte[] block in _dsfBlocks)
                {
                    block.AsSpan(_dsfFill).Clear();
                }
            }

            foreach (byte[] block in _dsfBlocks)
            {
                _file!.Write(block, 0, DsfBlockSize);
            }

            _dataBytes += (long)DsfBlockSize * _dsfBlocks.Length;
            _dsfFill = 0;
        }

        private void WriteHeader()
        {
            Span<byte> header = stackalloc byte[92];
            header.Clear();
            if (Format.Kind == OutputSampleKind.NativeDsd)
            {
                Encoding.ASCII.GetBytes("DSD ").CopyTo(header);
                BinaryPrimitives.WriteInt64LittleEndian(header[4..], 28);
                Encoding.ASCII.GetBytes("fmt ").CopyTo(header[28..]);
                BinaryPrimitives.WriteInt64LittleEndian(header[32..], 52);
                BinaryPrimitives.WriteInt32LittleEndian(header[40..], 1);
                BinaryPrimitives.WriteInt32LittleEndian(header[44..], 0);
                BinaryPrimitives.WriteInt32LittleEndian(header[48..], Format.Channels switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 6, 6 => 7, _ => 2 });
                BinaryPrimitives.WriteInt32LittleEndian(header[52..], Format.Channels);
                BinaryPrimitives.WriteInt32LittleEndian(header[56..], Format.SampleRate);
                BinaryPrimitives.WriteInt32LittleEndian(header[60..], 1);
                BinaryPrimitives.WriteInt32LittleEndian(header[72..], DsfBlockSize);
                Encoding.ASCII.GetBytes("data").CopyTo(header[80..]);
                _file!.Write(header);
                return;
            }

            // RIFF WAVE with a JUNK chunk reserved for an RF64 ds64 chunk.
            int container = Format.Kind == OutputSampleKind.Dop ? 24 : Format.ContainerBits;
            int blockAlign = Format.Channels * container / 8;
            using var writer = new BinaryWriter(_file!, Encoding.ASCII, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(0u);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("JUNK"));
            writer.Write(28u);
            writer.Write(new byte[28]);
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(40u);
            writer.Write((ushort)0xFFFE);
            writer.Write((ushort)Format.Channels);
            writer.Write((uint)Format.SampleRate);
            writer.Write((uint)(Format.SampleRate * blockAlign));
            writer.Write((ushort)blockAlign);
            writer.Write((ushort)container);
            writer.Write((ushort)22);
            writer.Write((ushort)Math.Min(Format.ValidBits, container));
            writer.Write(Format.Channels == 2 ? 3u : 0u);
            writer.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(0u);
        }

        private void FinalizeHeader()
        {
            if (_file is null)
            {
                return;
            }

            long end = _file.Length;
            using var writer = new BinaryWriter(_file, Encoding.ASCII, leaveOpen: true);
            if (Format.Kind == OutputSampleKind.NativeDsd)
            {
                _file.Position = 12;
                writer.Write(end);
                _file.Position = 64;
                writer.Write(_dsdBytesPerChannel * 8);
                _file.Position = 84;
                writer.Write(12L + _dataBytes);
            }
            else if (end - 8 <= uint.MaxValue)
            {
                _file.Position = 4;
                writer.Write((uint)(end - 8));
                _file.Position = WaveDataSizeOffset;
                writer.Write((uint)_dataBytes);
            }
            else
            {
                int blockAlign = Format.Channels * (Format.Kind == OutputSampleKind.Dop ? 24 : Format.ContainerBits) / 8;
                _file.Position = 0;
                writer.Write(Encoding.ASCII.GetBytes("RF64"));
                writer.Write(uint.MaxValue);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("ds64"));
                writer.Write(28u);
                writer.Write((ulong)(end - 8));
                writer.Write((ulong)_dataBytes);
                writer.Write((ulong)(_dataBytes / blockAlign));
                writer.Write(0u);
                _file.Position = WaveDataSizeOffset;
                writer.Write(uint.MaxValue);
            }

            _file.Position = end;
        }
    }
}
