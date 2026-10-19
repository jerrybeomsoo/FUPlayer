# Optional FFmpeg decoders

FUPlayer reads FLAC, WAV, AIFF, DSF and DFF itself. Additional codecs, including MP3, AAC/ALAC, Ogg and Opus,
use separately installed FFmpeg libraries. The player ZIP does not contain those libraries.

## Install from Settings

Open **Settings → FFmpeg** and start the build/install action. It downloads FFmpeg's source and the build
tools it needs, builds the LGPL configuration, and installs the libraries where FUPlayer looks for them.
This needs an Internet connection, disk space and several minutes; it does not require an administrator
installation. Follow its progress and restart/rescan when requested so previously skipped files are found.

From PowerShell in the player folder, the equivalent is:

```powershell
.\fuplayer-cli.exe ffmpeg-install
```

Use `fuplayer-cli.exe help` for optional build-tool and install directory overrides.
The default Windows x64 library location is `%LOCALAPPDATA%\FUPlayer\ffmpeg\win-x64`.

## Building from the repository

```powershell
pwsh .\build\ffmpeg\build-ffmpeg.ps1
```

The included configuration is FFmpeg 9.0.1 with LGPL-only options. Do not enable GPL, nonfree or version3
components when preparing FUPlayer distributions. Libraries must match the major versions FUPlayer loads:
avformat 63, avcodec 63, avutil 61 and swresample 7.
See [third-party notices](../THIRD-PARTY-NOTICES.md) for licensing and source obligations if you redistribute
a build containing FFmpeg. The normal release script leaves these libraries out.
