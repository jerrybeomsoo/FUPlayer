#!/usr/bin/env bash
# Builds LGPL-only shared FFmpeg libraries (libavformat, libavcodec, libavutil, libswresample) for FUPlayer
# from the FFmpeg 9.0.1 source tree in the repository root.
#
# Windows: this needs a MinGW-w64 compiler. Git Bash does not have one, so running the script
# there stops at "C compiler test failed". Either let the wrapper do the whole job from an
# ordinary PowerShell prompt:
#     pwsh build/ffmpeg/build-ffmpeg.ps1
# which installs MSYS2 and the compiler if they are missing and then calls this script, or open
# the MSYS2 UCRT64 shell yourself and run:
#     pacman -S --needed mingw-w64-ucrt-x86_64-gcc make nasm diffutils
#     ./build/ffmpeg/build-ffmpeg-lgpl.sh
# Linux/macOS: run in a normal shell with a C toolchain and nasm installed.
#
# Result: build/ffmpeg/out/<rid>/bin (Windows DLLs) or build/ffmpeg/out/<rid>/lib, copied at the
# end into native/<rid>/ at the top of the repository, which is where the build picks them up.
#
# LICENSING: never add --enable-gpl, --enable-nonfree or --enable-version3 here.
# FUPlayer may only redistribute the LGPL v2.1+ configuration of FFmpeg.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
FFMPEG_SRC="${FFMPEG_SRC:-$REPO_ROOT/ffmpeg-9.0.1}"
JOBS="${JOBS:-$(nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4)}"

die() { echo "$@" >&2; exit 1; }

if [[ ! -x "$FFMPEG_SRC/configure" ]]; then
  die "No FFmpeg source at $FFMPEG_SRC.

Download FFmpeg 9.0.1 and unpack it there, or point FFMPEG_SRC at a copy you already have:

    curl -LO https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz
    tar xf ffmpeg-9.0.1.tar.xz

FUPlayer's bindings are written against 9.0.x and reject other major versions at start-up."
fi

EXTRA_ARGS=()
WINDOWS=0
case "$(uname -s)" in
  MINGW*|MSYS*|UCRT*|CYGWIN*)
    WINDOWS=1
    RID="win-x64"
    EXTRA_ARGS+=(--target-os=mingw32 --arch=x86_64 --extra-ldflags=-static-libgcc)
    # FFmpeg uses Win32 threads here, but gcc still reaches into libwinpthread for clock_gettime and
    # nanosleep, and that DLL is part of MSYS2 rather than of Windows. Linked statically the libraries
    # need nothing but Windows itself, which is the point: a user drops the files next to the player
    # and they work.
    EXTRA_ARGS+=(--extra-libs="-Wl,-Bstatic -lwinpthread -Wl,-Bdynamic")
    ;;
  Linux*)
    RID="linux-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/')"
    ;;
  Darwin*)
    RID="osx-$(uname -m | sed 's/x86_64/x64/')"
    ;;
  *)
    die "Unsupported build host: $(uname -s)"
    ;;
esac

# Find the compiler before configure does, because configure's own failure is eighty lines of
# feature tests ending in "C compiler test failed", which says nothing about what to install.
CC="${CC:-}"
if [[ -z "$CC" ]]; then
  for candidate in gcc cc clang; do
    if command -v "$candidate" >/dev/null 2>&1; then
      CC="$candidate"
      break
    fi
  done
fi

if [[ -z "$CC" ]]; then
  if (( WINDOWS )); then
    die "No C compiler on PATH.

This shell has no gcc. Git Bash and the plain MSYS shell never do; the compiler lives in the
MSYS2 UCRT64 environment. From PowerShell, run:

    pwsh build/ffmpeg/build-ffmpeg.ps1

which installs MSYS2 and the compiler if they are missing, then builds. To do it by hand, open
the MSYS2 UCRT64 shell and run:

    pacman -S --needed mingw-w64-ucrt-x86_64-gcc make nasm diffutils
    ./build/ffmpeg/build-ffmpeg-lgpl.sh"
  else
    die "No C compiler on PATH. Install one (apt install build-essential, or xcode-select --install)."
  fi
fi

# On Windows the compiler also has to be the MinGW one. The MSYS and Cygwin compilers build
# against msys-2.0.dll or cygwin1.dll, and the resulting DLLs cannot be loaded by a .NET process
# that is not itself an MSYS program.
if (( WINDOWS )); then
  TRIPLE="$("$CC" -dumpmachine 2>/dev/null || echo unknown)"
  case "$TRIPLE" in
    *mingw*) ;;
    *msys*|*cygwin*)
      die "$CC targets $TRIPLE, which links against the MSYS runtime and cannot be loaded by FUPlayer.
Open the MSYS2 UCRT64 shell (not the MSYS one) and run the script again, or use
pwsh build/ffmpeg/build-ffmpeg.ps1."
      ;;
    *)
      echo "Warning: $CC reports target '$TRIPLE', which is not a MinGW target. Continuing anyway." >&2
      ;;
  esac
fi

command -v make >/dev/null 2>&1 || die "make is not on PATH. On MSYS2: pacman -S make."

# nasm is only needed for the hand-written x86 assembly. Without it FFmpeg still builds, just
# slower, so this is a warning and a configure flag rather than an error.
if ! command -v nasm >/dev/null 2>&1 && ! command -v yasm >/dev/null 2>&1; then
  echo "Warning: no nasm or yasm on PATH, so the x86 assembly is left out and decoding is slower." >&2
  echo "         On MSYS2: pacman -S nasm." >&2
  EXTRA_ARGS+=(--disable-x86asm)
fi

