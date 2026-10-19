# Settings and output

## Audio output

On **Output**, select the back-end and device before choosing the sample rate.
WASAPI shared follows Windows' shared audio path. WASAPI exclusive and ASIO require the device to be available
and to accept the selected format. ASIO and native DSD support depend on the manufacturer's driver.
Use PCM initially, then try higher rates or DSD after ordinary playback works.

Larger device buffers and an adequate playback FIFO give processing more time to meet each deadline.
For Network DSP, the FIFO value is a minimum: WAN batches may require a larger effective buffer.
See [network latency and buffering](network-dsp.md#processing-latency-and-idle-use).
Smaller buffers respond faster but can increase dropouts. A very long filter can add seconds of delay even
when its DSP load is low. See [DSP studio](dsp.md) for the different delay sources.

## Processing and GPU

Choose filters and neural stages in **DSP studio**. Settings contains the local GPU selection, arithmetic
precision and DSP thread controls. Automatic GPU selection prefers CUDA on NVIDIA and Vulkan on other GPUs.
Select an OpenCL device explicitly if you need that path. CPU/GPU sharing is measured, so a GPU may be idle
when the processor finishes a particular filter faster. A layered FFT partition layout uses the processor.

32-bit GPU arithmetic usually uses less bandwidth and can run faster, while 64-bit retains more numerical
precision. The active chain in Now playing reports the device and numerical verification. Neural **Runs on**
choices are separate: Windows neural GPU processing uses DirectML, independently of the resampler's API.

**Preprocess files ahead** reads future samples from seekable files and compensates leading filter delay.
Starting or seeking waits for that preparation. Live streams retain their delay, and processing must still
keep up with playback. This does not change filter phase or ringing.

## Network DSP

In **Settings → Network DSP**, enter the server's IP address, domain or URL and UDP port, turn on **Process on a
server**, then select the resampling/restorer/upscaler checkboxes. The corresponding stage must also be
enabled in DSP studio. Its local neural **Runs on** selector becomes grey while remote processing is selected.
For Internet access, paste the server's key in **Server access key**. It is masked on screen and saved in
settings; keep that file private. Leave it empty only for a LAN-only server. See [Network DSP](network-dsp.md)
for single-port forwarding, key generation and service setup.

## Language, library and optional decoders

Choose **Same as Windows**, **English** or **한국어** for the interface; restart when requested.
Add music folders in Library. The player saves settings and caches under `%APPDATA%\FUPlayer`.
For additional codecs, open **Settings → FFmpeg** and follow the [decoder guide](building-ffmpeg.md).

## Process priority

The Windows player and CLI request real-time process priority when launched. Windows may grant a lower
priority, such as High, according to the account's privileges. `fuplayer-cli.exe priority` reports the request
and result. Priority cannot compensate for a processing chain that takes longer than the available buffer.
