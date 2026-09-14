# fuplayer-cli

A headless companion to the player: list devices, render faster than real time, measure throughput, or play from
a terminal. Every processing setting in the interface has an equivalent here.

```bash
dotnet run -c Release --project src/FUPlayer.Cli -- <command> [options]
```

or, after building, `src/FUPlayer.Cli/bin/Release/net9.0/fuplayer-cli.exe`.

## Commands

| Command | What it does |
| --- | --- |
| `devices` | Back-ends, devices, and the rates, formats and DSD modes each one reports |
| `filters` / `dithers` / `modulators` | List the processing presets and their ids |
| `gpus` | OpenCL devices that could convolve a filter |
| `info <file>` | Format, tags and the processing plan for one file |
| `render <files> --out <dir>` | Process to WAV or DSF faster than real time |
| `bench <file>` | Report how many times faster than real time the chain runs |
| `play <files>` | Play through an audio device. Ctrl+C stops |
| `apps` | Applications whose audio can be captured |
| `capture --app <name\|pid>` | Record one application's output to a float WAV |
| `bandwidth <files>` | Where each file's spectrum ends, and whether that looks like a codec |
| `models` | Installed repair models and networks |

### Training a repair model

These four build and check a model for the lossy repair stages. See
[restoration.md](restoration.md) for what they are for.

| Command | Does |
| --- | --- |
| `roundtrip <file> --rate <B/s>` | Code a file with the system AAC encoder and report what it did to each band |
| `dataset <files> --out <file>` | Build coded-against-original training pairs |
| `train-repair <dataset> --out <name>` | Fit the network, reporting against the do-nothing baseline |
| `evaluate <files>` | Measure how much closer the repair gets to the original, on files it never saw |
| `train <files> --cutoff <Hz>` | Fit the simpler linear model, straight from lossless music |

## Processing options

| Option | Meaning |
| --- | --- |
| `--mode pcm\|dsd\|follow` | Output mode. Default `pcm` |
| `--rate <Hz>` | Fixed PCM output rate |
| `--limit <Hz>` | Highest PCM output rate to choose automatically |
| `--dsd 64\|128\|256\|512\|1024` | Highest DSD multiple |
| `--filter <id>` | Resampling filter, from `filters` |
| `--dither <id>` | Dither or noise shaping, from `dithers` |
| `--modulator <id>` | Delta-sigma modulator, from `modulators` |
| `--taps <n>` | Filter length at the output rate. 0 uses the filter's own specification |
| `--staging single\|multi` | One stage for the whole ratio, or a half-band cascade |
| `--convolution auto\|tap` | Frequency domain past the crossover, or direct convolution always |
| `--convolution-from <taps>` | Taps per phase at which the frequency domain takes over. Default 1024 |
| `--convolution-block <ms>` | Longest a frequency-domain stage may hold input. 0 lets it choose |
| `--convolution-layered` | Non-uniform partitioning. Far cheaper at a short block, and no GPU offload |
| `--threads <n>` | Extra DSP threads. 0 is single threaded, default is automatic |
| `--bits <16..32>` | DAC word length |
| `--volume <dB>` | Default 0 for `render` and `bench`, -3 for `play` |
| `--dop` | Send DSD as DoP rather than native |
| `--pass-through` | Send DSD files unchanged when the output rate matches |
| `--remove-ultrasonics` | Low-pass sources above 48 kHz at 20 kHz |
| `--no-limiter` | Leave peaks above full scale alone |
| `--repair-artifacts` | Damp the warbling a low bit rate leaves behind |
| `--repair-rebuild` | Synthesise a band above the codec's cutoff |
| `--repair-predict` | Let a trained network set the levels for both |

## Graphics acceleration

| Option | Meaning |
| --- | --- |
| `--gpu` | Convolve long filters on an OpenCL device |
| `--gpu-device <id>` | Which device, from `gpus`. Default is the most capable |
| `--gpu-fast` | 32-bit arithmetic. Two to three times faster on most cards |
| `--gpu-force` | Use the device even where the processor measured faster |
| `--gpu-share <0..100>` | Fixed percentage of the phases, instead of timing every split |
| `--gpu-hold` | Time the splits once and hold the answer, rather than re-measuring |

`--gpu-share 0` is worth knowing about: it attaches the device and gives it no work, which measures what the
round trip costs on its own.

## Device options

Only used by `play`.

| Option | Meaning |
| --- | --- |
| `--backend wasapi-exclusive\|wasapi-shared\|asio\|null` | Which back-end |
| `--device <id>` | Device id from `devices` |
| `--buffer <ms>` | Device buffer length |

## Examples

Look at a file and what the player would do with it:

```bash
fuplayer-cli info "album/01 track.flac" --rate 705600 --taps 1048576 --staging single
```

Render an album with a million-tap filter, single stage, to 32-bit WAV:

```bash
fuplayer-cli render album/*.flac --out ./rendered --rate 705600 --taps 1048576 --staging single --bits 32
```

Measure the throughput of a DSD256 conversion, with and without the GPU:

```bash
fuplayer-cli bench track.flac --mode dsd --dsd 256
fuplayer-cli bench track.flac --mode dsd --dsd 256 --gpu --gpu-fast
```

Compare the two convolution paths for exactness. Rendered with dither off at 32 bits, the two files should
differ by no more than the file format's own resolution:

```bash
fuplayer-cli render track.flac --out cpu --rate 352800 --taps 262144 --staging single --bits 32 --dither none
fuplayer-cli render track.flac --out gpu --rate 352800 --taps 262144 --staging single --bits 32 --dither none \
  --gpu --gpu-share 100
```

Play through a specific ASIO device:

```bash
fuplayer-cli play album/*.flac --backend asio --device "ASIO MADIface USB" --mode dsd --dsd 256
```
