# Changelog

## 0.6.3 — 2026-10-19

### Added

- **The neural upscaler on a Linux server's graphics card.** The standalone Linux server runs it through ONNX
  Runtime's WebGPU provider on Vulkan, on the first card of its own by default; its first call is checked
  against the processor's. On a Radeon RX 6400 a call took 6.7 ms against 11.4 ms on three i5-8500 cores.
  `--upscaler-device cpu` keeps it on the processor. [Network DSP guide](https://github.com/jerrybeomsoo/FUPlayer/blob/v0.6.3/docs/network-dsp.md#devices-on-the-server).
- **Server load in Now playing.** The server's processing time relative to the music, beside the round trip;
  the top bar shows the higher of the two machines' loads, and the overload warning names the server when it
  is the one that cannot keep up.
- **`--dsd-48k` in the CLI**, for the 48 kHz DSD rate family. [Command line](https://github.com/jerrybeomsoo/FUPlayer/blob/v0.6.3/docs/cli.md).

### Changed

- **A new network protocol (version 4).** The player keeps several hundred milliseconds of audio in flight, so
  the server never waits for the network between blocks; packets are acknowledged in bulk, recovered selectively
  and paced. Through a 26 ms, 0.1 %-loss link a chain takes as long as on the LAN. **Update the player and the
  server together**; a mismatch is reported by both.
- **The server runs the player's own processing code.** Every combination of remote stages, the output delta
  included, gives the player's output: identical to the bit without the neural networks.
- **Faster tap-by-tap convolution.** Fused multiply-adds and cache-sized coefficient tiles (on an i7-7820HQ,
  35 to 51 thousand million multiply-adds a second on eight threads), and a Nyquist filter's centre phase is a
  copy rather than a convolution, the same bits for an eighth less work at ×8.
- **Meters and analyzer follow what is heard.** Levels and spectra reach Now playing when the device plays them,
  not when they are computed, which was up to a FIFO ahead and, with a server, in bursts.

### Fixed

- **Stutter at 262,144 taps tap by tap over the Internet.** DSD256 of the 48 kHz family with both neural stages
  and the exciter now plays without underruns through a 26 ms link (server load about 83 %), where 0.6.2 ran at
  0.58× real time. 524,288 taps tap by tap is beyond that server (137 % load); Automatic convolution gives the same
  samples to within rounding for a fraction of the work.
- **A slow, coarse spectrogram and a jumping level meter** with a DSP server: both were fed in network bursts.
- **"GetElementType is not implemented" in the server log** at every change of settings: a worker no longer exits
  under a running network call.

### Compatibility and validation

- Player and server must both be **0.6.3**. Access keys, addresses, the single forwarded UDP port and the stage
  checkboxes are unchanged; a stage between two selected ones now runs on the server too.
- Measured on the real server (i5-8500, RX 6400) with real models and a clocked null output, 60 s per case, at
  131,072, 262,144 and 524,288 taps, on the LAN and through a simulated 26 ms link with 0.1 % loss. Not an
  outside-ISP, DAC or listening test.

## 0.6.2 — 2026-10-19

### Added

- **Combined remote processing.** Eligible stereo sessions can run restoration, neural upscaling,
  the optional exciter and resampling in one server transfer. Now playing identifies remote exciter
  execution. [Network DSP guide](https://github.com/jerrybeomsoo/FUPlayer/blob/v0.6.2/docs/network-dsp.md).

### Changed

- **Internet buffering adapts to RTT.** Larger bounded audio batches and a prefetch worker overlap
  remote processing with local output. The selected FIFO is a floor; a 500 ms setting can become about
  1.7 seconds effectively on a roughly 26 ms path. Low-RTT LAN requests remain short.
- **Automatic server FFT processing.** WAN sessions can use their existing buffering budget for FFT
  blocks, avoiding unnecessary short partitions. Large response copies are removed. Explicit
  convolution choices remain, and final DSD modulation stays on the client.
- **DSP load explanation.** The meter includes Network DSP waiting time and is distinct from CPU/GPU
  utilisation. Now playing's tooltip explains this.

### Fixed

- **Internet stuttering with both neural stages and exciter.** Tiny sequential round trips no longer
  dominate eligible sessions. Selective repair defers whole-request retries, duplicate requests do not
  replay entire audio bursts, and large responses use bounded progress windows with loss recovery.
- **Buffered track transitions and cancellation.** Pending remote audio is drained before track
  boundaries and flushing; Stop/Pause releases prefetch work and senders waiting for progress.

### Compatibility and validation

- Update both player and server to **v0.6.2** for all improvements. Existing access keys, stage
  checkboxes, DNS addresses and the single external UDP port continue to work. Audio remains float64;
  Internet transport keeps authenticated encryption without TLS.
- Controlled testing used 24-bit/48 kHz stereo, TAF 131,072 taps, both real neural models, exciter,
  26 ms added RTT, 300 Mbps per direction and 0.1% packet loss. The 48 kHz DSD family had zero underruns
  at DSD64 for two minutes and DSD128/256/512/1024 for one minute each. All five standard rates passed
  shorter checks too. The installed Linux system service passed another two-minute DSD64 run and released
  its workers afterwards. These are clocked null-output tests, not an outside-ISP or physical-DAC guarantee.
- **587 Windows tests and 87 focused Linux tests pass.** The late Stop after this track boundary issue
  remains; native FreeBSD/ARM64 deployment is still unverified.

## 0.6.1 — 2026-10-19

### Added

- **Internet DSP through DNS or URLs.** Enter an IP address, domain, or `udp://host:port` in Settings.
  Public connections use a server access key and authenticated AES-256-GCM UDP, without TLS. LAN-only
  servers can retain plain UDP. The new masked key field and CLI `--dsp-key-file` configure authorised clients.
  [Setup guide](https://github.com/jerrybeomsoo/FUPlayer/blob/v0.6.1/docs/network-dsp.md).
- **Server key management.** `--generate-key-file` creates a random private key without printing it;
  `--allow-internet --key-file` enables Internet access. Packet replay checks, per-session/direction keys,
  bounded queues and authentication before worker allocation protect the processing path.

### Fixed

- **One external UDP port is sufficient.** Isolated workers bind loopback; the listener relays their traffic
  through its own port. Forward UDP 58181 to internal UDP 58181. This also supports routers mapping a wider
  external range to that single internal port. Clients no longer need separately reachable worker ports.
- **Internet retry timing.** Encrypted sessions adapt recovery intervals to longer RTTs while retaining
  MTU-sized packets and selective recovery. Invalid keys do not allocate DSP workers.
- **Linux service setup and discovery.** The installer supports private key installation, Internet options
  and the `fuplayer-dsp-server.service` alias. The guide distinguishes system services from `--user` services
  and explains migration between them.

### Compatibility

- Update both player and server to v0.6.1 for Internet mode. The shared key is required from every client of
  an Internet-enabled listener, including clients on its LAN. Plain LAN-only mode remains available.
- The access key is saved in the player's settings. Keep those files private. This protocol has no forward
  secrecy; use a fresh key if one is disclosed. IPv4 and a direct UDP-capable DNS address are required.
- The late Stop after this track boundary issue remains. No new native FreeBSD/ARM64 or hardware audio claim.

## 0.6.0 — 2026-10-19

### Added

- **Network DSP with a separate portable server.** Send resampling, the neural restorer, the neural upscaler,
  or any combination to a Windows/Linux x64 computer. Choose stages with checkboxes in Settings. The single
  server executable includes its runtime and models; it releases active workers when playback stops or pauses.
  A Linux systemd installer enables boot startup. Now playing shows network round trip time.
  The UDP protocol is for trusted private LANs and has no encryption or authentication.
  [Network setup](https://github.com/jerrybeomsoo/FUPlayer/blob/v0.6.0/docs/network-dsp.md).
- **CUDA and Vulkan convolution.** Automatic selection prefers CUDA on NVIDIA and Vulkan on other GPUs;
  OpenCL remains selectable. Numerical verification and measured CPU/GPU sharing remain active.
  Neural GPU processing on Windows continues to use DirectML; Linux neural processing uses the CPU.
- **Preprocess files ahead** prepares seekable files before output and compensates leading filter delay.
  Startup and seeking wait for that work; live input retains its delay.
- **Windows process priority request.** Player and CLI request real-time priority at launch. Windows can
  grant a lower priority according to account privileges; `fuplayer-cli priority` reports the result.

### Fixed

- **TAF graphs and precision information.** Passband includes the real transition peak and -6.02 dB cutoff;
  decimation responses, marker placement and compute op/s accounting are corrected. The sinc precision preview
  distinguishes 32-bit coefficient storage from the design percentage and actual GPU execution verification.
- **Remote processing dropouts.** Channel workers, bounded neural threading, Automatic FFT selection and
  UDP acknowledgement/recovery avoid serial work and repeated audio bursts during computation. A controlled
  120-second 768 kHz test with TAF and both networks had zero underrun frames; results depend on your LAN and hardware.
- **Remote neural controls.** Restorer/upscaler local Runs on choices are grey while their stage is selected remotely.
- **Album art.** Unrelated images are no longer chosen just because they share a music folder. Named covers
  and album-title matches are recognised, mixed-album folders keep their own artwork, and cached thumbnail
  selection is refreshed. Folder images are enumerated once when regrouping cached albums.

### Packages and guides

- Download **FUPlayer-0.6.0-win-x64.zip** for the desktop player and CLI, or one of the two standalone
  **FUPlayer-DspServer** executables for a processing computer. **install-dsp-server.sh** is optional Linux
  service setup; **SHA256SUMS.txt** covers all four downloads. The Windows ZIP includes both neural models,
  licences and offline user guides. FFmpeg libraries and DirectML.dll are not bundled.
- Public documentation now focuses on setup, playback and troubleshooting. Development plans and validation
  reports remain in the development repository.

### Known limitations

- Late changes to Stop after this track can still miss the audible boundary in already-buffered audio.
- Linux ARM64 and native FreeBSD are unverified. This release provides a Windows player and Windows/Linux x64 servers.
- Client and server must both use this protocol version; update older development server builds together.

## 0.5.1 — 2026-10-18

### Added

- **Tracks page under Library** for finding individual songs. Every search word must match the title, artist,
  album, album artist, genre or file name, ignoring case, Japanese kana type and character width. Rows show covers
  and ratings read from FLAC/Ogg and ID3 tags, with sorting by title, artist, album, rating or recently changed.
  Double-click or Enter plays that track alone; the menu also plays the results from a selected track, plays next,
  queues or shows the file ([guide](docs/library.md#tracks), [PR #3](https://github.com/jerrybeomsoo/FUPlayer/pull/3)).
- **Stop after this track**, a transport toggle beside repeat, saved between desktop runs and remaining enabled
  after stopping. It stops automatic advancement while keeping explicit Next available
  ([guide](docs/library.md#stop-after-this-track), [PR #2](https://github.com/jerrybeomsoo/FUPlayer/pull/2)).

### Fixed

- **A flat music folder can show its separate albums.** Tracks are grouped by album title and album artist rather
  than folder alone; different track artists do not split a compilation. Old caches are regrouped without a rescan,
  and albums sharing a folder use their embedded covers
  ([guide](docs/library.md#albums), [PR #1](https://github.com/jerrybeomsoo/FUPlayer/pull/1)).

### Known limitations

- **Large flat-folder caches load more slowly.** A synthetic cache with 4,683 entries and 1,146 albums took 7.7 to
  8.8 seconds to load, versus 0.04 to 0.14 seconds before regrouping, because folder-cover discovery is repeated.
- **Late stop-toggle changes can miss the audible boundary.** Enabling in the final buffered part can let the next
  track play, and disabling then can still stop. The desktop restores the saved mode; engine consumers using only
  the settings constructor or `ApplySettings` must set the live property separately
  ([details](docs/library.md#known-limitations)).

These three changes were contributed by
[horseman777777-sketch](https://github.com/horseman777777-sketch).

## 0.5.0 — 2026-10-01

### Added

- **The Transient-Aligned Filter**, a new resampling filter (`transient-aligned`). It keeps the ideal reconstruction
  filter, the sinc, exactly as it is over most of its length, and tapers only its two ends, just smoothly enough for
  150 dB of image rejection from a thousandth of the Nyquist frequency past it. It is cut off at the source's
  Nyquist frequency, so the original samples come out bit for bit. It runs in its own stages: 16 times the source
  rate, then a short stage of the same kind up to 256 times. The automatic length is 0.92 s (647,217 taps at 16
  times), half of it the sinc's own; at 1,015,809 taps 68 % is, at 33,554,433 taps 99 %. It is FUPlayer's own design
  of the idea behind Rob Watts' WTA filter. Its page sets out what is published about that filter, checks an AI
  model's claims about it against the sources, and gives the measurements ([docs](docs/transient-aligned-filter.md)).
- **The neural restorer and upscaler can run on a graphics adapter** (DSP studio › Lossy repair › Runs on,
  `--restorer-device`, `--upscaler-device`; `models` lists the adapters). They use DirectML, through ONNX Runtime's
  DirectML build and the DirectML.dll that Windows has; nothing proprietary ships. On a Quadro M2200 the upscaler ran
  10.7 times real time against 6.9 on the processor, and the restorer 6.4 against 5.4. Each network has its own
  setting, an adapter that is missing or cannot open it leaves it on the processor, and Now playing says where it
  runs ([docs](docs/neural-restorer.md#on-a-graphics-adapter)).
- **A mel frequency scale** for the analyzer in Now playing, beside the logarithmic and linear ones.

### Fixed

- **The filter view in DSP studio no longer loads for minutes at long lengths.** It used to stream an impulse
  through the whole conversion, sized in the wrong unit: 33,554,432 taps from 44.1 to 48 kHz took about a quarter of
  an hour. The response is now read from the filter's own coefficients: 0.8 s for that case, 3.6 s for 16 times at
  the same length. An analysis no longer wanted is stopped, and the filter cache keeps at most 1 GB instead of up to
  twelve filters of half a gigabyte each.
- **Cost per channel** counted frequency-domain stages in floating-point operations and the rest in multiply-adds;
  every stage now counts multiply-adds, so frequency-domain figures are about half what they were and comparable
  with the rest, and large values read "1.48 T ops/s" instead of "1479.8 G ops/s".
- **The magnitude plot** of a long filter showed one frequency bin per point, often far below the worst stopband
  sidelobe near it. It now shows the quietest and the loudest bin around each point.

## 0.4.2 — 2026-09-29

### Changed

- **The oversampling exciter runs at any output rate.** At an output at or below the source rate, such as 48 kHz for
  a 48 kHz stream, it now runs at the source rate instead of standing idle (after the restorer, or for a lossless
  source) or taking the signal to twice the rate and back through the chosen filter. Above it, it runs at twice the
  source rate as before.
- **Its harmonics start at about 15 kHz and fade in.** From three quarters of where the music's spectrum begins to
  fall, held between 12 and 18 kHz (14.4 kHz for AAC at 256 kbit/s), they rise under the music evenly in decibels,
  from 50 dB under it to 6 dB under it at that edge, and stay 6 dB under the restorer's or the upscaler's band. On a
  spectrogram of the output delta they rise out of the floor instead of starting at a wall; the level under the edge
  moves 0.02 dB on average. The part of them that would only have raised the music's level is taken out first.
- **Above a codec's edge the fill rises in across its roll-off** to the line it filled to before, and tops up bins the
  music half fills. On 27 held-out coded stretches the band above the edge is now 0.2 dB from the masters (0.4.1:
  1.4 dB over), and after the restorer 0.0 dB instead of 0.9 dB under. Above Nyquist nothing changed against hi-res
  masters ([docs](docs/exciter.md)).
- **Where the music ends is followed as it plays:** measured over the last three seconds instead of the whole track and
  glided to over half a second, so the band no longer jumps when the estimate firms up, and it follows a live stream
  that changes source. It starts at once instead of waiting for the first measurement.

## 0.4.1 — 2026-09-29

### Changed

- **The oversampling exciter is a stage of its own**, no longer part of the neural restorer, and off until asked
  for (DSP studio › Lossy repair, `--exciter`). It now runs at twice the source rate, so the harmonics it makes from
  the music above 7 kHz reach past the source's Nyquist frequency, up to 44.1 or 48 kHz, and show in the analyzer's
  processed output. Their level is the music's own slope continued 12 dB an octave steeper, which is where hi-res
  masters lie: on 40 of them from the library, brought to CD rate and excited, +0.5 to +1.5 dB from the masters
  above Nyquist on average. It adds only what is missing under that line, so the music's own band and the
  restorer's and upscaler's bands are left alone. PCM sources up to 48 kHz; about 25 ms
  ([docs](docs/exciter.md)). `--no-exciter` is still accepted and changes nothing.
- **A new neural restorer**, fitted with Nuttall's window, which the player takes from the model's description. On
  the held-out songs, against the model it replaces: closer to the masters above 12 kHz for every codec family
  (log-spectral distance 7.75 → 7.61 dB), the mixed level above 16 kHz −0.88 → −0.67 dB, mid and side 0.93 → 0.86
  and 1.20 → 1.09 dB, frames over the masking threshold 0.61 → 0.47 % (Vorbis 1.22 → 0.22 %). A lossless file forced
  through it changes a little more, −62.0 → −54.3 dB ([docs](docs/neural-restorer.md),
  [restorer guide](docs/neural-restorer.md)).
- The Korean interface's descriptions and glossary are shorter.

### Fixed

- Turning the exciter on or off now takes effect at once rather than at the next track.

## 0.4.0 — 2026-09-29

### Added

- **Oversampling exciter** after the neural restorer, on unless turned off. The restorer fills the band above a
  codec's edge at the master's level but with a fine structure of its own: a violin's partials stopped at the edge.
  The exciter continues them: the octave below the edge goes through a second- and third-degree polynomial at four
  times the rate, whose products of one note's partials lie on that note's own series, and it is analysed there, so
  nothing folds back below Nyquist. In each twelfth of an octave above the edge the harmonics take the restorer's
  level, read through a window that keeps the louder band below the edge out of the reading, and a share of its
  band: the more of the octave below the edge is in partials, the larger the share. Below the edge nothing changes,
  and a partial the restorer's output already has above it is left alone. The edge comes from the codec detector
  and everything else from the music; there is nothing to set. On held-out songs with partials above the edge, the
  fine-structure correlation with the masters there went from 0.00 to 0.04 and the share of their partials matched
  within a bin from 37 to 41 %, the level within 0.2 dB. 26 to 28 ms of delay, a few per cent of one core.
  `--no-exciter` leaves it out ([docs](docs/neural-restorer.md#the-oversampling-exciter)).

- **Korean interface** (한국어). Every page, message, filter, dither and modulator description, and the notes
  the planner and engine write, in English or Korean. The player follows Windows' language when there is a
  translation for it; *Settings › Language* changes it and offers to restart. Terms without a settled Korean word
  keep their usual name and are explained where they appear.
- **Glossary**, a page under Settings: 58 terms explained in plain words, in either language, with a search box.
  The DSP studio and Output pages link to it.

### Changed

- A restorer's description can name the window its frames were cut with (`window_coefficients`); the player uses
  it, and a restorer that names none is the Hann one it always was. The training scripts can train with Nuttall's
  window or a trainable cosine sum (`--window`).

### Fixed

- The restorer's first call of the network, a few hundred milliseconds while ONNX Runtime prepared itself, fell
  after playback had begun and could empty the output buffer; the restorer and the exciter now run once on silence
  when they are set up.
- The Output page's capability labels had a fixed width that a longer label ran past.

## 0.3.0 — 2026-09-28

### Added

- **UPnP input.** The player can be a UPnP / DLNA renderer that foobar2000's UPnP output and other controllers
  play to: FLAC, WAV (RF64 too, and streams of unknown length) and LPCM over HTTP, through the whole processing
  chain, with the controller's pause, stop, volume and mute. A new *UPnP input* page under Live input turns it on
  and shows what arrives. *Add to foobar2000* puts the player in foobar2000's list of renderers, so it sends FLAC
  and pauses rather than stopping; with **Bits** set to 24 on foobar2000's *Output › Devices* page a 24-bit file
  arrives as 24-bit FLAC ([docs](docs/upnp-input.md)). `fuplayer-cli upnp` is the same renderer without the
  window. Off until turned on.
- **Now playing for live sources.** A captured application or a UPnP stream shows what it is playing: the title,
  artist and cover from the application's Windows media session (foobar2000, Spotify and the browsers publish
  one), then the stream's own metadata, then the window title, under a LIVE INPUT or UPNP label, with the
  application's icon when there is no cover. The duration reads LIVE, next and previous are off for a source the
  player does not queue, and a controller's mute shows as a MUTED badge that unmutes when clicked.
- **FFmpeg from Settings.** *Settings › FFmpeg › Download and build FFmpeg* downloads FFmpeg 9.0.1's source
  (checked against its SHA-256), builds the LGPL libraries with MSYS2 (the installed one, or a private copy it
  sets up, also pinned), installs them in `%LOCALAPPDATA%\FUPlayer\ffmpeg` and loads them without a restart;
  the library then picks up the files it passed over ([docs](docs/building-ffmpeg.md)). No administrator rights
  are needed. `fuplayer-cli ffmpeg-install` does the same from a prompt.
- **Nuttall-windowed sinc filters**: Nuttall's four-term window with smooth ends in compact, balanced and steep
  versions, and the Blackman–Nuttall window, each in linear and minimum phase. About 110 dB at the edge of the
  stopband for about 7 % more taps than Kaiser at that level, and the Nuttall window's stopband keeps falling:
  142 / 160 / 178 dB four, eight and sixteen transition widths further out, where Kaiser's is at 131 / 136 / 142
  ([docs](docs/dsp.md#filter-design)).
- A note on trainable windows: where the idea comes from, and what it would and would not do for the player
  ([filter guide](docs/dsp.md)).

### Changed

- The player needs Windows 10 version 1809 or later, for the media sessions it reads.
- Ctrl+1 to Ctrl+8 open the pages in the rail's order, counting top-level pages only.
- The DSP studio plots a Nuttall filter's magnitude further down, where its stopband goes on falling.

## 0.2.2 — 2026-09-25

### Changed

- **A new neural restorer model.** Fitted from scratch to 634 high-resolution masters of every genre, screened so
  that every reference really carries music to 22.05 kHz, at both delivery rates. Against the model in 0.2.1, on
  the same 1,952 held-out stretches: log-spectral distance above 12 kHz 11.3 → 7.8 dB, the mid's and the side's
  band levels 2.0 → 0.9 and 3.0 → 1.2 dB from the master, the level above 16 kHz unchanged at 0.9 dB under it. A
  lossless file forced through it changes a little more, −65.8 → −62.0 dB, and frames with a band over the
  masking threshold go from 0.45 to 0.61 % ([docs](docs/neural-restorer.md)).
- The overload warning names the convolution mode when a long filter is running tap by tap, which is the largest
  lever there is.
- The Upscaled band level choices say which band they move: the one above the source's Nyquist frequency, and
  nothing below it.
- The live input page says what format the captured application arrives in, which Windows decides, and opens the
  setting that changes it; it names the collision when the application also plays directly to the output device,
  and opens the Windows pages that separate the two.
- The DSP studio says which way Automatic read the playing source ("VORBIS, read as lossy").
- A long filter running tap by tap on the graphics device keeps its history on the device, and sends it only the
  new samples of each block.
- With the neural upscaler on, the 2× interpolator in front of the network runs 5.4 times faster (8.6 → 1.6 ms
  per second of audio, per channel), and finding where the source's spectrum ends costs a tenth of what it did
  (34 → 3.5 ms per second of audio on the engine thread): it was worked out again after every block, a hundred
  times a second, where the spectrum changes twenty-three times.
- The limiter costs 40 % less at high output rates (12.7 → 7.5 ms per second of audio per channel at 705.6 kHz),
  its output unchanged bit for bit.

### Fixed

- Dark horizontal lines across the restored band, the same bins on every track: the 0.2.1 model wrote fourteen
  bins 2 to 5 dB under their neighbours at 48 kHz, among them 869, 881 and 894, on every input. None now at
  either rate, and the training scripts measure it (`restorer/bin_lines.py`).
- The spectrogram and waterfall drew the low octaves as blocks at short transform lengths.
- A model placed in `%APPDATA%\FUPlayer\models` under the shipped model's own name was ignored in favour of the one
  beside the program. The newest file of a name is the one used now, as the documentation always said.
- Tap by tap on a graphics device: after the device had been let go and taken back, which the check of whether it
  makes the music quicker does, it was given phases again before the input it convolves against had been
  refilled, and a few blocks came out wrong, at 44.1 kHz as loud as the music itself. It now waits for a whole
  filter's worth of samples.
- The limiter could lose the loudest sample of its look-ahead window when the level had been falling for longer
  than the window, and steer its gain by a quieter one. It takes a smooth fall, a low tone at a high rate; the
  music it was measured on never produced one.
- Changing how many of the phases the graphics device is given closed and reopened the audio device, which on an
  ASIO interface relocks the DAC. It now rebuilds the processing only.
- Now playing could, in a narrow window, read the audio device's format while the engine was closing it, and crash
  the player. Found by reading the code, not in use.
- The DSP studio's plots kept their progress bar running, with nothing said, when a filter could not be designed
  for them; they now say why.
- A damaged AIFF header is reported as damage, so FFmpeg is given the file, rather than failing with an internal
  error.
- A WAV render with an odd number of data bytes (24-bit mono, an odd number of frames) ends with the pad byte RIFF
  requires.

## 0.2.1 — 2026-09-18

### Added

- **Neural restorer** (DSP Studio → Lossy repair): coded 44.1 and 48 kHz stereo (Opus, AAC, MP3, Vorbis at 96 to
  160 kbit/s) back towards the lossless recording at the same rate, in 91 ms at 48 kHz and 99 ms at 44.1. A causal
  STFT network on mid and side with a 6-frame look-ahead: it rewrites the band above the codec's low-pass, restores
  the side channel the codec collapsed, and corrects spectral holes ([docs](docs/neural-restorer.md)).
- The restorer and the upscaler **chain**: with both on, the restorer runs first and the upscaler reads its output
  as lossless. Source type and Output delta cover both.
- **Both models ship** in the package's `models` folder.
- `--neural-restore` and `--restorer` in `fuplayer-cli`; `models` lists restorers and upscalers.
- `FUPlayer.exe --capture <pid>` starts a capture from the command line.
- `asio.log` and `player.log` in the settings folder record the ASIO driver's life and the engine's errors.

### Fixed

- Live input into a device that also plays the captured application directly (RME ASIO drivers do at a matching
  rate) was heard twice. The capture now mutes the application's own playback devices, not its session, so the
  capture keeps every sample, and unmutes them when it ends or on the next start after a crash. A switch on the
  live input page turns this off.
- The ASIO driver's callback table and buffers now outlive the driver, and nothing thrown in a driver callback
  leaves it: a fail-fast crash during live input to DSD256 was not reproduced, and these are the causes that could
  be closed.
- The graphics-device filter path leaked an OpenCL event every block.

### Changed

- The release package lists every third-party component it contains and carries each license and notice text in
  `licenses/`.

## 0.2.0 — 2026-09-17

### Added

- **Neural upscaler** (DSP Studio → Lossy repair): 44.1 and 48 kHz PCM to 88.2 and 96 kHz through an STFT-domain
  ConvNeXt network on ONNX Runtime (CPU, about 0.55 s of latency). It corrects codec artefacts below the cutoff
  and synthesises the band above it and above the source Nyquist. No model ships: train one from your own
  high-resolution files with `training/neural-upscaler` ([docs](docs/neural-upscaler.md)).
- **Source type** (Automatic, Lossy, Lossless), **Upscaled band level** (−6 to +6 dB) and **Output delta**
  (the upscaler's contribution alone).
- **Application capture**: send another application's audio through the whole chain (Windows process loopback).
- `FUPlayer.exe --diagnostics`; `--neural-upscale`, `--upscaler`, `--source-type`, `--upscaler-level`,
  `--output-delta` and `models` in `fuplayer-cli`.

### Fixed

- Lossy files and captures are always peak-limited, so decoder overshoots no longer clip into noise above 40 kHz
  or destabilise a DSD modulator. The limiter also runs whenever the upscaler does.
- Processed-output spectrum, spectrogram and waterfall at DSD and high PCM rates: the signal is decimated to the
  displayed band and analysed at the source's frequency resolution, so the low octaves no longer render as blocks.
- Ogg files that failed to open without a message, and the LGPL FFmpeg build script.

### Changed

- Settings files from 0.1.0 are migrated automatically.

## 0.1.0 — 2026-09-14

First release: PCM and DSD output up to DSD1024, long linear- and minimum-phase filters with optional GPU
offload, WASAPI and ASIO back-ends, native FLAC, WAV, AIFF, DSF and DFF decoding, the library, the analyzer and
`fuplayer-cli`.