WORK_DIR="$SCRIPT_DIR/work/$RID"
OUT_DIR="$SCRIPT_DIR/out/$RID"
mkdir -p "$WORK_DIR" "$OUT_DIR"
cd "$WORK_DIR"

# Audio-only feature set. Uncompressed DSF/DSDIFF, WAV, AIFF and FLAC are decoded by FUPlayer itself,
# but their FFmpeg counterparts are kept as a fallback.
#
# libswresample stays in even though FUPlayer converts samples itself, because FFmpeg's Opus decoder
# depends on it. Disabling it does not fail the build: configure quietly drops opus_decoder with one
# warning in the middle of a thousand lines, and Ogg Opus files then report "no decoder" at run time.
DEMUXERS="aac,ac3,aiff,ape,asf,caf,dsf,dts,eac3,flac,iff,matroska,mov,mp3,mpc,mpc8,ogg,tak,truehd,tta,w64,wav,wv"
DECODERS="aac,aac_latm,ac3,alac,ape,dca,dsd_lsbf,dsd_lsbf_planar,dsd_msbf,dsd_msbf_planar,dst,eac3,flac,mlp"
DECODERS+=",mp1float,mp2float,mp3,mp3float,mpc7,mpc8,opus,tak,truehd,tta,vorbis,wavpack"
DECODERS+=",wmalossless,wmapro,wmav1,wmav2"
DECODERS+=",pcm_s8,pcm_u8,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be,pcm_f32le,pcm_f32be,pcm_f64le,pcm_f64be"
PARSERS="aac,aac_latm,ac3,dca,flac,mlp,mpegaudio,opus,tak,vorbis"

CC="$CC" "$FFMPEG_SRC/configure" \
  --prefix="$OUT_DIR" \
  --cc="$CC" \
  "${EXTRA_ARGS[@]}" \
  --enable-shared --disable-static \
  --disable-programs --disable-doc --disable-debug \
  --disable-avdevice --disable-avfilter --disable-swscale \
  --disable-network --disable-autodetect \
  --disable-everything \
  --enable-protocol=file \
  --enable-demuxer="$DEMUXERS" \
  --enable-decoder="$DECODERS" \
  --enable-parser="$PARSERS"

if ! grep -q 'FFMPEG_LICENSE "LGPL version 2.1 or later"' config.h; then
  echo "Refusing to continue: configured FFmpeg license is not LGPL v2.1+." >&2
  grep FFMPEG_LICENSE config.h >&2 || true
  exit 1
fi

# Configure drops any component whose dependencies are not met and carries on, saying so in one line
# of several thousand. That is how the Opus decoder went missing for a while. Check that every piece
# asked for above is really in the build rather than finding out when a file will not play.
missing=()
check_enabled() {
  local kind="$1"
  local names="$2"
  local name upper
  for name in ${names//,/ }; do
    upper="$(echo "${name}_${kind}" | tr '[:lower:]' '[:upper:]')"
    # Components are listed in config_components.h; config.h holds the library-wide switches.
    grep -q "^#define CONFIG_${upper} 1$" config_components.h || missing+=("$kind $name")
  done
}

check_enabled demuxer "$DEMUXERS"
check_enabled decoder "$DECODERS"
check_enabled parser "$PARSERS"

if (( ${#missing[@]} )); then
  echo "Refusing to continue: configure left these out of the build:" >&2
  printf '  %s
' "${missing[@]}" >&2
  echo "Search ffbuild/config.log for \"Disabled\" to see which dependency each one wanted." >&2
  exit 1
fi

make -j"$JOBS"
make install

# Anything the libraries import beyond Windows' own DLLs has to be shipped with them or they will not
# load on a machine that has no MSYS2. The MinGW runtime DLLs are all named libsomething-N.dll, and the
# MSYS and Cygwin ones msys-2.0.dll and cygwin1.dll, so a name test is enough to catch the lot.
if (( WINDOWS )) && command -v objdump >/dev/null 2>&1; then
  imports="$(objdump -p "$OUT_DIR"/bin/*.dll | sed -n 's/^[[:space:]]*DLL Name: //p' | sort -u)"
  foreign="$(echo "$imports" | grep -Ei '^(lib.*-[0-9]+\.dll|msys-.*\.dll|cygwin1\.dll)$' || true)"
  if [[ -n "$foreign" ]]; then
    echo "Refusing to continue: the libraries need runtime DLLs that are not part of Windows:" >&2
    echo "$foreign" | sed 's/^/  /' >&2
    echo "Link them statically or ship them alongside." >&2
    exit 1
  fi
fi

# Stage the libraries where the build expects them. Directory.Build.targets copies anything in
# native/<rid>/ into an ffmpeg folder next to FUPlayer.exe, which is one of the places
# FFmpegLibrary looks, so a build after this one picks them up with nothing else to set.
STAGE_DIR="$REPO_ROOT/native/$RID"
mkdir -p "$STAGE_DIR"
if (( WINDOWS )); then
  cp -f "$OUT_DIR"/bin/*.dll "$STAGE_DIR"/
else
  cp -Pf "$OUT_DIR"/lib/libav*.so.* "$OUT_DIR"/lib/libswresample*.so.* "$STAGE_DIR"/ 2>/dev/null || true
  cp -Pf "$OUT_DIR"/lib/libav*.dylib "$OUT_DIR"/lib/libswresample*.dylib "$STAGE_DIR"/ 2>/dev/null || true
fi

echo
echo "FFmpeg (LGPL v2.1+) installed to: $OUT_DIR"
echo "Staged for the build in:          $STAGE_DIR"
ls -1 "$STAGE_DIR"
echo
echo "Required by FUPlayer: avformat-63, avcodec-63, avutil-61, and swresample-7 next to them"
