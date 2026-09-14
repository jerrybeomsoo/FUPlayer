#!/usr/bin/env bash
# Builds LGPL-only shared FFmpeg libraries (libavformat, libavcodec, libavutil) for FUPlayer
# from the FFmpeg 9.0.1 source tree in the repository root.
#
# Windows: run from an MSYS2 "UCRT64" (or "MINGW64") shell:
#     pacman -S --needed base-devel mingw-w64-ucrt-x86_64-toolchain nasm
#     ./build/ffmpeg/build-ffmpeg-lgpl.sh
# Linux/macOS: run in a normal shell with a C toolchain and nasm installed.
#
# Result: build/ffmpeg/out/<rid>/bin (Windows DLLs) or build/ffmpeg/out/<rid>/lib.
# Copy avformat-63, avcodec-63 and avutil-61 next to FUPlayer.exe (or into native/<rid>/).
#
# LICENSING: never add --enable-gpl, --enable-nonfree or --enable-version3 here.
# FUPlayer may only redistribute the LGPL v2.1+ configuration of FFmpeg.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
FFMPEG_SRC="${FFMPEG_SRC:-$REPO_ROOT/ffmpeg-9.0.1}"
JOBS="${JOBS:-$(nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4)}"

if [[ ! -x "$FFMPEG_SRC/configure" ]]; then
  echo "FFmpeg source not found at $FFMPEG_SRC (set FFMPEG_SRC)." >&2
  exit 1
fi

EXTRA_ARGS=()
case "$(uname -s)" in
  MINGW*|MSYS*|UCRT*)
    RID="win-x64"
    EXTRA_ARGS+=(--target-os=mingw32 --arch=x86_64 --extra-ldflags=-static-libgcc)
    ;;
  Linux*)
    RID="linux-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/')"
    ;;
  Darwin*)
    RID="osx-$(uname -m | sed 's/x86_64/x64/')"
    ;;
  *)
    echo "Unsupported build host: $(uname -s)" >&2
    exit 1
    ;;
esac

WORK_DIR="$SCRIPT_DIR/work/$RID"
OUT_DIR="$SCRIPT_DIR/out/$RID"
mkdir -p "$WORK_DIR" "$OUT_DIR"
cd "$WORK_DIR"

# Audio-only feature set. Uncompressed DSF/DSDIFF, WAV, AIFF and FLAC are decoded by FUPlayer itself,
# but their FFmpeg counterparts are kept as a fallback.
DEMUXERS="aac,ac3,aiff,ape,asf,caf,dsf,dts,eac3,flac,iff,matroska,mov,mp3,mpc,mpc8,ogg,tak,truehd,tta,w64,wav,wv"
DECODERS="aac,aac_latm,ac3,alac,ape,dca,dsd_lsbf,dsd_lsbf_planar,dsd_msbf,dsd_msbf_planar,dst,eac3,flac,mlp"
DECODERS+=",mp1float,mp2float,mp3,mp3float,musepack7,musepack8,opus,tak,truehd,tta,vorbis,wavpack"
DECODERS+=",wmalossless,wmapro,wmav1,wmav2"
DECODERS+=",pcm_s8,pcm_u8,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be,pcm_f32le,pcm_f32be,pcm_f64le,pcm_f64be"
PARSERS="aac,aac_latm,ac3,dca,flac,mlp,mpegaudio,opus,tak,vorbis"

"$FFMPEG_SRC/configure" \
  --prefix="$OUT_DIR" \
  "${EXTRA_ARGS[@]}" \
  --enable-shared --disable-static \
  --disable-programs --disable-doc --disable-debug \
  --disable-avdevice --disable-avfilter --disable-swscale --disable-swresample \
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

make -j"$JOBS"
make install

echo
echo "FFmpeg (LGPL v2.1+) installed to: $OUT_DIR"
echo "Required by FUPlayer: avformat-63, avcodec-63, avutil-61"
