# Network DSP

Use another computer on your trusted LAN for resampling, neural restoration, neural upscaling, or any
combination. The player keeps decoding, the exciter, volume, limiting, DSD modulation and audio output.
Both computers should use **v0.6.0**; older development server protocols are incompatible.

## Download and start a server

Download the standalone server for **Windows x64** or **Linux x64** from the
[v0.6.0 release](https://github.com/jerrybeomsoo/FUPlayer/releases/tag/v0.6.0).
It is one portable executable containing the runtime, both neural models and licence texts. The processing
computer does not need FUPlayer, its CLI, an audio device or a separate .NET installation.
Normal OS libraries and GPU drivers are still needed. Native libraries/models extract to a writable cache.
Linux ARM64 and native FreeBSD are not verified release targets.

On Windows, open PowerShell in the download folder:

```powershell
.\FUPlayer-DspServer-0.6.0-win-x64.exe --listen 0.0.0.0
```

On Linux:

```bash
chmod +x ./FUPlayer-DspServer-0.6.0-linux-x64
./FUPlayer-DspServer-0.6.0-linux-x64 --listen 0.0.0.0
```

Allow **UDP 58181–58213** from your player computer through the server's LAN firewall.
58181 is the listener; 58182–58213 are allocated to active workers. The installer does not change firewall rules.

## Start automatically on Linux

Download **install-dsp-server.sh** beside the Linux binary, then run:

```bash
sudo bash install-dsp-server.sh ./FUPlayer-DspServer-0.6.0-linux-x64
```

This enables and starts `fuplayer-dsp.service` with a dedicated account. The binary goes under
`/opt/fuplayer-dsp`, and its cache under `/var/cache/fuplayer-dsp`.
Re-running the installer upgrades its own installation and restarts it. Existing unrelated units are left alone.

For a per-user installation, use `bash install-dsp-server.sh ./FUPlayer-DspServer-0.6.0-linux-x64 --user`.
Boot startup for that account needs systemd lingering; the installer prints the required command if needed.
Use `--dry-run` to inspect the proposed unit. Management commands for a system installation are:

```bash
sudo systemctl status fuplayer-dsp.service
sudo journalctl -u fuplayer-dsp.service
sudo systemctl restart fuplayer-dsp.service
sudo systemctl disable --now fuplayer-dsp.service
```

For a user installation use `systemctl --user` / `journalctl --user` without sudo.

## Connect the player

1. Open **Settings → Network DSP**.
2. Enter the server's private IPv4 address and port **58181**.
3. Turn on **Process on a server**.
4. Select **Resampling on server**, **Neural restorer on server**, and/or **Neural upscaler on server**.
5. Enable the corresponding stages in DSP studio and start playback.

A checkbox chooses where a stage runs; it does not enable a disabled neural stage. Automatic source type
can leave the restorer idle on lossless files. A remote neural stage's local **Runs on** control is grey.
Its previous local selection returns when you switch back to local processing.

## Performance and idle behaviour

Prefer wired Ethernet. Raw float64 audio uses unencrypted UDP with loss recovery and no bandwidth cap.
Now playing's network round trip time measures the connection, separately from DSP calculation and audio
buffer/filter delay. High sample rates and several remote stages require more bandwidth.
Increase the playback FIFO if short scheduling or network delays cause dropouts.

The server uses channel workers, bounded neural threading and measured CPU/GPU sharing. Automatic convolution
uses FFT processing for long filters; explicitly choosing tap-by-tap can remain expensive.
CUDA is preferred for NVIDIA convolution, Vulkan for other GPUs. Linux neural inference uses the CPU;
Windows neural adapter options use DirectML. A low GPU percentage alone does not indicate a bottleneck.

Stop, Pause or disconnection releases the active worker and its GPU/model allocations. The small listener
remains ready for a new client; an abruptly lost client is cleaned up after a timeout. Resuming has to
initialise processing and refill the buffer again. The idle listener uses some RAM, rather than zero RAM.

## Options and connection scope

Use `--help`, `--version`, `--devices`, `--licenses` or `--extract-licenses <folder>` on the server.
Useful overrides include `--cpu`, `--threads`, `--gpu-device`, `--gpu-fast`, `--port`, `--models` and `--cache`.
With custom ports, configure both the listener and worker range in the server/firewall.

This protocol accepts private/link-local/loopback IPv4 peers. **It has no authentication or encryption**,
so use it only on a trusted LAN and do not forward its ports to the Internet.
The player reports a failed exchange instead of substituting lost audio. For errors, check the service log
and [troubleshooting](troubleshooting.md). No JSON connection profile or certificate is needed.
