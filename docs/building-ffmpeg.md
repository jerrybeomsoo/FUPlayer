# Building the LGPL FFmpeg libraries

FUPlayer decodes WAV, AIFF, FLAC, DSF and DSDIFF with its own managed code. Everything else
(MP3, AAC/ALAC in M4A, WavPack, Monkey's Audio, Ogg Vorbis, Opus, TTA, TAK, WMA, DST-compressed
DSDIFF …) is decoded through FFmpeg when its shared libraries are present. Without them the app still
runs, and opening one of those files reports that the libraries are missing rather than playing it.

FUPlayer's bindings match **FFmpeg 9.0.x** exactly (libavformat 63, libavcodec 63, libavutil 61).
The struct offsets were verified against the 9.0.1 headers in this repository; other major versions are
rejected at start-up.

## Windows

From an ordinary PowerShell prompt at the top of the repository:

```powershell
pwsh build/ffmpeg/build-ffmpeg.ps1
```

That script does the whole job:

1. Finds MSYS2, or installs it with `winget install MSYS2.MSYS2` if it is not there.
2. Installs `mingw-w64-ucrt-x86_64-gcc`, `make`, `nasm`, `diffutils` and `pkgconf` into MSYS2's
   UCRT64 environment.
3. Runs `build-ffmpeg-lgpl.sh` in that environment.
4. Copies the libraries to `native/win-x64/`, where the solution finds them.

The build takes a few minutes. `-SkipToolchainInstall` runs the build without touching MSYS2,
`-Msys2Root` points at an installation somewhere unusual, and `-Jobs` sets how many compilers run
at once.

**Git Bash cannot build this.** It has bash but no C compiler, so `configure` ends in
`C compiler test failed` after eighty lines of feature tests. The shell script now checks for a
compiler first and says which shell to use instead. The plain MSYS shell is no good either: its gcc
builds against `msys-2.0.dll`, and a DLL that needs the MSYS runtime cannot be loaded into
FUPlayer. The script refuses that compiler rather than producing libraries that fail at run time.

### Doing it by hand

1. Install [MSYS2](https://www.msys2.org) and open the **MSYS2 UCRT64** shell.
2. Install the toolchain:

   ```bash
   pacman -S --needed mingw-w64-ucrt-x86_64-gcc make nasm diffutils
   ```

3. From the repository root, run:

   ```bash
   ./build/ffmpeg/build-ffmpeg-lgpl.sh
   ```

The full `base-devel` and `mingw-w64-ucrt-x86_64-toolchain` groups work as well; the five packages
above are simply all that this build needs.

### Where the libraries go

`build-ffmpeg-lgpl.sh` installs into `build/ffmpeg/out/win-x64/` and then copies these files into
`native/win-x64/` at the top of the repository:

- `avformat-63.dll`
- `avcodec-63.dll`
- `avutil-61.dll`
- `swresample-7.dll`

FUPlayer loads the first three by name. `swresample` is there because FFmpeg's Opus decoder depends
on it, and `avcodec` loads it from its own folder, so it has to travel with them.

`Directory.Build.targets` copies anything in `native/<rid>/` into an `ffmpeg` folder next to
`FUPlayer.exe` on every build and every publish, so building the solution afterwards is all it takes.
`dotnet run --project src/FUPlayer.Cli -- info` prints the loaded version, and so does the Settings
page in the player.

At run time the libraries are looked for next to the executable, in `ffmpeg/` beside it, in
`native/<rid>/` beside it, and in the directory named by the `FUPLAYER_FFMPEG_PATH` environment
variable, in that order, before the system library path. Set `FuPlayerFFmpegDirectory` to build
against a copy kept somewhere else, or `FuPlayerSkipFFmpegCopy=true` to leave them out of a build.

### Using the MSVC toolchain instead

Open an "x64 Native Tools Command Prompt for VS 2022", start the MSYS2 shell from it with
`msys2_shell.cmd -use-full-path -ucrt64`, and add `--toolchain=msvc` to the `configure` line in the script.
The resulting DLL names are the same.

## Linux and macOS

Run the same script from a normal shell after installing a C toolchain and `nasm`
(`sudo apt install build-essential nasm`, or `brew install nasm`). The libraries end up in
`build/ffmpeg/out/<rid>/lib` and are copied to `native/<rid>/` in the same way.

## What is enabled

The script configures FFmpeg with `--disable-everything` and enables only the audio demuxers, decoders
and parsers FUPlayer needs, with no external libraries (`--disable-autodetect`). Ogg files are covered
by the `ogg` demuxer with the `vorbis` and `opus` decoders; nothing links against libvorbis or libopus.

After configure it checks two things and stops if either is wrong. The license in `config.h` has to
read **"LGPL version 2.1 or later"**, and every demuxer, decoder and parser on the list has to be in
`config.h` as well. The second check exists because configure does not fail when a component's
dependencies are missing: it prints one `Disabled …` line among several thousand and builds the rest.
That is how `opus_decoder` was left out for a while, since it needs libswresample, which this build
used to switch off.

Without `nasm` the build still works. It warns, passes `--disable-x86asm`, and the decoders run
slower for want of their hand-written assembly.

There is no `ffmpeg.exe` at the end of this: `--disable-programs` builds the libraries only, since
that is all FUPlayer calls. The `ffmpeg` command that `restoration.md` mentions, which widens the
training set with more codecs, is a separate thing to install if you want it.

## Licensing checklist when redistributing

- Never add `--enable-gpl`, `--enable-nonfree` or `--enable-version3`.
- Ship `ffmpeg-9.0.1/COPYING.LGPLv2.1` and `THIRD-PARTY-NOTICES.md` with the binaries.
- Provide the exact FFmpeg source used (the `ffmpeg-9.0.1` tree the libraries were built from) or a written offer.
- Keep the libraries as separate DLL / shared-object files so users can replace them.
