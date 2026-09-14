# Architecture

## Projects

| Project | Depends on | Contents |
| --- | --- | --- |
| `FUPlayer.Core` | TagLib# | Playback engine, DSP, decoders, library, settings. No UI, no platform calls |
| `FUPlayer.Audio.Windows` | Core | WASAPI exclusive and shared, ASIO including native DSD and DoP |
| `FUPlayer.App` | Core, Audio.Windows, Avalonia | The desktop interface |
| `FUPlayer.Cli` | Core, Audio.Windows | Headless tool for devices, rendering and benchmarks |

`FUPlayer.Core` has no Windows-specific code. A macOS or Linux port needs a new back-end project implementing
`IAudioBackend` and nothing else.

## The engine

`PlaybackEngine` owns one thread that decodes, processes and writes. It is a straight loop rather than a task
graph, because the work is a pipeline with one producer and one consumer and the ordering must be exact.

```
PlaybackEngine
 |
 +- OutputPlanner   decides the output format for a source: rate, bits, DSD multiple, filter, dither
 |
 +- DspPipeline     per-block processing for every channel
 |    |
 |    +- ResamplerChain (one per channel)      polyphase stages
 |    +- SmoothedGain, SoftLimiter, DelayLine  volume, limiting, speaker trim
 |    +- PcmQuantizer or DeltaSigmaModulator   output stage
 |    +- MeterHub                              level meters, spectra
 |
 +- SpscByteRing    lock-free FIFO between the engine thread and the device thread
 |
 +- IAudioBackend   WASAPI, ASIO, file or null
```

### Threading

- **Engine thread**: decode, drive the pipeline, write to the FIFO.
- **Worker pool** (`ParallelWorkers`): one task per channel for the whole per-block chain.
- **Within a channel**: a partitioned filter spreads its polyphase phases with `Parallel.For`. The degree of
  parallelism is the thread budget divided by the channel count, widened to the whole budget when a GPU is
  holding whole channels, and narrowed again if the GPU is released mid-stream.
- **Device thread**: owned by the driver. It only ever reads from the FIFO.
- **UI thread**: reads a `PlaybackStatus` snapshot on a 33 ms timer. It never touches engine state directly.

The FIFO is a single-producer single-consumer ring of bytes with a power-of-two capacity, so the wrap is a mask
and neither side takes a lock.

## The DSP chain

`ResamplerFactory.Create` turns a filter preset, an input rate and an output rate into a `ResamplerChain` of
stages. The interesting decisions all happen there:

- Whether to use one stage for the whole ratio or a half-band cascade.
- Whether a stage convolves directly (`PolyphaseStage`) or in the frequency domain (`FftPolyphaseStage`).
- Whether a frequency-domain stage uses one FFT size for all taps or several (`LayeredFftPolyphaseStage`).
- How long the filter should be at this stage's rate, which is not the same as at the output rate when a
  cascade or a bridge stage finishes the conversion.

Chains are cached, keyed by every input to the decision, so changing the volume does not redesign a
million-tap filter.

### Key types

| Type | Role |
| --- | --- |
| `PolyphaseStage` | Direct convolution. One multiply-add per tap per output sample |
| `FftPolyphaseStage` | Uniformly partitioned overlap-save FFT. Holds one block of input |
| `LayeredFftPolyphaseStage` | Non-uniform partitioning: short FFTs for early taps, longer ones behind |
| `PartitionPlan` | Dynamic program that picks the band layout against a cost model |
| `RealFftPlan` | Real-input FFT: a complex transform of half the length plus an untangling pass |
| `FirKernel` | The direct convolution inner loop, vectorised |
| `FirDesign` | Windowed-sinc design, minimum-phase and intermediate-phase transforms |
| `DeltaSigmaModulator` | 1-bit modulation with the noise transfer functions in `ModulatorCatalog` |
| `PcmQuantizer` | Dither and noise shaping from `DitherCatalog` |

## Graphics acceleration

`FUPlayer.Core/Dsp/Acceleration` is a thin OpenCL layer with no external dependency: `OpenClApi` binds the
handful of entry points needed through `NativeLibrary`, and the kernels are a string in `GpuKernels`.

| Type | Role |
| --- | --- |
| `GpuRuntime` | Finds devices, compiles the program once per device and precision |
| `GpuPolyphaseFilter` | Uploads a filter's partition spectra, then verifies the device against the CPU |
| `GpuConvolver` | Per-channel device state for the frequency-domain path |
| `GpuDirectConvolver` | The same for direct convolution |
| `SplitConvolver` | Runs both sides on the same block: device started, CPU works, device collected |
| `PhaseBalance` | Decides how many phases the device gets, by timing every split during playback |

Nothing is offloaded until the device has reproduced the processor's own output for that filter, to 1e-10
relative in 64-bit and 1e-5 in 32-bit. The split is then measured rather than assumed, first on the filter's own
blocks and then against whole pipeline blocks with the device left out entirely.

## The interface

Avalonia with compiled bindings, MVVM through `CommunityToolkit.Mvvm`. Pages are created once and kept alive
with their visibility toggled, so the spectrogram history and scroll positions survive navigation.

`PlayerServices` is the composition root: settings, the audio back-end registry, the engine, the library and the
cover cache. Setting changes are debounced for 400 ms before they reach `PlaybackEngine.ApplySettings`, which
decides whether the change needs the stream restarted or can be applied to the running pipeline.

Custom drawing lives in `Controls`: `ResponsePlot`, `SpectrumView`, `SpectrogramView`, `WaterfallView`,
`LevelMeterBar`, `SeekBar` and `VolumeKnob` are all direct `Render` implementations rather than composed
elements, because they redraw at 30 frames a second.

## Settings

`PlayerSettings` is a plain object tree serialized to `settings.json`. It carries a version number;
`SettingsMigration` upgrades older files when the layout changes, and `SettingsStore` clamps values that would
be out of range. Renaming a setting means adding a migration entry and bumping `PlayerSettings.CurrentVersion`.
