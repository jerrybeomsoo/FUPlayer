# DSP studio

Choose the output mode first, then the filter and its length. Start with the automatic length and add
longer filters only after checking playback stability. The response view lets you inspect the selected
design without playing audio.

## Filter design

Windowed sinc filters offer different transition widths and ringing. Minimum/intermediate phase choices,
where available, move the response in time. The [Transient-Aligned Filter](transient-aligned-filter.md)
retains a central sinc region and tapers its ends. Simple interpolation and IIR choices trade a shorter
response for different frequency behaviour. None of the plots establishes a listening preference.

Lengths are counted at the output rate. More taps usually make a sharper transition and require more
memory and work. The phase and length determine group delay. **Passband** zooms in on the region near the
source bandwidth; a transition peak can be a real part of the design. The TAF cutoff marker is about
-6.02 dB, rather than the conventional -3 dB point of some other filter designs.

## Convolution and delay

Automatic convolution uses FFT processing for long filters. Tap-by-tap processing avoids FFT block delay
but can require much more arithmetic. The maximum block setting trades processing cost against buffering;
layered partitions can reduce the cost of a short block, using the processor.

Group delay, FFT block delay and the playback FIFO are separate. A faster GPU reduces calculation time,
but does not remove the filter's time response. **Settings → Preprocess files ahead** can compensate the
leading delay for seekable files by preparing future samples before output starts. Live input cannot use it.
Now playing reports the active chain and measured processing load. Compute op/s counts arithmetic, not
audio quality, and different algorithms can do the same job with different numbers of operations.

## Optional stages

The [restorer](neural-restorer.md) works at the source rate, followed by the [upscaler](neural-upscaler.md),
then the optional [exciter](exciter.md) and final resampling/output stages. Source type controls whether
lossy-only processing is used. **Output delta** monitors the processing difference against aligned input;
it is a diagnostic listening/analysis mode, so turn it off for ordinary playback.

For network processing, select individual stages in [Network DSP](network-dsp.md). Neural **Runs on**
selectors are grey when that stage is remote. Local volume, limiting and audio output remain on the player.
