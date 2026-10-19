# Command line

The Windows ZIP includes **fuplayer-cli.exe**. Open PowerShell in its folder and run
`.\fuplayer-cli.exe help` for the complete current options. The CLI can play, render to files, benchmark,
inspect devices/models and run a UPnP renderer. The processing-only server is a separate executable.

## Useful commands

| Command | Purpose |
| --- | --- |
| `devices` | List audio back-ends, devices and formats |
| `filters`, `dithers`, `modulators` | List preset IDs |
| `gpus` | List CUDA, Vulkan and OpenCL convolution devices |
| `models` | List neural models and Windows neural GPU adapters |
| `priority` | Show requested and granted process priority |
| `info <file>` | Show file tags, format and processing plan |
| `bench <file>` | Measure processing speed without audio output |
| `render <file> --out <folder>` | Process to WAV/DSF |
| `play <file>` | Play audio; Ctrl+C stops |
| `apps`, `capture` | Inspect/capture Windows application audio |
| `upnp` | Run a renderer for a compatible controller |
| `ffmpeg-install` | Build/install optional decoders |

## Examples

List the device IDs and filters before choosing them:

```powershell
.\fuplayer-cli.exe devices
.\fuplayer-cli.exe filters
.\fuplayer-cli.exe gpus
```

Render a file with TAF to 32-bit PCM, with dither off:

```powershell
.\fuplayer-cli.exe render "C:\Music\track.flac" --out .\rendered --rate 192000 --filter transient-aligned --taps 131072 --bits 32 --dither none
```

Benchmark both neural stages on a source explicitly classified as lossy:

```powershell
.\fuplayer-cli.exe bench "C:\Music\track.wav" --rate 96000 --neural-restore --neural-upscale --source-type lossy
```

Use `--gpu` for convolution acceleration, `--gpu-device <id>` from `gpus` for a specific device, and
`--gpu-fast` for 32-bit arithmetic. `--threads <n>` sets extra DSP threads. `--preprocess-files`
prepares future samples from seekable files and compensates leading filter delay.
Neural `--restorer-device` / `--upscaler-device` refer to the adapters listed by `models`, not `gpus`.

## Network DSP

```powershell
.\fuplayer-cli.exe render "C:\Music\track.flac" --out .\rendered --rate 192000 --dsp-server 192.168.1.10 --remote-resampling
```

Use `--dsp-port` for another listener port. `--remote-restorer` and `--remote-upscaler` select those stages;
their normal `--neural-restore` / `--neural-upscale` flags must also enable them. If no remote stage flags
are provided, all three are selected. See [Network DSP](network-dsp.md) for standalone server setup.

For an Internet server, use its domain/URL and key file:

```powershell
.\fuplayer-cli.exe render "C:\Music\track.flac" --out .\rendered --rate 192000 --dsp-server udp://dsp.example.net:58181 --dsp-key-file .\dsp-access.key --remote-resampling
```

Keep the key out of command arguments and shared logs; `--dsp-key-file` reads the private file.
