#!/usr/bin/env bash
#
# Prepares LGPL-only FFmpeg 8.1 shared libraries for distribution, for macOS (universal
# arm64+x86_64), Windows (x64), and Linux (x64). Platforms that are already staged in the output
# directory are skipped, so this is cheap to re-run (and `./scripts/run.sh package` runs it
# automatically).
#
# Why LGPL: TUFReplay loads FFmpeg as separate, replaceable shared libraries at runtime and never
# statically links it, which satisfies LGPL section 6. Building FFmpeg WITHOUT --enable-gpl (so no
# libx264/libx265/libvvenc) keeps it LGPL, so it does not impose its license on the rest of
# TUFReplay. Do NOT swap in a GPL build unless you intend to license all of TUFReplay under the GPL.
#
# Hardware encoders remain available (VideoToolbox on macOS; NVENC/AMF/QSV on Windows), which is
# what the renderer uses in practice. The software x264/x265 fallback is intentionally absent.
#
# Usage:
#   ./scripts/tasks/prepare-lgpl-ffmpeg.sh [output-dir]
# then:
#   TUFREPLAY_FFMPEG_OSX_DIR=<out>/osx TUFREPLAY_FFMPEG_WIN_X64_DIR=<out>/win-x64 \
#     ./scripts/run.sh package
#
set -euo pipefail

FFMPEG_TAG="n8.1"
WIN_ASSET="ffmpeg-${FFMPEG_TAG}-latest-win64-lgpl-shared-8.1.zip"
WIN_URL="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/${WIN_ASSET}"
LINUX_ASSET="ffmpeg-${FFMPEG_TAG}-latest-linux64-lgpl-shared-8.1.tar.xz"
LINUX_URL="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/${LINUX_ASSET}"
FFMPEG_SRC_URL="https://github.com/FFmpeg/FFmpeg.git"

# Expected SONAME majors for FFmpeg.AutoGen 8.1.0. A mismatch means the AutoGen binding version and
# the FFmpeg build have diverged and the first native call would throw.
OSX_LIBS=(libavcodec.62 libavformat.62 libavutil.60 libswresample.6 libswscale.9)
LINUX_CORE_LIBS=(libavcodec.so.62 libavformat.so.62 libavutil.so.60 libswresample.so.6 libswscale.so.9)

OUT_DIR="${1:-$(pwd)/build/lgpl-ffmpeg}"
WORK_DIR="$OUT_DIR/.work"
mkdir -p "$OUT_DIR/osx" "$OUT_DIR/win-x64" "$OUT_DIR/linux-x64" "$WORK_DIR"

log() { printf '==> %s\n' "$1"; }

