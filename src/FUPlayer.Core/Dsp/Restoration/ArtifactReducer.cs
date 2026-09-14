namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Damps the warbling a low bit rate leaves behind.
///
/// When a perceptual encoder runs short of bits it keeps a high-frequency band in one frame and drops
/// it in the next. The band switching on and off at the frame rate is heard as a watery chirping,
/// which is the artefact usually called birdies. This limits how far a high bin's magnitude may move
/// between frames, so a band that flickers is held steady while a band that is genuinely there, or a
/// note that genuinely starts, passes through: a real onset moves every bin at once and is allowed
/// its full rise, whereas a lone bin jumping on its own is not.
///
/// It cannot put back what the encoder discarded. It only stops the remains from flapping.
/// </summary>
public sealed class ArtifactReducer : SpectralProcessor
{
    private readonly double[] _previous;

    /// <summary>Lowest frequency to work on. Below this nothing is touched.</summary>
    public double FromHz { get; set; } = 6_000.0;

    /// <summary>0 leaves the signal alone, 1 holds a lone bin almost still.</summary>
    public double Strength { get; set; } = 0.5;

    public ArtifactReducer(int sampleRate, int fftSize = 2048)
        : base(sampleRate, fftSize)
    {
        _previous = new double[Bins];
    }

    public override void Reset()
    {
        base.Reset();
        Array.Clear(_previous);
    }

    protected override void Transform(Span<double> re, Span<double> im)
    {
        double strength = Math.Clamp(Strength, 0.0, 1.0);
        if (strength <= 0.0)
        {
            return;
        }

        int start = Math.Clamp((int)(FromHz / BinHz), 1, Bins - 1);

        // A frame where everything rises together is a transient, not a codec dropping a band, so the
        // limit is lifted in proportion to how much of the band moved the same way.
        double rising = 0.0;
        for (int bin = start; bin < Bins; bin++)
        {
            double magnitude = Math.Sqrt((re[bin] * re[bin]) + (im[bin] * im[bin]));
            if (magnitude > _previous[bin])
            {
                rising += 1.0;
            }
        }

        rising /= Math.Max(1, Bins - start);
        double broadband = Math.Clamp((rising - 0.5) * 2.0, 0.0, 1.0);

        // From 24 dB a frame at the lightest setting down to 3 dB at the heaviest, relaxed again when
        // the whole band is moving.
        double stepDb = (24.0 - (21.0 * strength)) + (18.0 * broadband);
        double rise = Math.Pow(10.0, stepDb / 20.0);
        double fall = 1.0 / rise;

        for (int bin = start; bin < Bins; bin++)
        {
            double magnitude = Math.Sqrt((re[bin] * re[bin]) + (im[bin] * im[bin]));
            double previous = _previous[bin];

            double limited;
            if (previous <= 0.0)
            {
                limited = magnitude;
            }
            else
            {
                limited = Math.Clamp(magnitude, previous * fall, previous * rise);
            }

            if (magnitude > 0.0 && limited != magnitude)
            {
                double scale = limited / magnitude;
                re[bin] *= scale;
                im[bin] *= scale;
            }

            _previous[bin] = limited;
        }
    }
}
