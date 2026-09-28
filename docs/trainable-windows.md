# Trainable windows

What "trainable windows" are, where the idea comes from, and what it would and would not do for FUPlayer.
Written for 0.3.0 (September 2026) from the papers themselves, a second opinion from Gemini 3.8 Flash, and
measurements made for this note.

## Where the idea comes from

The claim that trainable cosine-sum and Gaussian windows beat the Hamming window comes from one paper:
H. C. Prashanth, M. Rao, D. Eledath and V. Ramasubramanian, *Trainable windows for SincNet architecture*,
EURASIP Journal on Audio, Speech, and Music Processing 2023, article 3.

SincNet (Ravanelli and Bengio, SLT 2018) starts a speaker-recognition network with 80 band-pass filters, each the
difference of two sincs, 251 taps long under a fixed Hamming window; only the cut-offs are learned. The paper tried
28 windows in that place, for speaker identification on TIMIT, and let the shape of some of them be trained with the
network: a Gaussian's width, and the coefficients of a generalised cosine sum of up to ten terms, one window shared
by all 80 filters.

The sentence-level error at the last epoch was 0.0123 with Hamming, 0.0072 with a trained Gaussian and 0.0022 with a
trained nine-term cosine sum: the "41 % and 82 % better" that is quoted. Three things belong next to those numbers.

- It is **speaker identification on TIMIT only**. The paper has no speech-recognition or speaker-separation
  experiment, though it is sometimes summarised as improving both.
- The test set is 1,890 sentences, so the difference is about 23 misclassified sentences against 4. The headline
  figures are single runs; five-seed runs to epoch 100 are shown as a figure.
- Two **fixed** windows, Parzen and Bohman, improved on Hamming by 41 % and 47 %. The authors put the gain down to
  lower stopband ripple and less spectral leakage than Hamming's, whose peak sidelobe is at −43 dB. A low-leakage
  window is what helped, trained or not.

The same line of work includes LEAF (Zeghidour et al., ICLR 2021), which learns a Gaussian low-pass pooling window
per channel, and the differentiable STFT of Leiber et al. (2022 to 2025), which learns window lengths. The method of
fitting a cosine sum's coefficients to an objective is older: Nuttall (IEEE Trans. ASSP 1981) and Albrecht
(ICASSP 2001, up to eleven terms and −289 dB peak sidelobe) fitted theirs to signal-processing criteria rather than
to a network's loss.

## Where a window matters in FUPlayer

In three places: the resampling filters, the STFT the two networks work in, and the analyzer.

### Resampling filters: nothing to train at playback, but the method carries over

A resampling filter has nothing to learn from while it plays: what it should do (pass the audio band flat, reject
the images, finish its transition in time) is known before a note is played, and there is no loss to follow. And the
mechanism behind the paper's gain is already spent here: its baseline leaks at −43 dB, where FUPlayer's filters hold
110 to 260 dB.

What does carry over is the method: fit a cosine sum's coefficients once, against the objective a windowed-sinc
filter actually has, and ship them as constants. The objective used here is the lowest peak stopband level from the
edge of the stopband on, for a given transition width, with the window made to reach zero at its ends so that the
stopband keeps falling away from the edge: 18 dB an octave when the window reaches zero (C1), 30 when its second
derivative does too (C3). Because the windowed ideal low-pass has a closed-form edge response,

    H(ξ) = ½ − (1/π) [ b₀ Si(πξ) + ½ Σₖ bₖ ( Si(π(ξ − k)) + Si(π(ξ + k)) ) ],   ξ = (f − f_c) · N,

which is linear in the coefficients and the same for every length N, the fit is a small minimax problem (solved
here by iteratively reweighted least squares in double precision), and the result applies to a 1,000-tap filter and
a 30-million-tap one alike. Results, as the transition width each window needs relative to Kaiser for the same level
at the edge, and the level four, eight and sixteen transition widths further out:

| At the edge | Window | Length | 4 / 8 / 16 widths out |
| --- | --- | --- | --- |
| 110 dB | Kaiser | 1.00× | 131 / 136 / 142 dB |
| | Nuttall (4 terms, C1), in the catalogue since 0.3.0 | 1.09× | 145 / 162 / 179 dB |
| | fitted, 9 terms, C1 | 1.01× | 141 / 157 / 174 dB |
| | fitted, 9 terms, C3 | 1.10× | 178 / 206 / 235 dB |
| 150 dB | Kaiser | 1.00× | 169 / 174 / 180 dB |
| | fitted, 9 terms, C1 | 1.01× | 175 / 191 / 208 dB |
| | fitted, 9 terms, C3 | 1.06× | 210 / 237 / 266 dB |
| 200 dB | Kaiser | 1.00× | 218 / 223 / 229 dB |
| | fitted, 10 terms, C1 | 1.01× | 219 / 234 / 251 dB |
| | fitted, 10 terms, C3 | 1.05× | 250 / 277 / 297 dB |

