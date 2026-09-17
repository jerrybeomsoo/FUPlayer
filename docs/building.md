# Building FUPlayer

## Prerequisites

| | |
| --- | --- |
| .NET SDK | 9.0.300 or newer ([download](https://dotnet.microsoft.com/download/dotnet/9.0)) |
| OS | Windows 10 or 11, x64. The audio back-ends are Windows-only |
| IDE (optional) | Visual Studio 2022 17.14+, JetBrains Rider, or VS Code with the C# Dev Kit |

Nothing else is required. All package dependencies restore from NuGet.

> **Why .NET 9 and not 10?** Visual Studio 2022 resolves the .NET 9 SDK, which cannot target `net10.0`. The
> target framework is a single property, `FuPlayerTargetFramework` in `Directory.Build.props`. Change it to
> `net10.0` once the solution is only built with newer tooling. No code depends on it.

## Build and run

```bash
git clone https://github.com/jerrybeomsoo/FUPlayer.git
cd FUPlayer
dotnet build -c Release
```

```bash
# the player
dotnet run -c Release --project src/FUPlayer.App

# the command line tool
dotnet run -c Release --project src/FUPlayer.Cli -- devices
```

Build output lands in `src/FUPlayer.App/bin/Release/net9.0/FUPlayer.exe` and
`src/FUPlayer.Cli/bin/Release/net9.0/fuplayer-cli.exe`.

## Publishing a self-contained build

`build/package.ps1` publishes the player and the command line tool into `publish/FUPlayer`, drops the native
debug symbols that SkiaSharp and HarfBuzzSharp ship (about 100 MB of them), adds the licence files and writes
`artifacts/FUPlayer-<version>-win-x64.zip`:

```bash
pwsh build/package.ps1 -Version 0.2.0
```

The result runs on a machine with no .NET runtime installed and comes to roughly 47 MB zipped. Pass
`-FrameworkDependent` for a build that uses an installed .NET 9 Desktop Runtime instead, which is about a third
of the size. The same script runs in CI: pushing a tag such as `v0.2.0` makes `.github/workflows/release.yml`
build the package and attach it to a GitHub release, with that version's section of `CHANGELOG.md` as the
release notes.

For a single project without the packaging around it:

```bash
dotnet publish src/FUPlayer.App -c Release -r win-x64 --self-contained true -o publish/FUPlayer
```

Trimming is not supported: the settings layer uses reflection-based JSON serialization, and Avalonia's compiled
bindings still need the view models intact.

## Debug builds and the DSP library

`FUPlayer.Core` is compiled with optimisation in Debug as well as Release. Unoptimised, the DSP cannot keep up
with high DSD rates, and a debug session would only show you a player that stutters.

To step through DSP code:

```bash
dotnet build -p:FuPlayerDebugDsp=true
```

That turns optimisation off for `FUPlayer.Core` only. Expect real-time playback to fail while it is set.

## Optional: FFmpeg for extra formats

FLAC, WAV, AIFF, DSF and DFF are decoded by managed code in this repository. Everything else, including MP3,
AAC, ALAC, WavPack, Monkey's Audio, Ogg Vorbis, Opus, TTA, TAK and WMA, needs FFmpeg's shared libraries. Without
them the player runs normally and reports those formats as unsupported.

The bindings match **FFmpeg 9.0.x** exactly (libavformat 63, libavcodec 63, libavutil 61); other major versions
are rejected at start-up. On Windows one command builds them, installing MSYS2 and a compiler first if the
machine has neither:

```powershell
pwsh build/ffmpeg/build-ffmpeg.ps1
```

The libraries land in `native/win-x64/`, and `Directory.Build.targets` copies them into an `ffmpeg` folder
beside `FUPlayer.exe` on every build and publish, so the next build is all it takes. See
[building-ffmpeg.md](building-ffmpeg.md) for the manual route, for Linux and macOS, and for the licensing
checklist.

To place them by hand instead, put `avformat-63.dll`, `avcodec-63.dll`, `avutil-61.dll` and
`swresample-7.dll` next to `FUPlayer.exe`, in an `ffmpeg` sub-folder beside it, or in the folder named by
the `FUPLAYER_FFMPEG_PATH` environment variable. The Settings page reports whether they were found.

## Running the player against a throwaway profile

Useful when you do not want to disturb your real settings:

```bash
mkdir /tmp/fuplayer-test
echo '{"Output":{"BackendId":"null","Mode":"Dsd"}}' > /tmp/fuplayer-test/settings.json
FUPlayer.exe --settings-dir /tmp/fuplayer-test --page NowPlaying "some track.flac"
```

`--settings-dir` moves the settings, library index and queue somewhere else. `null` is a silent output back-end
that consumes the stream at real-time speed, which is handy for testing the engine without a DAC.

## Where settings live

`%APPDATA%\FUPlayer` holds `settings.json`, the library index, the saved queue and the cover thumbnail cache.
Deleting the folder resets the player. Settings files carry a version number and are migrated forward when the
layout changes.
