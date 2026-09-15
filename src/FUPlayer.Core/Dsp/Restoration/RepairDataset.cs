using FUPlayer.Core.Dsp.Numerics;

namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Turns a pair of signals, one coded and one not, into the frames a network is trained on.
///
/// For every frame the coded spectrum is patched exactly the way the player patches it, and the
/// answer recorded is how far each band of that patch is from the same band of the original. Doing
/// the patch here rather than leaving it to the network is what keeps the numbers bounded: the
/// question becomes how much to move a plausible spectrum, not how to conjure one from silence.
/// </summary>
public sealed class RepairFrameBuilder
{
    /// <summary>Transform size, matching the one the player repairs with.</summary>
    public const int FftSize = 2048;

    public const int Hop = FftSize / 4;

    private readonly RealFftPlan _plan = new(FftSize);
    private readonly double[] _window = new double[FftSize];
    private readonly double[] _frame = new double[FftSize];
    private readonly double[] _re = new double[FftSize];
    private readonly double[] _im = new double[FftSize];
    private readonly double[] _power;
    private readonly float[] _original;

    public RepairFrameBuilder(BandLayout layout, int context, int sampleRate)
    {
        Layout = layout;
        Context = context;
        SampleRate = sampleRate;
        BinHz = (double)sampleRate / FftSize;

        _power = new double[_plan.Bins];
        _original = new float[layout.Count];

        for (int i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / FftSize));
        }
    }

    public BandLayout Layout { get; }

    public int Context { get; }

    public int SampleRate { get; }

    public double BinHz { get; }

    public int InputSize => NeuralRepairModel.ExpectedInputs(Layout.Count, Context);

    public int OutputSize => Layout.Count;

    /// <summary>
    /// Band levels of one patched frame of the coded signal, and the gains that would take it to the
    /// original. Returns the coded frame's overall level.
    /// </summary>
    public double Describe(
        ReadOnlySpan<double> coded, ReadOnlySpan<double> original, int start, double cutoffHz, double ceilingHz,
        long frameIndex, Span<float> codedLevels, Span<float> gainsDb)
    {
        int edge = (int)Math.Ceiling(cutoffHz / BinHz) + 1;
        int ceiling = Math.Min(_plan.Bins - 1, (int)(ceilingHz / BinHz));

        for (int i = 0; i < FftSize; i++)
        {
            _frame[i] = coded[start + i] * _window[i];
        }

        _plan.Forward(_frame, _re, _im);
        SpectralPatch.Apply(_re.AsSpan(0, _plan.Bins), _im.AsSpan(0, _plan.Bins), edge, ceiling, frameIndex);
        Power();
        double level = Layout.Levels(_power, BinHz, codedLevels);

        for (int i = 0; i < FftSize; i++)
        {
            _frame[i] = original[start + i] * _window[i];
        }

        _plan.Forward(_frame, _re, _im);
        Power();
        double originalLevel = Layout.Levels(_power, BinHz, _original);

        // Both sets of levels have had their own loudness removed, so the difference of the two
        // loudnesses goes back in before they are compared.
        float offset = (float)(originalLevel - level);
        for (int band = 0; band < Layout.Count; band++)
        {
            gainsDb[band] = Math.Clamp(
                _original[band] + offset - codedLevels[band],
                NeuralRepairModel.MinGainDb,
                NeuralRepairModel.MaxGainDb);
        }

        return level;
    }

    /// <summary>Lays out the network's input from a run of frames: the bands either side, then two scalars.</summary>
    public static void Compose(
        ReadOnlySpan<float> window, int bands, int context, double level, double cutoffFraction, Span<float> input)
    {
        window[..(bands * ((2 * context) + 1))].CopyTo(input);
        int at = bands * ((2 * context) + 1);

        // Loudness in a range the network can work with, and where the codec's wall sits.
        input[at] = (float)Math.Clamp((level + 60.0) / 40.0, -3.0, 3.0);
        input[at + 1] = (float)Math.Clamp(cutoffFraction, 0.0, 1.0);
    }

    private void Power()
    {
        for (int bin = 0; bin < _power.Length; bin++)
        {
            _power[bin] = (_re[bin] * _re[bin]) + (_im[bin] * _im[bin]);
        }
    }
}