The 150 dB nine-term window was checked as a real 8,001-tap filter: 0.8 % longer than Kaiser, and 175 / 191 / 208 dB
out where Kaiser is at 169 / 174 / 180, as the formula said. Limited to four terms, the fit lands on Nuttall's own
window (0.356, 0.487, 0.144, 0.0126 against Nuttall's 0.355768, 0.487396, 0.144232, 0.012604).

So a fitted cosine sum is as short as Kaiser, to within a few per cent, and its stopband goes on falling where
Kaiser's levels off. It costs nothing at playback: the coefficients are constants, and a window is computed once per
filter. But the gain is measurable, not audible. Every figure in the table is far below the noise of any converter,
and the images that land further out are already 170 dB down or more with Kaiser at 150 dB. Double precision is
enough for the fit to about 200 dB; around 250 dB the cancellation in the edge formula leaves only a few digits, and
a fit there should be checked in extended precision.

### The networks' STFT: the one place the paper's lesson applies directly

The restorer and the upscaler both work on a 2,048-point STFT at a hop of 256 with a periodic Hann window, computed by
PyTorch in training and by the player in C# (`NeuralRestorer`), outside the ONNX graph. That is the paper's
situation: a fixed window in front of a trained network. Hann's sidelobes start at −31 dB and fall 18 dB an octave,
so a strong partial spreads above the restorer's feature floor (85 dB below each frame's loudest bin) over roughly
ten to twenty bins on either side, which is where AAC's dead-zone holes, the thing the restorer has to find and fill,
can be hidden.

Testing it means training. The variants: Hann as it is; a fixed low-leakage window (Nuttall, or Kaiser with β
around 10); and a trained four-term cosine sum, started at Hann, the same window used for analysis and synthesis
(`torch.istft`'s division by the summed squared window keeps reconstruction exact for any positive window at 8×
overlap). Two cautions. The training losses here are computed on the network's own spectra, so with a trained
window they must be moved to a fixed reference STFT, or the optimiser can lower the loss by reshaping the window
instead of improving the audio. And a lower-leakage window has a wider main lobe (about 8 bins for Nuttall against 4
for Hann), so transients and pre-echo need measuring as well as log-spectral distance. A full restorer run took
7 h 17 min here; a screening round of the three variants at a third of that length is an afternoon of GPU time. A
change would mean new models, and the player building whatever window the model's metadata names.

### The analyzer

A display, whose window the viewer chooses. There is nothing to train.

## Recommendation

1. **No trainable window in the playback path.** There is nothing to train it against, and the filters already
   have the low leakage the paper's gain came from.
2. The **Nuttall filters** added in 0.3.0 already give a stopband that keeps falling, at 110 dB.
3. If the idea is to go further, there are two honest forms of it:
   - **Fitted cosine-sum filters** at 150, 200 and 250 dB: as long as Kaiser's to within a few per cent, 20 to 70 dB
     lower further out. A few hours to add (coefficients, presets, tests), measurable and inaudible.
   - **The restorer's STFT window** as an experiment: the one place where the paper's mechanism, leakage in front of
     a network, is actually at work in FUPlayer. Worth an afternoon of training to find out, and worth shipping only
     if held-out log-spectral distance improves by more than the spread between seeds without costing transients.

## Sources

- Prashanth, Rao, Eledath and Ramasubramanian, [Trainable windows for SincNet architecture](https://link.springer.com/article/10.1186/s13636-023-00271-0), EURASIP JASMP 2023, 3.
- Ravanelli and Bengio, [Speaker recognition from raw waveform with SincNet](https://arxiv.org/abs/1808.00158), SLT 2018.
- Zeghidour, Teboul, de Chaumont Quitry and Tagliasacchi, [LEAF: a learnable frontend for audio classification](https://arxiv.org/abs/2101.08596), ICLR 2021.
- Leiber et al., [A differentiable short-time Fourier transform with respect to the window length](https://arxiv.org/abs/2208.10886), 2022.
- Albrecht, [A family of cosine-sum windows for high-resolution measurements](https://ieeexplore.ieee.org/document/940309/), ICASSP 2001.
- Nuttall, "Some windows with very good sidelobe behavior", IEEE Trans. ASSP 29(1), 1981.