fetch_windows() {
  if [ -f "$OUT_DIR/win-x64/avcodec-62.dll" ]; then
    log "Windows DLLs already staged; skipping."
    return
  fi
  log "Fetching LGPL Windows FFmpeg ($WIN_ASSET)"
  local zip="$WORK_DIR/$WIN_ASSET"
  curl -fsSL -o "$zip" "$WIN_URL"
  rm -rf "$WORK_DIR/win"
  mkdir -p "$WORK_DIR/win"
  unzip -oq "$zip" -d "$WORK_DIR/win"

  local bin
  bin="$(dirname "$(find "$WORK_DIR/win" -name 'avcodec-62.dll' | head -1)")"
  [ -n "$bin" ] || { echo "avcodec-62.dll not found in Windows build" >&2; exit 1; }

  # Guard against a GPL build slipping in.
  if ls "$bin"/*x264* "$bin"/*x265* >/dev/null 2>&1; then
    echo "Windows build contains GPL libraries (x264/x265); wrong asset." >&2
    exit 1
  fi
  cp "$bin"/*.dll "$OUT_DIR/win-x64/"
  log "Windows LGPL DLLs staged to $OUT_DIR/win-x64"
}

fetch_linux() {
  if [ -f "$OUT_DIR/linux-x64/libavcodec.so.62" ]; then
    log "Linux shared objects already staged; skipping."
    return
  fi
  log "Fetching LGPL Linux FFmpeg ($LINUX_ASSET)"
  local tarball="$WORK_DIR/$LINUX_ASSET"
  curl -fsSL -o "$tarball" "$LINUX_URL"
  rm -rf "$WORK_DIR/linux"
  mkdir -p "$WORK_DIR/linux"
  tar -xJf "$tarball" -C "$WORK_DIR/linux"

  local lib
  lib="$(dirname "$(find "$WORK_DIR/linux" -name 'libavcodec.so.62*' -type f | head -1)")"
  [ -n "$lib" ] || { echo "libavcodec.so.62 not found in Linux build" >&2; exit 1; }

  # Guard against a GPL build slipping in.
  if ls "$lib"/*x264* "$lib"/*x265* >/dev/null 2>&1; then
    echo "Linux build contains GPL libraries (x264/x265); wrong asset." >&2
    exit 1
  fi

  # Stage one real file per library, named by its SONAME (the name dlopen uses). The tarball ships
  # name -> SONAME -> full-version symlink chains; zip archives don't round-trip symlinks reliably,
  # so dereference instead.
  local soname
  find "$lib" -maxdepth 1 -name '*.so.*' | grep -E '\.so\.[0-9]+$' | while read -r soname; do
    cp -L "$soname" "$OUT_DIR/linux-x64/$(basename "$soname")"
  done

  local core
  for core in "${LINUX_CORE_LIBS[@]}"; do
    [ -f "$OUT_DIR/linux-x64/$core" ] || { echo "$core missing after staging" >&2; exit 1; }
  done
  log "Linux LGPL shared objects staged to $OUT_DIR/linux-x64"
}

build_macos() {
  if [ "$(uname -s)" != "Darwin" ]; then
    log "Not on macOS; skipping the macOS build. Run this on a Mac to produce the dylibs."
    return
  fi
  if [ -f "$OUT_DIR/osx/libavcodec.62.dylib" ]; then
    log "macOS dylibs already staged; skipping."
    return
  fi
  command -v nasm >/dev/null 2>&1 || { echo "nasm is required (brew install nasm)." >&2; exit 1; }

  local src="$WORK_DIR/FFmpeg"
  if [ ! -d "$src" ]; then
    log "Cloning FFmpeg $FFMPEG_TAG source"
    git clone --depth 1 --branch "$FFMPEG_TAG" "$FFMPEG_SRC_URL" "$src"
  fi

  build_arch() {
    local arch="$1"
    local dir="$WORK_DIR/mac-$arch"
    log "Building macOS FFmpeg ($arch)"
    rm -rf "$dir"; mkdir -p "$dir"
    (
      cd "$dir"
      # --disable-xlib/--disable-libxcb/--disable-sdl2: autodetect would link Homebrew X11/SDL
      # dylibs from the build machine, which do not exist on user machines and would make dlopen
      # fail there.
      "$src/configure" \
        --prefix="$dir/install" \
        --enable-shared --disable-static \
        --disable-gpl --disable-nonfree \
        --disable-programs --disable-doc \
        --enable-videotoolbox \
        --disable-xlib --disable-libxcb --disable-sdl2 \
        --arch="$arch" --cc="clang -arch $arch" \
        --enable-cross-compile --target-os=darwin >/dev/null
      make -j"$(sysctl -n hw.ncpu)" >/dev/null
      make install >/dev/null
    )
  }
  build_arch arm64
  build_arch x86_64

  log "Combining into universal dylibs and rewriting load paths"
  for major in "${OSX_LIBS[@]}"; do
    local a x out
    a="$(ls "$WORK_DIR/mac-arm64/install/lib/${major%.*}."*.dylib | grep -E '\.[0-9]+\.[0-9]+\.dylib$' | head -1)"
    x="$(ls "$WORK_DIR/mac-x86_64/install/lib/${major%.*}."*.dylib | grep -E '\.[0-9]+\.[0-9]+\.dylib$' | head -1)"
    out="$OUT_DIR/osx/$major.dylib"
    lipo -create "$a" "$x" -output "$out"
    # The install name and inter-library dependencies must resolve relative to the dylib's own
    # folder on the user's machine, not the build prefix.
    install_name_tool -id "@loader_path/$major.dylib" "$out"
    # `|| true`: libavutil has no inter-library dependencies, and a no-match grep exit would
    # otherwise kill the script under `set -euo pipefail`.
    otool -L "$out" | awk 'NR>1{print $1}' | { grep "/install/lib/" || true; } | while read -r dep; do
      install_name_tool -change "$dep" "@loader_path/$(basename "$dep")" "$out"
    done
    codesign --force --sign - "$out" >/dev/null 2>&1 || true
  done
  log "macOS universal LGPL dylibs staged to $OUT_DIR/osx"
}

fetch_windows
fetch_linux
build_macos

cat <<EOF

Done. LGPL FFmpeg prepared under: $OUT_DIR

Package with `./scripts/run.sh package` (it picks these up from $OUT_DIR automatically).

FFmpeg source (for the LGPL written offer) is FFmpeg tag $FFMPEG_TAG at $FFMPEG_SRC_URL
EOF
