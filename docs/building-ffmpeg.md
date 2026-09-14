# Building the LGPL FFmpeg libraries

FUPlayer decodes WAV, AIFF, FLAC, DSF and DSDIFF with its own managed code. Everything else
(MP3, AAC/ALAC in M4A, WavPack, Monkey's Audio, Ogg Vorbis, Opus, TTA, TAK, WMA, DST-compressed
DSDIFF) is decoded through FFmpeg when its shared libraries are present. Without them the app still
runs; those formats are simply reported as unsupported.

FUPlayer's bindings match **FFmpeg 9.0.x** exactly (libavformat 63, libavcodec 63, libavutil 61).
The struct offsets were verified against the 9.0.1 headers in this repository; other major versions are
rejected at start-up.

## Windows (MSYS2, MinGW-w64 UCRT toolchain)

1. Install [MSYS2](https://www.msys2.org) and open the **MSYS2 UCRT64** shell.
2. Install the toolchain:

   ```bash
   pacman -S --needed base-devel mingw-w64-ucrt-x86_64-toolchain nasm
   ```

3. From the repository root, run:

   ```bash
   ./build/ffmpeg/build-ffmpeg-lgpl.sh
   ```

4. Copy these files from `build/ffmpeg/out/win-x64/bin/` next to `FUPlayer.exe`
   (or into an `ffmpeg` sub-folder beside it):

   - `avformat-63.dll`
   - `avcodec-63.dll`
   - `avutil-61.dll`

The script configures FFmpeg with `--disable-everything` and enables only the audio demuxers, decoders
and parsers FUPlayer needs, with no external libraries (`--disable-autodetect`). It checks that
`config.h` reports **"LGPL version 2.1 or later"** and aborts otherwise.

### Using the MSVC toolchain instead

Open an "x64 Native Tools Command Prompt for VS 2022", start the MSYS2 shell from it with
`msys2_shell.cmd -use-full-path -ucrt64`, and add `--toolchain=msvc` to the `configure` line in the script.
The resulting DLL names are the same.

## Linux and macOS

Run the same script from a normal shell after installing a C toolchain and `nasm`
(`sudo apt install build-essential nasm`, or `brew install nasm`). The libraries end up in
`build/ffmpeg/out/<rid>/lib`. FUPlayer looks for `libavformat.so.63` / `libavformat.63.dylib`
next to the application, in an `ffmpeg` sub-folder, in the directory named by the `FUPLAYER_FFMPEG_PATH`
environment variable, and finally on the system library path.

## Licensing checklist when redistributing

- Never add `--enable-gpl`, `--enable-nonfree` or `--enable-version3`.
- Ship `COPYING.LGPLv2.1` from the FFmpeg source and `THIRD-PARTY-NOTICES.md` with the binaries.
- Provide the exact FFmpeg source used (the release tarball from ffmpeg.org) or a written offer.
- Keep the libraries as separate DLL / shared-object files so users can replace them.
