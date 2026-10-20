# Troubleshooting

| Symptom | What to try |
| --- | --- |
| Playback drops out | Check DSP load and underruns in Now playing. Increase FIFO/device buffering, shorten the filter, lower the output rate or disable optional neural stages. |
| Playback starts or seeks slowly | Long filters and neural stages need preparation. Preprocess files ahead waits before output starts; compare a shorter filter. |
| A GPU appears idle | Check the active chain. Measured sharing can favour the CPU; layered FFT partitions use the CPU. Neural processing and resampling have separate device choices. On a Linux DSP server the upscaler uses the card from v0.6.3; `--devices` lists it. |
| Restorer says idle | Automatic source type normally skips lossless input. Select Lossy only when the recording is actually from a coded source or for a controlled test. |
| Neural Runs on is grey | That stage is selected for Network DSP. Change its network checkbox to return to the saved local adapter. |
| No remote connection | Use compatible player/server versions (v0.6.3 and v0.7.0 use protocol 4), check DNS/address and the access key, and allow/forward the UDP listener port. Inspect the server console/service journal. |
| Remote dropouts | Read **Server load** in Now playing. Above 100 % the server cannot keep up: shorten the filter or use Automatic convolution, as tap by tap costs a multiply-add per tap for every output sample. Below it, check the server's upload and the player's download speed. See the [network guide](network-dsp.md#load-latency-and-bandwidth). |
| Apple Music has no sound or will not connect | See the [streaming guide](streaming.md#if-something-fails); check Firefox installation, direct input frames and output/DSP status. |
| Some music formats are skipped | Install the optional LGPL FFmpeg libraries from Settings. Built-in readers do not cover every codec. |
| Wrong or missing cover | Use embedded art, an album-title image or an explicit name such as cover.jpg. In a folder with multiple albums, use an album-specific filename. |
| Exclusive/ASIO output cannot open | Check the chosen device/rate, close other users of that device, and confirm its driver supports the requested PCM/DSD mode. |

Settings and logs are under `%APPDATA%\FUPlayer`. A separate test installation can use
`FUPlayer.exe --settings-dir C:\FUPlayer-Test` to avoid changing normal settings.
For server services, use `journalctl -u fuplayer-dsp.service` or the per-user equivalent.

When reporting a bug, include the version, Windows/Linux version, device and driver, source format, output
rate, filter/taps, enabled stages, buffer sizes, and the Now playing status/error text. For a network issue
include server version, CPU/GPU and whether the connection is wired. Remove personal file paths from logs
before posting them. Reports can be filed in [GitHub Issues](https://github.com/jerrybeomsoo/FUPlayer/issues).

Known in v0.7.0: changing Stop after this track in the final buffered part of a track can miss the audible
boundary. Native FreeBSD, Linux ARM64 hardware and all possible DAC/driver combinations are unverified.

For a missing Linux service, check the installation scope: `systemctl status fuplayer-dsp.service` shows
a system service; `systemctl --user status fuplayer-dsp.service` shows the current account's user service.
The unit name is `fuplayer-dsp.service`, also available as `fuplayer-dsp-server.service` after installation.
Internet DSP needs compatible protocol 4 versions (v0.6.3 or v0.7.0), a matching key, a direct IPv4 DNS record and UDP forwarding.
An HTTPS website loading successfully does not establish that its UDP port is reachable.