/// <summary>
/// The training file: a short header and then one record per frame, features followed by answers.
///
/// Flat float32 so it can be streamed past the trainer as many times as needed without parsing
/// anything, and so a few million frames stay a file rather than a memory problem.
/// </summary>
public static class RepairDataset
{
    public const int Magic = 0x53445546;
    public const int CurrentVersion = 2;

    /// <summary>
    /// Version 1 had no group table, so the only split available was the tail of the file, which is
    /// whichever releases happened to sort last. Version 2 records where each source file's records
    /// begin, so the held-out set can be spread across the whole corpus instead.
    /// </summary>
    public const int FirstVersionWithGroups = 2;

    public sealed class Writer : IDisposable
    {
        private readonly BinaryWriter _writer;
        private readonly List<long> _groups = [];
        private readonly long _countAt;
        private long _count;

        public Writer(string path, BandLayout layout, int context, int sampleRate)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _writer = new BinaryWriter(File.Create(path));
            _writer.Write(Magic);
            _writer.Write(CurrentVersion);
            _writer.Write(layout.Count);
            _writer.Write(context);
            _writer.Write(sampleRate);
            _writer.Write(layout.LowHz);
            _writer.Write(layout.HighHz);
            _countAt = _writer.BaseStream.Position;
            _writer.Write(0L);
            _writer.Write(0L);
        }

        public long Count => _count;

        /// <summary>Marks the start of another source file's records.</summary>
        public void BeginGroup() => _groups.Add(_count);

        public void Add(ReadOnlySpan<float> input, ReadOnlySpan<float> target)
        {
            foreach (float value in input)
            {
                _writer.Write(value);
            }

            foreach (float value in target)
            {
                _writer.Write(value);
            }

            _count++;
        }

