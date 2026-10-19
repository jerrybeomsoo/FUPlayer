# UPnP input

FUPlayer can act as a UPnP/DLNA renderer. Another application on your LAN chooses music and sends it to
FUPlayer, which applies the selected processing and plays it through the selected local output device.
It is a renderer, rather than a built-in streaming-service browser.

Enable UPnP input in the player, then select FUPlayer as the output renderer in a compatible controller.
Both devices need to be on a LAN where discovery traffic is allowed. The default renderer request port
is TCP 58180, separate from Network DSP's UDP ports. The controller must provide an accessible audio URL
in a format FUPlayer can decode; some formats require [FFmpeg](building-ffmpeg.md).

For example, the command-line renderer can run as:

```powershell
.\fuplayer-cli.exe upnp --name "FUPlayer" --backend wasapi-shared
```

Use `--local-only` to accept control only from this computer, or `--port` to select another request port.
Discovery and format support depend on the controller. This does not add Widevine, a service login,
or direct integration with Spotify, Apple Music or YouTube Music. Windows application audio capture
is a separate option for applications already playing on the same computer.
