using System.Diagnostics;

namespace FUPlayer.Core.Output;

/// <summary>Real-time paced sink that discards audio. Useful for testing the engine without hardware.</summary>
public sealed class NullAudioBackend : IAudioBackend
{
    public const string BackendId = "null";

    public string Id => BackendId;

    public string DisplayName => "Silent output";

    public string Description => "Consumes audio in real time without playing it (testing and benchmarking).";

    public bool IsAvailable => true;

    public bool SupportsNativeDsd => true;

    public bool IsBitPerfect => true;

    public bool HasControlPanel => false;

    public IReadOnlyList<AudioDevice> GetDevices() => [new AudioDevice(BackendId, "silent", "Silent device", true)];

    public DeviceCapabilities GetCapabilities(string? deviceId, int channels) => DeviceCapabilities.Unrestricted();

    public IAudioStream OpenStream(string? deviceId, OutputFormat format, AudioStreamOptions options, IAudioRenderSource source) =>
        new NullStream(format, options, source);

    public void ShowControlPanel(string? deviceId)
    {
    }

    private sealed class NullStream : IAudioStream
    {
        private readonly IAudioRenderSource _source;
        private Thread? _thread;
        private volatile bool _running;

        public NullStream(OutputFormat format, AudioStreamOptions options, IAudioRenderSource source)
        {
            Format = format;
            _source = source;
            int milliseconds = options.BufferMilliseconds > 0 ? options.BufferMilliseconds : 10;
            BufferFrames = Math.Max(64, format.CanonicalFrameRate * milliseconds / 1000);
        }

        public event EventHandler<Exception>? Failed;

        public OutputFormat Format { get; }

        public int BufferFrames { get; }

        public double LatencySeconds => (double)BufferFrames / Format.CanonicalFrameRate;

        public bool IsRealtime => true;

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "FUPLAYER silent output", Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            _thread?.Join();
            _thread = null;
        }

        public void Dispose() => Stop();

        private void Run()
        {
            var buffer = new byte[BufferFrames * Format.CanonicalBytesPerFrame];
            var clock = Stopwatch.StartNew();
            long renderedFrames = 0;
            try
            {
                while (_running)
                {
                    long due = (long)(clock.Elapsed.TotalSeconds * Format.CanonicalFrameRate);
                    while (renderedFrames + BufferFrames <= due)
                    {
                        _source.Render(buffer, BufferFrames, realtime: true);
                        renderedFrames += BufferFrames;
                    }

                    Thread.Sleep(2);
                }
            }
            catch (Exception ex)
            {
                Failed?.Invoke(this, ex);
            }
        }
    }
}