        public void Dispose()
        {
            // The table goes after the records, since neither its size nor the record count is known
            // until everything has been written.
            long table = _writer.BaseStream.Position;
            foreach (long start in _groups)
            {
                _writer.Write(start);
            }

            _writer.BaseStream.Position = _countAt;
            _writer.Write(_count);
            _writer.Write((long)_groups.Count);
            _writer.Dispose();
            _ = table;
        }
    }

    /// <summary>
    /// Joins training sets that were built the same way into one.
    ///
    /// Building a set is paced by the encoders, one file at a time, so an afternoon's corpus takes
    /// hours on one core and a fraction of that on several. Splitting the files between a few runs
    /// and joining the results afterwards is the whole of the parallelism, and it keeps the group
    /// boundaries, which is what the held-out split is built on.
    /// </summary>
    public static long Merge(IReadOnlyList<string> sources, string output)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sources.Count);

        var readers = new List<Reader>();
        try
        {
            foreach (string source in sources)
            {
                readers.Add(new Reader(source));
            }

            Reader first = readers[0];
            foreach (Reader other in readers.Skip(1))
            {
                if (other.Bands != first.Bands || other.Context != first.Context
                    || other.SampleRate != first.SampleRate
                    || Math.Abs(other.LowHz - first.LowHz) > 1e-6 || Math.Abs(other.HighHz - first.HighHz) > 1e-6)
                {
                    throw new InvalidDataException(
                        "Those sets were not built the same way: bands, context, rate and band range all have to match.");
                }
            }

            var layout = new BandLayout(first.LowHz, first.HighHz, first.Bands);
            long written = 0;

            using (var writer = new Writer(output, layout, first.Context, first.SampleRate))
            {
                const int Batch = 4096;
                float[] inputs = new float[Batch * first.InputSize];
                float[] targets = new float[Batch * first.OutputSize];

                foreach (Reader reader in readers)
                {
                    foreach ((long start, long end) in Spans(reader))
                    {
                        writer.BeginGroup();
                        for (long at = start; at < end;)
                        {
                            int want = (int)Math.Min(Batch, end - at);
                            int got = reader.Read(at, want, inputs, targets);
                            if (got == 0)
                            {
                                break;
                            }

                            for (int i = 0; i < got; i++)
                            {
                                writer.Add(
                                    inputs.AsSpan(i * first.InputSize, first.InputSize),
                                    targets.AsSpan(i * first.OutputSize, first.OutputSize));
                            }

                            at += got;
                            written += got;
                        }
                    }
                }
            }

            return written;
        }
        finally
        {
            foreach (Reader reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    /// <summary>Each source file's run of records, or one run covering everything when there is no table.</summary>
    private static IEnumerable<(long Start, long End)> Spans(Reader reader)
    {
        if (reader.Groups.Count == 0)
        {
            yield return (0, reader.Count);
            yield break;
        }

        for (int i = 0; i < reader.Groups.Count; i++)
        {
            long start = reader.Groups[i];
            long end = i + 1 < reader.Groups.Count ? reader.Groups[i + 1] : reader.Count;
            if (end > start)
            {
                yield return (start, end);
            }
        }
    }

    public sealed class Reader : IDisposable
    {
        private readonly BinaryReader _reader;
        private long _start;
        private byte[] _block = [];
        private float[]? _cache;

        public Reader(string path)
        {
            _reader = new BinaryReader(File.OpenRead(path));
            try
            {
                Open();
            }
            catch
            {
                // A file that is refused must not stay open, or it cannot even be deleted.
                _reader.Dispose();
                throw;
            }
        }

        private void Open()
        {
            if (_reader.ReadInt32() != Magic)
            {
                throw new InvalidDataException("That file is not a training set.");
            }

            Version = _reader.ReadInt32();
            if (Version != CurrentVersion)
            {
                throw new InvalidDataException($"The training set is version {Version}; this build reads version {CurrentVersion}.");
            }

            Bands = _reader.ReadInt32();
            Context = _reader.ReadInt32();
            SampleRate = _reader.ReadInt32();
            LowHz = _reader.ReadDouble();
            HighHz = _reader.ReadDouble();
            Count = _reader.ReadInt64();
            long groups = Version >= FirstVersionWithGroups ? _reader.ReadInt64() : 0L;
            _start = _reader.BaseStream.Position;

            InputSize = NeuralRepairModel.ExpectedInputs(Bands, Context);
            OutputSize = Bands;

            long expected = _start + (Count * (InputSize + OutputSize) * sizeof(float));
            if (_reader.BaseStream.Length < expected)
            {
                // A run that was interrupted leaves a short file; use what is there, and the table
                // that would have followed it is gone too.
                Count = (_reader.BaseStream.Length - _start) / ((InputSize + OutputSize) * sizeof(float));
                groups = 0;
            }

            if (groups > 0)
            {
                _reader.BaseStream.Position = expected;
                long[] starts = new long[groups];
                for (int i = 0; i < groups; i++)
                {
                    starts[i] = _reader.ReadInt64();
                }

                Groups = starts;
            }
        }

        public int Version { get; private set; }

        public int Bands { get; private set; }

        public int Context { get; private set; }

        public int SampleRate { get; private set; }

        public double LowHz { get; private set; }

        public double HighHz { get; private set; }

        public long Count { get; private set; }

        public int InputSize { get; private set; }

        public int OutputSize { get; private set; }

        /// <summary>True once the whole set is in memory.</summary>
        public bool IsCached => _cache is not null;

        /// <summary>
        /// Reads the whole set into memory when it will fit, which turns training from something the
        /// disk paces into something the arithmetic paces. An epoch reads the entire training portion,
        /// so a set read from disk is read once per epoch and again for every evaluation.
        /// Returns false, harmlessly, when there is not the room.
        /// </summary>
        public bool TryCache(long budgetBytes = 5L << 30)
        {
            long needed = Count * (InputSize + OutputSize) * sizeof(float);
            if (_cache is not null || needed > budgetBytes || needed > int.MaxValue)
            {
                return _cache is not null;
            }

            try
            {
                float[] cache = new float[Count * (InputSize + OutputSize)];
                byte[] buffer = new byte[1 << 20];
                _reader.BaseStream.Position = _start;

                int at = 0;
                long remaining = needed;
                while (remaining > 0)
                {
                    int want = (int)Math.Min(buffer.Length, remaining);
                    _reader.BaseStream.ReadExactly(buffer, 0, want);
                    Buffer.BlockCopy(buffer, 0, cache, at, want);
                    at += want;
                    remaining -= want;
                }

                _cache = cache;
                return true;
            }
            catch (Exception ex) when (ex is OutOfMemoryException or IOException)
            {
                _cache = null;
                return false;
            }
        }

        /// <summary>Where each source file's begin. Empty for a set built before this was recorded.</summary>
        public IReadOnlyList<long> Groups { get; private set; } = [];

        public BandLayout Layout => new(LowHz, HighHz, Bands);

        /// <summary>
        /// Splits the set into what to train on and what to keep back, one group in
        /// <paramref name="everyNth"/> held out so that the held-out material comes from across the
        /// whole corpus rather than from whichever files happened to sort last.
        /// </summary>
        public (List<(long Start, long End)> Training, List<(long Start, long End)> Held) Split(int everyNth = 8)
        {
            var training = new List<(long, long)>();
            var held = new List<(long, long)>();

            if (Groups.Count < everyNth)
            {
                // Too few groups to spread the split over, so fall back to the tail.
                long boundary = Count - Math.Max(2_000, Count / 10);
                training.Add((0, boundary));
                held.Add((boundary, Count));
                return (training, held);
            }

            for (int group = 0; group < Groups.Count; group++)
            {
                long start = Groups[group];
                long end = group + 1 < Groups.Count ? Groups[group + 1] : Count;
                if (end <= start)
                {
                    continue;
                }

                (group % everyNth == everyNth - 1 ? held : training).Add((start, end));
            }

            return (training, held);
        }

        /// <summary>
        /// Reads a run of records into flat buffers. Returns how many were read.
        ///
        /// The records are contiguous, so the whole run comes off the disk in one read and is then
        /// split up in memory. Reading it a number at a time costs forty thousand calls per batch and
        /// makes the disk, rather than the arithmetic, the thing training waits for.
        /// </summary>
        public int Read(long index, int count, Span<float> inputs, Span<float> targets)
        {
            long available = Math.Min(count, Count - index);
            if (available <= 0)
            {
                return 0;
            }

            int stride = InputSize + OutputSize;
            ReadOnlySpan<float> values;

            if (_cache is not null)
            {
                values = _cache.AsSpan((int)(index * stride), (int)available * stride);
            }
            else
            {
                int record = stride * sizeof(float);
                int wanted = (int)available * record;
                if (_block.Length < wanted)
                {
                    _block = new byte[wanted];
                }

                _reader.BaseStream.Position = _start + (index * record);
                _reader.BaseStream.ReadExactly(_block, 0, wanted);
                values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_block.AsSpan(0, wanted));
            }

            for (int i = 0; i < available; i++)
            {
                int at = i * stride;
                values.Slice(at, InputSize).CopyTo(inputs[(i * InputSize)..]);
                values.Slice(at + InputSize, OutputSize).CopyTo(targets[(i * OutputSize)..]);
            }

            return (int)available;
        }

        public void Dispose() => _reader.Dispose();
    }
}
