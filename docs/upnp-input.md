# UPnP input: playing from foobar2000 and other controllers

FUPlayer can be a **UPnP renderer** (a DLNA "digital media renderer"): a device that foobar2000's UPnP
output, phone apps such as BubbleUPnP, and other UPnP AV or DLNA controllers find on the network and play
to. What a controller sends goes through the whole processing chain, like a file, to whichever output
device the player is set to.

It is off until asked for. Turn it on under **Live input › UPnP input** in the navigation rail.

## The page

- **Offer this player as a UPnP renderer** starts it. The player then answers discovery (SSDP), serves
  its device and service descriptions, and takes AVTransport, RenderingControl and ConnectionManager
  commands over HTTP, by default on port 58180.
- **Name** is what controllers list it as, `FUPlayer (<computer name>)` when left empty. The device keeps
  one identity from run to run, so a controller that remembers it finds it again.
- **Take orders from the local network**: other devices on the network see the player and may play to it.
  Off, it announces nothing and only controllers on the same computer can use it. Either way nothing
  outside the private address ranges is answered.
- **Follow the controller's volume**: the controller's volume slider and mute move the player's volume
  knob. Off, the knob stays where it is. A controller's mute shows as a MUTED badge beside the knob;
  clicking the badge unmutes, and the controller is told.
- **Receiving** shows what arrives: from which controller, the track, the container and format, and how
  far ahead of the output the stream is buffered.
- **Activity** lists what controllers asked for, newest first; `upnp.log` in the settings folder has
  the full history.

The first time the renderer starts, Windows may ask whether to let FUPlayer through its firewall. A
controller on the same computer needs nothing; private networks are enough for one elsewhere.

## foobar2000

1. Turn the renderer on.
2. In foobar2000: *File › Preferences › Playback › Output*, and choose **FUPlayer (…)** as the device.
   It is listed with the other output devices once foobar2000 has seen the player announce itself.
3. Play. The stream starts after about a second of buffering, plus the processing chain's own delay.

Now playing then shows the track foobar2000 is playing, its artist and its cover, taken from the media
session foobar2000 publishes to Windows (the stream itself only says "foobar2000 audio stream").

### FLAC and 24 bits

Out of the box foobar2000 treats a renderer it has never heard of cautiously: it sends **16-bit WAV**,
and pausing stops the stream. Two settings lift that:

- **The renderer list.** foobar2000 keeps what it knows about each make of renderer in
  `foo_out_upnp-config-v2.txt` in its profile folder (`%APPDATA%\foobar2000-v2`, or `profile` beside a
  portable installation). *Add to foobar2000* on the UPnP input page appends an entry for FUPlayer, which
  says it takes an endless FLAC stream, chunked, at up to 24 bits, and pauses:

  ```text
  # FUPlayer: added from its UPnP input page. It pauses, and plays an endless FLAC stream at up to 24 bits.
  manufacturer=FUPlayer contributors
  model=FUPlayer
  supports-pause=true
  supports-FLAC=true
  supports-infinite-length=true
  supports-chunked=true
  bitdepth-max=24
  preferred-format=FLAC
  ```

  foobar2000 reads the file when it starts, so restart it afterwards.
- **The bit depth.** foobar2000 still sends 16 bits to a renderer until told otherwise: under
  *Preferences › Playback › Output › Devices*, set **Bits** to 24 on FUPlayer's row.

With both, a 24-bit file arrives as 24-bit FLAC. The *Receiving* card and Now playing show the format.

## Other controllers

Any UPnP AV / DLNA control point can play to the player. It takes **FLAC**, **WAV** (including RF64 and
streams of unknown length) and **LPCM** (`audio/L16`, `audio/L24`), over `http` only: a controller
that asks for anything else, or for a `file:` address, gets an error it can show. MP3, AAC and other
compressed formats are left to the controller, most of which transcode or pass FLAC on as it is.

Seeking, the next track and gapless changes are the controller's business: a controller streaming
continuously (as foobar2000 does) sends new metadata on the same stream, and one that queues the next
file with *SetNextAVTransportURI* has it played when the current one ends.

## From the command line

```powershell
fuplayer-cli upnp --name "Living room" --backend asio
```

runs the same renderer without the window until Ctrl+C, printing what arrives each second.
`--local-only` takes orders from this computer only, `--port` picks the TCP port, and with
`--backend file --out <folder>` each stream is written to a file instead of played.
