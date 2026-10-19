# Network DSP

Use another computer for resampling, neural restoration, neural upscaling, or any combination. Select
stages in **Settings → Network DSP**; decoding, volume, limiting, DSD modulation and audio output stay
on the player. **v0.6.3 changed the network protocol: update the player and the server together.** A
0.6.2 player cannot use a 0.6.3 server, nor the other way round; the newer one says so.

## Download and start

Download the standalone **Windows x64** or **Linux x64** server from the
[release](https://github.com/jerrybeomsoo/FUPlayer/releases/tag/v0.6.3). It is one executable containing the
runtime, both models and licence texts. The processing computer needs no player, CLI or audio device.
Normal host OS libraries and GPU drivers are still required; native libraries and models extract to a cache.

For a LAN-only server, run it from PowerShell on Windows:

```powershell
.\FUPlayer-DspServer-0.6.3-win-x64.exe --listen 0.0.0.0
```

Or on Linux:

```bash
chmod +x ./FUPlayer-DspServer-0.6.3-linux-x64
./FUPlayer-DspServer-0.6.3-linux-x64 --listen 0.0.0.0
```

Allow **UDP 58181** through the server firewall. Active workers bind loopback and their traffic is relayed
through this listener. They do not need separate firewall or router forwarding rules.

## Internet access with a domain

1. Point your domain's DNS **A record** to the router's public IPv4 address. Ordinary web reverse proxies
   do not carry this UDP protocol; use a DNS record that resolves directly to the router.
2. Forward external **UDP 58181** to **UDP 58181** on the processing computer. A router rule mapping a wider
   external range to that same internal port also works when the player connects to 58181.
3. Generate a random access key on the server and enable Internet mode. On Linux:

```bash
./FUPlayer-DspServer-0.6.3-linux-x64 --generate-key-file ./dsp-access.key
./FUPlayer-DspServer-0.6.3-linux-x64 --allow-internet --key-file ./dsp-access.key
```

On Windows, use the `.exe` name with the same options. Keep the key file private. The generate command
does not print the key or overwrite an existing key file. Copy its contents securely to each authorised
player's **Server access key** field. Everyone with the key can use that server.

4. In the player, enter a domain such as `dsp.example.net`, or `udp://dsp.example.net:58181`.
   Pasted HTTP/HTTPS root URLs are accepted as a hostname too; DSP still uses UDP. A URL port overrides the
   separate UDP port setting. Paths, query strings and credentials in URLs are rejected.
   Routers with incomplete NAT loopback are supported by selecting a return endpoint only after its
   encrypted reply authenticates for the current session. No private IP address is hardcoded.
5. Paste the access key, turn on **Process on a server**, and select the processing stages.

Public addresses require a key. An Internet-enabled server requires the key from LAN clients too;
there is no automatic fallback to plain UDP. A default LAN-only server needs no key and rejects public peers.
Internet traffic uses authenticated AES-256-GCM UDP with per-session/direction keys, replay checks and
MTU-sized packets. It adds no TLS connection setup. Keep clocks synchronised within two minutes.
This shared-key protocol does not provide forward secrecy; protect and rotate the key if it is disclosed.
The player saves a pasted key in its settings, so treat settings files as private and remove keys before
sharing diagnostics. IPv4 is supported; IPv6-only DNS records are not sufficient.

## Install or update a Linux boot service

Download **install-dsp-server.sh** beside the binary. For a LAN service:

```bash
sudo bash install-dsp-server.sh ./FUPlayer-DspServer-0.6.3-linux-x64
```

For Internet mode, first generate the key as above, then:

```bash
sudo bash install-dsp-server.sh ./FUPlayer-DspServer-0.6.3-linux-x64 --allow-internet --key-file ./dsp-access.key
```

To update an installed Internet service to a new version and keep its key, give it the installed key:

```bash
sudo bash install-dsp-server.sh ./FUPlayer-DspServer-0.6.3-linux-x64 --allow-internet --key-file /etc/fuplayer-dsp/access.key
```

The system service runs as a dedicated account. The binary is installed under `/opt/fuplayer-dsp`, cache
under `/var/cache/fuplayer-dsp`, and a private installed key under `/etc/fuplayer-dsp/access.key`.
Re-running the installer updates its own installation and restarts it. It leaves unrelated units and
firewall rules unchanged. `--dry-run` shows the proposed unit.

```bash
systemctl status fuplayer-dsp.service
sudo systemctl restart fuplayer-dsp.service
sudo journalctl -u fuplayer-dsp.service
sudo systemctl disable --now fuplayer-dsp.service
```

The alias `fuplayer-dsp-server.service` works too. A **user service is a different scope**: installing
with `--user` uses `~/.local/lib/fuplayer-dsp`, `~/.cache/fuplayer-dsp` and `~/.config/fuplayer-dsp/access.key`.
Inspect it with `systemctl --user status fuplayer-dsp.service` and `journalctl --user -u fuplayer-dsp.service`.
Boot startup requires systemd lingering; the installer prints the command when needed.

When moving from a user service to a system service, first stop/disable the old user unit with
`systemctl --user disable --now fuplayer-dsp.service`, then install the system service. Two instances cannot
share the listener port. Normal `systemctl status` and `sudo systemctl status` inspect the system scope,
and cannot show a unit installed only in another user's service manager.

## What runs where

Remote checkboxes choose where an enabled stage runs. Enable neural stages in DSP studio too; Automatic
source type normally leaves the restorer idle on lossless files. A stage between two selected ones runs on
the server as well: with the restorer and resampling selected, the upscaler and the exciter go there too,
since the audio crosses the network once each way. A remote stage's local **Runs on** selector becomes grey
and its previous local choice is retained.

The server runs the same processing code as the player, so the output is the player's own: identical to
the bit without the neural networks, and within a few steps of a 32-bit sample with them. Output delta,
ultrasonic removal and every stage combination work remotely. Final DSD modulation still uses the player's
CPU, so higher DSD rates can need a faster client even when the server has spare resources.

## Load, latency and bandwidth

The player keeps several hundred milliseconds of audio in flight to the server, so the server never waits
for the network between blocks and a slow Internet connection costs latency, not throughput. In Now playing,
**DSP load** is this computer's work, **Server load** is the server's processing time relative to the music,
and **Network round trip** is the UDP echo time. The DSP figure in the top bar is the higher of the two loads.
Above 100 % server load the server cannot keep up, whatever the network does, and the player says so.

Tap-by-tap convolution costs one multiply-add per tap for every output sample. For DSD256 of the 48 kHz
family the filter runs at 768 kHz, so 262,144 taps is 50 thousand million multiply-adds a second for two
channels. Measured on an i5-8500 with a Radeon RX 6400, that plays at about 92 % server load; 524,288 taps
needs about 155 % and cannot play. **Automatic convolution** gives the same samples to within rounding at a
small fraction of the arithmetic, and is the choice for long filters.

For Internet playback, start with **Fast FIFO off** and a **500 ms FIFO**; the player raises the FIFO to cover
what it keeps in flight. The network carries 64-bit audio. A stereo 768 kHz intermediate stream, used by the
48 kHz DSD rate family, needs about **98 Mbps of sample payload** before headers and recovery traffic. Check
the server's upload as well as the client's download speed, and leave headroom for jitter.

## Devices on the server

CUDA is preferred for NVIDIA convolution and Vulkan for other GPUs, with measured CPU/GPU sharing. The neural
upscaler runs on a graphics card by default: DirectML on Windows, and on Linux ONNX Runtime's WebGPU provider
through Vulkan (for example an AMD card with the Mesa RADV driver). Its first call is checked against the
processor's; a card that disagrees is not used. `--upscaler-device cpu` keeps it on the processor. The
restorer stays on the processor by default, where its small calls run faster; `--restorer-device auto` moves
it to the card. `--devices` lists the convolution devices and the neural adapters.

Stop, Pause or disconnection releases active workers and their GPU/model allocations. The small listener
remains ready for another client. Resuming initialises processing and fills the buffer again.

Use `--help`, `--version`, `--devices`, `--licenses` or `--extract-licenses <folder>` for server information.
Overrides include `--cpu`, `--threads`, `--gpu-device`, `--gpu-fast`, `--port`, `--models` and `--cache`.
Worker port options still select the internal loopback range. Native FreeBSD and Linux ARM64 hardware
remain unverified. See [troubleshooting](troubleshooting.md) for connection and playback problems.
