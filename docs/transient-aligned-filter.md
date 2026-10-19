# Transient-Aligned Filter

Select **Transient-Aligned** in DSP studio to use this resampling filter. It keeps a central region of the
ideal sinc and tapers the ends. It uses its own interpolation stages; the usual staging selector does not
replace those stages. The cutoff follows the source's Nyquist frequency, half its sample rate.

Longer lengths retain a larger sinc region, with more memory, calculation and delay. Start with the
automatic length and inspect the response before choosing a very long filter.

## Reading the percentage and plots

The sinc percentage describes the design's untapered region. The precision preview also shows the effect
of storing coefficients as 32-bit values when that precision is selected. It does not measure the errors
of a complete GPU FFT or prove that an entire output stream is sample-identical. Use Now playing's active
device and verification result to see which implementation actually runs.

The **Passband** graph includes the transition peak and an exact cutoff marker, about **-6.02 dB**.
A small peak near the transition is part of this filter's response. v0.6.0 fixes clipped peaks, misleading
decimation plots and crowded marker labels; it does not remove real ripple by drawing a flat line.
The flat-band marker and cutoff are distinct frequencies.

For startup delay, try **Settings → Preprocess files ahead** on seekable files. This prepares audio before
playback and compensates leading filter delay; the filter's phase, ringing and processing cost remain.
See [DSP studio](dsp.md) and [troubleshooting](troubleshooting.md).
